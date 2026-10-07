namespace CleanC.Core;

/// <summary>
/// 递归删除目标的**安全闸门**（唯一真源）。
///
/// 为什么必须有它：本工具不少删除目标来自**本机环境**，而不是写死的常量——
/// <c>%TEMP%</c>、<c>GOCACHE</c>、<c>GOPATH</c>、注册表里的 <c>InstallLocation</c>、
/// 以及 <c>%APPDATA%</c> 下按目录名猜出来的缓存目录。这些字符串在**别人的电脑**上
/// 完全可能指向盘根、Windows 目录或整个共享目录：
/// <list type="bullet">
///   <item><c>GOCACHE=D:\</c> → 「Go 构建缓存」目标就是 <c>D:\</c>，一路 <c>DeleteDirectory</c> 会清空整盘；</item>
///   <item><c>TMP</c>/<c>TEMP</c>/<c>USERPROFILE</c> 全部缺失时 <c>Path.GetTempPath()</c> 返回
///         <c>C:\WINDOWS\</c>（已实测）→「用户临时文件」会去清空 Windows 目录；</item>
///   <item>注册表脏数据把 <c>D:\</c>、<c>C:\Program Files</c>、<c>C:\Users</c> 写成安装位置。</item>
/// </list>
///
/// 因此约定：**任何**递归删除都必须先过这里，并且查两次——
/// 扫描期（决定要不要把这一项列给用户看）与删除期（真正动手前）。
/// 任一次不通过就拒绝，并把原因写进错误列表，让用户看得见。
/// </summary>
public static class SafeTargets
{
    /// <summary>驱动器/共享根、以及这些目录本身：绝不能作为递归删除目标。</summary>
    private static readonly string[] ProtectedExact = BuildProtectedExact();

    /// <summary>这些目录**及其子树**都不允许被递归清空（Windows 目录），例外见 <see cref="WindowsTreeExceptions"/>。</summary>
    private static readonly string[] ProtectedTrees = BuildProtectedTrees();

    /// <summary>Windows 目录下唯二允许的清理目标（系统临时目录 / 更新下载缓存）。</summary>
    private static readonly string[] WindowsTreeExceptions = BuildWindowsExceptions();

    /// <summary>返回 null 表示可以删除；否则返回**面向用户的中文拒绝原因**。</summary>
    public static string? Reject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "路径为空";

        string full;
        try
        {
            // 非绝对路径（例如 GetFolderPath 返回空串后 Path.Combine 拼出的相对路径）
            // 会解析成"当前工作目录下的某处"，必须拒绝而不是猜测。
            if (!Path.IsPathFullyQualified(path)) return "不是绝对路径（可能是相对路径，会指向当前工作目录）";
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return "路径无效或无法解析";
        }

        if (full.Length == 0) return "路径为空";

        // ① 盘根 / UNC 共享根：「C:\」「\\server\share」
        string root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        if (root.Length == 0) return "无法确定所在卷（可能是 UNC 路径且卷不可用）";
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
            return "是磁盘根目录或共享根目录";

        // ② 平台目录与其子树（Windows 目录）
        foreach (var tree in ProtectedTrees)
        {
            if (IsSameOrInside(full, tree) && !IsWindowsTreeException(full))
                return $"位于系统目录内（{tree}）";
        }

        // ③ 平台目录本身（子目录允许——例如卸载残留、第三方缓存就在 Program Files 里）
        foreach (var exact in ProtectedExact)
        {
            if (full.Equals(exact, StringComparison.OrdinalIgnoreCase))
                return $"是系统/用户关键目录本身（{exact}）";
        }

        // ④ 任意用户配置目录（C:\Users\<别人>，含当前用户）
        if (IsDirectChildOf(full, UsersRoot()))
            return "是用户配置目录本身";

        // ⑤ 任意用户配置目录**内部**的关键子目录（桌面/文档/下载/AppData/OneDrive）：
        //    提权后本工具会去操作「当前登录用户」而不是提权账户的目录，
        //    因此这里必须按 Users\<任意用户>\ 的相对层级判断，而不是只看进程自己的 Profile。
        if (RejectProfileRelative(full) is { } profileWhy) return profileWhy;

