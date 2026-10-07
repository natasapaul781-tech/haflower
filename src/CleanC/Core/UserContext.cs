using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>
/// 「当前登录用户」的目录上下文（全部用户级路径的唯一真源）。
///
/// **为什么必须有它**：本程序用 <c>requireAdministrator</c> / 按需提权运行时，
/// 如果提权用的是**另一个管理员账户**（企业电脑上很常见：标准用户 + 独立管理员账户），
/// 那么 <c>Environment.GetFolderPath(LocalApplicationData)</c>、<c>UserProfile</c>、
/// <c>DesktopDirectory</c>、<c>Path.GetTempPath()</c>、<c>RegistryHive.CurrentUser</c>
/// 全都指向那个管理员账户——工具会去清管理员的缓存、把说明书写进管理员的桌面、
/// 日志写到管理员的 Profile，用户看到的现象是「点了清理但什么都没变」。
///
/// 解析顺序：
/// <list type="number">
///   <item>命令行 <c>--user-profile=&lt;路径&gt;</c>：未提权实例在提权重启时显式带过来，
///         最可靠，且能让两次运行的日志落在同一个文件里；</item>
///   <item>进程已提权、且交互用户（explorer.exe 的令牌属主）与进程用户不同：
///         用该 SID 在 <c>ProfileList</c> 里查到 ProfileImagePath；</item>
///   <item>其余情况（含未提权）沿用 <see cref="Environment.GetFolderPath"/>，与改造前完全一致。</item>
/// </list>
/// 任一步失败都退回第 ③ 种，绝不抛异常。
/// </summary>
public static class UserContext
{
    /// <summary>用户配置目录根（如 <c>C:\Users\MrChen</c>）。</summary>
    public static string Profile { get; }

    public static string LocalAppData { get; }
    public static string RoamingAppData { get; }
    public static string Desktop { get; }
    public static string Documents { get; }

    /// <summary>该用户的临时目录（<c>%LocalAppData%\Temp</c>）；不存在时返回 null。</summary>
    public static string? TempDir { get; }

    /// <summary>交互用户的 SID（判定失败或未提权时为 null）。</summary>
    public static string? InteractiveSid { get; }

    /// <summary>是否采用了「另一个用户」的上下文（提权场景）。</summary>
    public static bool IsOtherUser { get; }

    /// <summary>解析来源说明（写日志用）。</summary>
    public static string SourceText { get; }

