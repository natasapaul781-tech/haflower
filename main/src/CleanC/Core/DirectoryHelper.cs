using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CleanC.Core;

/// <summary>测量上限（0 表示不限）：用于超大目录/整盘统计时的兜底保护。</summary>
public sealed class MeasureLimits
{
    public long MaxFiles { get; init; }
    public int MaxMilliseconds { get; init; }

    public static readonly MeasureLimits Unlimited = new();
}

/// <summary>目录扫描/删除与回收站辅助。所有操作用 try/catch 包裹，被占用项跳过并记录，不中断整体。</summary>
public static class DirectoryHelper
{
    private const int ProgressInterval = 8192; // 每 N 个文件检查一次取消/上报进度

    /// <summary>递归统计目录内文件总字节数。不存在的路径返回 0，访问失败的子项直接跳过。</summary>
    public static long GetDirectorySize(string path) => GetDirectorySize(path, CancellationToken.None);

    public static long GetDirectorySize(string path, CancellationToken ct) =>
        MeasureDirectory(path, MeasureLimits.Unlimited, null, ct).Bytes;

    /// <summary>
    /// 单次遍历测量目录：返回字节数/文件数/目录数/无权限目录数与截断标记。
    /// 重解析点（联接/符号链接）既不计入也不下探；隐藏与系统文件计入（如 pagefile.sys）。
    /// </summary>
    public static SizeEntry MeasureDirectory(string path, MeasureLimits? limits = null,
        Action<long, long>? progress = null, CancellationToken ct = default)
    {
        limits ??= MeasureLimits.Unlimited;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return new SizeEntry();

        var sw = limits.MaxMilliseconds > 0 ? Stopwatch.StartNew() : null;
        FastDirectoryWalker? walker = null;
        try
        {
            walker = new FastDirectoryWalker(path, null);
        }
        catch (Exception)
        {
            // 根目录本身无法访问
            return new SizeEntry { Denied = 1 };
        }

        bool truncated = false;
        try
        {
            while (walker.MoveNext())
            {
                if ((walker.Files & (ProgressInterval - 1)) == 0 && walker.Files > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(walker.Files, walker.Bytes);
                    if (limits.MaxFiles > 0 && walker.Files >= limits.MaxFiles) { truncated = true; break; }
                    if (sw != null && sw.ElapsedMilliseconds > limits.MaxMilliseconds) { truncated = true; break; }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* 遍历中途异常：保留已统计部分 */ }
        finally
        {
            walker.Dispose();
        }

        progress?.Invoke(walker.Files, walker.Bytes);
        return new SizeEntry
        {
            Bytes = walker.Bytes,
            Files = walker.Files,
            Dirs = walker.Dirs,
            Denied = walker.Denied,
            Truncated = truncated,
        };
    }

    /// <summary>删除目录内的所有内容（保留目录本身），逐个文件删除以精确统计释放字节数；被占用项写入错误列表并跳过。</summary>
    public static CleanResult DeleteFolderContents(string path)
    {
        var result = new CleanResult();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return result;

        // 最后一道闸门：目标可能来自环境变量或注册表脏数据，指向盘根/系统目录时一律拒绝
        // （扫描期已查过一次，这里再查一次——两次都过才动手）。
        if (SafeTargets.Reject(path) is { } unsafeWhy)
        {
            result.Errors.Add($"{path}: 已拒绝清理（{unsafeWhy}）。");
            return result;
        }

        DeleteContentsRecursive(path, result);
        SizeCache.InvalidateSubtree(path);   // 删除后占用缓存失效，下次测量重新读盘
        return result;
    }

    /// <summary>删除整个目录（含目录本身），返回实际释放字节数与错误。
    /// 符号链接/联接目录一律拒绝删除；被占用项逐文件跳过并记录，目录残留时保留。</summary>
    public static CleanResult DeleteDirectory(string path)
    {
        var result = new CleanResult();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return result;

        if (SafeTargets.Reject(path) is { } unsafeWhy)
        {
            result.Errors.Add($"{path}: 已拒绝删除（{unsafeWhy}）。");
            return result;
        }

        try
        {
            if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.Errors.Add($"{path}: 检测为符号链接/联接，已拒绝删除。");
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{path}: 无法读取目录属性，已跳过 ({ex.Message})");
            return result;
        }

        DeleteContentsRecursive(path, result);

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{path}: 目录未能完全删除，可能有文件仍被占用 ({ex.Message})");
        }
        SizeCache.InvalidateSubtree(path);   // 删除后占用缓存失效，下次测量重新读盘
        return result;
    }

    /// <summary>删除单个文件，返回是否成功；被占用/无权限时记录错误。</summary>
    public static bool DeleteFile(string file, List<string> errors)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(file)}: 被占用或无权删除 ({ex.Message})");
            return false;
        }
    }

    /// <summary>清空回收站（不弹系统确认框，进度/声音提示关闭）。返回是否成功。</summary>
    public static bool EmptyRecycleBin(List<string> errors)
    {
        const int SHERB_NOCONFIRMATION = 0x1;
        const int SHERB_NOPROGRESSUI = 0x2;
        const int SHERB_NOSOUND = 0x4;

        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        if (hr != 0)
        {
            // 0x80070491 表示回收站已空 —— 视为成功。
            const int ERROR_NOT_FOUND = unchecked((int)0x80070491);
            if (hr != ERROR_NOT_FOUND)
                errors.Add($"清空回收站失败 (错误码 0x{hr:X8})");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 本机各固定卷的回收站目录（<c>&lt;卷&gt;:\$Recycle.Bin</c>）。
    /// 回收站是**按卷**的：只统计系统盘会在多分区机器上严重低报（清空后释放量与实际不符）。
    /// 不可读/未就绪的卷直接跳过。
    /// </summary>
    public static IReadOnlyList<string> RecycleBinRoots()
    {
        var list = new List<string>();
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;
                    string p = Path.Combine(di.RootDirectory.FullName, "$Recycle.Bin");
                    if (Directory.Exists(p)) list.Add(p);
                }
                catch (Exception) { /* 单个卷失败忽略 */ }
            }
        }
        catch (Exception) { /* 枚举失败忽略 */ }

        // 兜底：至少给出系统盘（即使此刻读不到，也让「位置」列有内容）
        if (list.Count == 0)
        {
            try
            {
                string sys = Path.Combine(Environment.SystemDirectory, "..", "..", "$Recycle.Bin");
                string full = Path.GetFullPath(sys);
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
            }
            catch (Exception) { /* 忽略 */ }
        }

        return list;
    }

    /// <summary>估算当前登录用户在各卷回收站的占用字节数（尽力而为，无法访问则跳过该卷）。</summary>
    public static long GetRecycleBinSize()
    {
        long total = 0;
        bool elevated = ElevationHelper.IsElevated();

        // 统计对象是**当前登录用户**（提权到别的账户时也用它，与 UserContext 的其它口径一致）
        string sid = UserContext.InteractiveSid ?? string.Empty;
        if (sid.Length == 0 && !elevated)
        {
            try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
            catch (Exception) { /* 取不到 SID：退化为统计整个目录 */ }
        }

        foreach (var rebin in RecycleBinRoots())
        {
            try
            {
                // 提权后可统计全部用户；否则只统计自己的 SID 目录（其余无权读取）。
                total += string.IsNullOrEmpty(sid) ? GetDirectorySize(rebin) : GetDirectorySize(Path.Combine(rebin, sid));
            }
            catch (Exception) { /* 单个卷失败忽略 */ }
        }

        return total;
    }

    // ---------- 内部 ----------

    private static void DeleteContentsRecursive(string dir, CleanResult result)
    {
        foreach (var f in SafeGetFiles(dir))
        {
            long len = 0;
            try { len = new FileInfo(f).Length; }
            catch (Exception) { len = 0; }

            try
            {
                File.Delete(f);
                result.FreedBytes += len; // 长度读不到也算作 0，不产生误报
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{f}: 被占用或无权删除 ({ex.Message})");
            }
        }

        foreach (var d in SafeGetDirs(dir))
        {
            // 拒绝跟随符号链接/联接目录：junction 下方可能指向其他位置（如用户目录外），删除其内容会越界
            try
            {
                if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.Errors.Add($"{d}: 检测为符号链接/联接，已跳过（不跟随删除）。");
                    continue;
                }
            }
            catch (Exception) { /* 读取属性失败：按保守处理继续尝试删除 */ }
            DeleteContentsRecursive(d, result);
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception) { /* 目录被占用或非空，保留 */ }
    }

    private static string[] SafeGetFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, int dwFlags);
}