        // ⑥ 用户目录下的 OneDrive 同步根（不同机器的名字不同：OneDrive、OneDrive - Contoso、
        //    OneDrive - 个人…），是云同步数据而非缓存，一律拒绝
        if (IsDirectChildOf(full, ProfileRoot()) && IsOneDriveName(Path.GetFileName(full)))
            return "是 OneDrive 同步目录（云同步数据，不属于缓存）";

        // ⑦ 本程序自身所在目录：删掉正在运行的程序所在目录没有意义。
        //    刻意**只比对"这个目录本身"**，不比对"包含它的上层目录"：
        //    用户把 exe 放在 %TEMP% 或某个工具目录里是很常见的（解压即用），
        //    若连"包含它的目录"也拒绝，最常见的那条「用户临时文件」清理会静默消失——
        //    而实际上正在运行的 exe 自身受系统保护、删不掉，不会真的把自己删没。
        string? exe = null;
        try { exe = Environment.ProcessPath; } catch (Exception) { /* 忽略 */ }
        if (!string.IsNullOrEmpty(exe))
        {
            string exeDir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exe) ?? string.Empty);
            if (exeDir.Length > 0 && exeDir.Equals(full, StringComparison.OrdinalIgnoreCase))
                return "是本程序自身所在的目录";
        }

        return null;
    }

    /// <summary>可以安全递归删除时返回 true。</summary>
    public static bool IsSafe(string? path) => Reject(path) is null;

    /// <summary>
    /// 路径是否等于 <paramref name="root"/> 或位于其下。
    /// 两侧都在分隔符边界上比较，避免 <c>C:\Program Files</c> 误命中 <c>C:\Program Files Extra</c>
    /// 这类前缀陷阱（多处判定曾用裸 StartsWith）。
    /// </summary>
    public static bool IsSameOrUnder(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

        string r = Path.TrimEndingDirectorySeparator(root);
        if (r.Length == 0) return false;

        string p;
        try { p = Path.TrimEndingDirectorySeparator(path); }
        catch (Exception) { p = path.TrimEnd('\\', '/'); }

        return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 内部 ----------

    /// <summary><paramref name="candidate"/> 是否等于 <paramref name="ancestor"/> 或在其之下。</summary>
    private static bool IsSameOrInside(string candidate, string ancestor) => IsSameOrUnder(candidate, ancestor);

    private static bool IsDirectChildOf(string candidate, string parent)
    {
        if (parent.Length == 0) return false;
        string prefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return candidate[prefix.Length..].IndexOf(Path.DirectorySeparatorChar) < 0;
    }

    private static bool IsWindowsTreeException(string full)
    {
        foreach (var allow in WindowsTreeExceptions)
            if (full.Equals(allow, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Windows 目录（<c>C:\Windows</c> 之类）。优先 SpecialFolder.Windows（随系统盘变化），
    /// 环境变量只在缺失时兜底——直接写 <c>?? "C:\Windows"</c> 在系统装在 D: 的机器上是错的。
    /// </summary>
    public static string WindowsDirectory()
    {
        try
        {
            string s = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(s)) return Path.TrimEndingDirectorySeparator(s);
        }
        catch (Exception) { /* 忽略 */ }

        try
        {
            string? env = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(env)) return Path.TrimEndingDirectorySeparator(env);
        }
        catch (Exception) { /* 忽略 */ }

        try
        {
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Environment.SystemDirectory));
            if (!string.IsNullOrWhiteSpace(parent)) return Path.TrimEndingDirectorySeparator(parent);
        }
        catch (Exception) { /* 忽略 */ }

        return @"C:\Windows";
    }

    private static string WindowsRoot() => WindowsDirectory();

    private static string UsersRoot()
    {
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile));
            return parent ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ProfileRoot()
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool IsOneDriveName(string name) =>
        name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用户配置目录内部禁止作为清理目标的关键子目录（相对 <c>C:\Users\&lt;user&gt;\</c>）。
    /// 空字符串 = 配置目录本身。
    /// </summary>
    private static readonly string[] ForbiddenProfileRelative =
    {
        "", "AppData", @"AppData\Local", @"AppData\LocalLow", @"AppData\Roaming",
        "Desktop", "Documents", "Downloads", "Favorites", "Links", "Contacts",
        "Searches", "Saved Games", "3D Objects", "OneDrive",
    };

    /// <summary>
    /// 按「Users 根 → 用户目录 → 相对层级」判断，因此对**任意**用户的配置目录都生效，
    /// 而不只是当前进程所属的那个（提权场景下两者常常不同）。
    /// </summary>
    private static string? RejectProfileRelative(string full)
    {
        foreach (var usersRoot in UsersRoots())
        {
            string prefix = usersRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string rest = full[prefix.Length..];
            int sep = rest.IndexOf(Path.DirectorySeparatorChar);
            if (sep < 0) return "是用户配置目录本身";   // C:\Users\<user>

            string relative = rest[(sep + 1)..];
            foreach (var forbidden in ForbiddenProfileRelative)
                if (relative.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                    return $"是用户配置目录内的关键目录（{relative}）";

            // OneDrive 名字随租户变化（OneDrive - Contoso），按第一段前缀判断
            string firstSegment = relative.Split(Path.DirectorySeparatorChar)[0];
            if (IsOneDriveName(firstSegment))
                return "是 OneDrive 同步目录（云同步数据，不属于缓存）";
        }
        return null;
    }

    /// <summary>可能的 Users 根：当前用户的父目录，外加系统盘下的 <c>Users</c>。</summary>
    private static IEnumerable<string> UsersRoots()
    {
        var list = new List<string>();
        string current = UsersRoot();
        if (current.Length > 0) list.Add(current);

        try
        {
            string drive = Path.GetPathRoot(WindowsDirectory()) ?? string.Empty;
            if (drive.Length > 0)
            {
                string std = Path.TrimEndingDirectorySeparator(Path.Combine(drive, "Users"));
                if (!list.Contains(std, StringComparer.OrdinalIgnoreCase)) list.Add(std);
            }
        }
        catch (Exception) { /* 忽略 */ }

        return list;
    }

    private static string[] BuildProtectedTrees()
    {
        var list = new List<string>();
        Add(list, WindowsRoot());
        return list.ToArray();
    }

    private static string[] BuildWindowsExceptions()
    {
        string win = WindowsRoot();
        if (win.Length == 0) return Array.Empty<string>();

        var list = new List<string>();
        Add(list, Path.Combine(win, "Temp"));
        Add(list, Path.Combine(win, "SoftwareDistribution", "Download"));
        Add(list, Path.Combine(win, "SystemTemp"));   // Windows 11 下 SYSTEM 账户的临时目录
        return list.ToArray();
    }

    private static string[] BuildProtectedExact()
    {
        var list = new List<string>();

        // 驱动器根
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                Add(list, Path.TrimEndingDirectorySeparator(d.Name));
        }
        catch (Exception) { /* 忽略 */ }

        Add(list, WindowsRoot());
        Add(list, UsersRoot());

        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            Add(list, pf);
            Add(list, pf86);
            Add(list, pd);
            Add(list, profile);
            Add(list, localApp);
            Add(list, roaming);
            Add(list, desktop);
            Add(list, docs);

            // 平台共享目录（与 AppPathMatcher.BroadRelativePaths 同一口径）
            Add(list, Path.Combine(pf, "Common Files"));
            Add(list, Path.Combine(pf86, "Common Files"));
            Add(list, Path.Combine(pd, "Microsoft"));
            Add(list, Path.Combine(pd, "Package Cache"));

            if (profile.Length > 0)
            {
                Add(list, Path.Combine(profile, "AppData"));
                Add(list, Path.Combine(profile, "AppData", "Local"));
                Add(list, Path.Combine(profile, "AppData", "LocalLow"));
                Add(list, Path.Combine(profile, "AppData", "Roaming"));
                Add(list, Path.Combine(profile, "Desktop"));
                Add(list, Path.Combine(profile, "Documents"));
                Add(list, Path.Combine(profile, "Downloads"));
                Add(list, Path.Combine(profile, "OneDrive"));
            }
        }
        catch (Exception) { /* 忽略 */ }

        return list.ToArray();
    }

    private static void Add(List<string> list, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.Length > 0 && !list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
        }
        catch (Exception) { /* 忽略非法路径 */ }
    }
}