    static UserContext()
    {
        // ---- ① 命令行显式指定 ----
        string? explicitProfile = ParseUserProfileArg();
        if (explicitProfile is not null)
        {
            Profile = explicitProfile;
            LocalAppData = Path.Combine(explicitProfile, "AppData", "Local");
            RoamingAppData = Path.Combine(explicitProfile, "AppData", "Roaming");

            // 若指定的就是本进程自己的配置目录（未提权时最常见），直接用系统给的路径，
            // 这样被重定向到 OneDrive 的桌面/文档也能正确命中，不用猜。
            bool isSelf = IsSamePath(explicitProfile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Desktop = isSelf
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : Path.Combine(explicitProfile, "Desktop");
            Documents = isSelf
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.Combine(explicitProfile, "Documents");

            TempDir = SafeTemp(LocalAppData);
            InteractiveSid = null;
            IsOtherUser = !isSelf;
            SourceText = "命令行 --user-profile 指定";
            return;
        }

        // ---- ② 提权且交互用户是别人 ----
        string? sid = TryGetInteractiveUserSid();
        if (sid is not null && !IsCurrentProcessSid(sid))
        {
            string? profile = ProfilePathOfSid(sid);
            if (!string.IsNullOrEmpty(profile) && Directory.Exists(profile))
            {
                Profile = profile;
                LocalAppData = Path.Combine(profile, "AppData", "Local");
                RoamingAppData = Path.Combine(profile, "AppData", "Roaming");
                Desktop = ResolveShellFolder(sid, "Desktop") ?? Path.Combine(profile, "Desktop");
                Documents = ResolveShellFolder(sid, "Personal") ?? Path.Combine(profile, "Documents");
                TempDir = SafeTemp(LocalAppData);
                InteractiveSid = sid;
                IsOtherUser = true;
                SourceText = "交互用户 SID（进程运行在其它账户下）";
                return;
            }
        }

        // ---- ③ 默认：进程自己的账户（与改造前一致） ----
        Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        TempDir = SafeTemp(LocalAppData);
        InteractiveSid = sid;
        IsOtherUser = false;
        SourceText = "当前进程账户";
    }

    /// <summary>一行日志摘要（启动时写入，便于事后判断工具到底在操作谁的目录）。</summary>
    public static string Describe() =>
        $"用户上下文：{Profile}（来源：{SourceText}{(IsOtherUser ? "，已提权到其它账户" : string.Empty)}）";

    /// <summary>
    /// 把"要写文件的目录"规整成一个**可用的绝对路径**。
    ///
    /// 为什么需要：配置目录可能为空（<c>GetFolderPath</c> 在目录不存在时返回空串），
    /// 此时 <c>Path.Combine("", "x")</c> 是**相对路径**，会把文件写到进程的当前目录
    /// （快捷方式的"起始位置"可以是任意地方，提权后更是 C:\Windows\System32）。
    /// 依次尝试传入目录、exe 所在目录；都不行返回 null，由调用方决定跳过还是报错。
    /// </summary>
    public static string? WritableDirectory(string? preferred)
    {
        var candidates = new List<string?> { preferred, AppDirectory() };
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                if (!Path.IsPathFullyQualified(c)) continue;
                string full = Path.GetFullPath(c);
                Directory.CreateDirectory(full);
                return full;
            }
            catch (Exception) { /* 试下一个 */ }
        }
        return null;
    }

    /// <summary>本程序所在目录（单文件发布下同样正确）。</summary>
    public static string? AppDirectory()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            return Path.GetDirectoryName(exe);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- 内部 ----------

    /// <summary>
    /// 临时目录一律从**解析后的 LocalAppData** 推导，而不是用 <c>Path.GetTempPath()</c>：
    /// 后者在 TMP/TEMP/USERPROFILE 缺失时会返回 Windows 目录（实测），而且提权后指向的是
    /// 提权账户的临时目录。这里只取 <c>&lt;LocalAppData&gt;\Temp</c> 并过一遍安全闸门。
    /// </summary>
    private static string? SafeTemp(string localAppData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(localAppData)) return null;
            string temp = Path.Combine(localAppData, "Temp");
            if (!Directory.Exists(temp)) return null;
            if (!SafeTargets.IsSafe(temp)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析 <c>--user-profile=</c>；只在确实是一个真实用户配置目录时才采纳。</summary>
    private static string? ParseUserProfileArg()
    {
        try
        {
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (!a.StartsWith("--user-profile=", StringComparison.OrdinalIgnoreCase)) continue;
                string value = a["--user-profile=".Length..].Trim().Trim('"');
                if (value.Length == 0) continue;
                if (!Path.IsPathFullyQualified(value)) continue;
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                if (!Directory.Exists(full)) continue;
                // 真正的用户配置目录一定有 AppData 子目录，否则不认（防止被塞进任意路径）
                if (!Directory.Exists(Path.Combine(full, "AppData"))) continue;
                return full;
            }
        }
        catch (Exception) { /* 忽略 */ }
        return null;
    }

    /// <summary>
    /// 读注册表里该用户的「用户外壳文件夹」（Desktop / Personal）。
    /// 这些值可能是 REG_EXPAND_SZ（含 %USERPROFILE%），需要按**目标用户**展开而不是当前进程的变量。
    /// </summary>
    private static string? ResolveShellFolder(string? sid, string valueName)
    {
        if (string.IsNullOrEmpty(sid)) return null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            using var key = baseKey.OpenSubKey(
                $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw)) return null;

            string expanded = raw
                .Replace("%USERPROFILE%", Profile, StringComparison.OrdinalIgnoreCase)
                .Replace("%APPDATA%", RoamingAppData, StringComparison.OrdinalIgnoreCase)
                .Replace("%LOCALAPPDATA%", LocalAppData, StringComparison.OrdinalIgnoreCase);
            return Path.IsPathFullyQualified(expanded) ? Path.TrimEndingDirectorySeparator(expanded) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ProfilePathOfSid(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsCurrentProcessSid(string sid)
    {
        try
        {
            string? mine = WindowsIdentity.GetCurrent().User?.Value;
            return mine is not null && mine.Equals(sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 取当前登录（交互）用户的 SID：进程列表里 explorer.exe 的令牌属主。
    /// 提权到别的账户时，这是唯一能可靠拿到"真正在用这台电脑的人"的办法。
    /// </summary>
    private static string? TryGetInteractiveUserSid()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try
                {
                    string? sid = TokenUserSid(p.Handle);
                    if (!string.IsNullOrEmpty(sid)) return sid;
                }
                catch (Exception) { /* 单个进程拿不到就换下一个 */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception) { /* 忽略 */ }
        return null;
    }

    private static string? TokenUserSid(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out token)) return null;

            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;

            var user = Marshal.PtrToStructure<TOKEN_USER>(buffer);
            if (!ConvertSidToStringSid(user.User.Sid, out IntPtr sidString)) return null;
            try { return Marshal.PtrToStringUni(sidString); }
            finally { LocalFree(sidString); }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private const int TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_USER
    {
        public SID_AND_ATTRIBUTES User;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
