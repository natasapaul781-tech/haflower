using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>位置来源：决定 UI 展示文案与可信度（越靠前越可信）。</summary>
public enum AppPathSource
{
    /// <summary>注册表 InstallLocation（最可信）。</summary>
    RegistryInstallLocation,

    /// <summary>DisplayIcon 指向的目录。</summary>
    DisplayIcon,

    /// <summary>卸载程序所在目录（由 UninstallString 推断）。</summary>
    UninstallerPath,

    /// <summary>App Paths 注册的执行文件所在目录。</summary>
    AppPaths,

    /// <summary>按显示名/发布者匹配到的目录（推断）。</summary>
    NameMatch,

    /// <summary>商店(UWP)包体目录（WindowsApps）。</summary>
    UwpPackage,

    /// <summary>商店(UWP)应用数据目录（%LOCALAPPDATA%\Packages）。</summary>
    UwpData,

    /// <summary>用户数据目录（%LOCALAPPDATA% / %APPDATA% / %PROGRAMDATA% 名称匹配）。</summary>
    UserDataMatch,
}

/// <summary>一个已解析出的软件位置（安装目录或数据目录）及其测量结果。</summary>
public sealed class AppPathInfo
{
    public required string Path { get; init; }
    public required AppPathSource Source { get; init; }

    /// <summary>true = 数据目录（用户配置/缓存），false = 安装目录。</summary>
    public bool IsData { get; init; }

    public long Bytes { get; set; }
    public long Files { get; set; }
    public long Dirs { get; set; }
    public int Denied { get; set; }
    public bool Truncated { get; set; }
    public bool Measured { get; set; }

    /// <summary>该目录同时被其他软件使用（占用不重复计入合计）。</summary>
    public string? SharedWith { get; set; }

    /// <summary>
    /// 位置可疑（如注册表把父目录当成安装位置）：此时不测量、不计入占用，只展示位置并说明原因。
    /// </summary>
    public string? Suspect { get; set; }

    public string SourceText => Source switch
    {
        AppPathSource.RegistryInstallLocation => "注册表 InstallLocation",
        AppPathSource.DisplayIcon => "DisplayIcon 推断",
        AppPathSource.UninstallerPath => "卸载程序目录推断",
        AppPathSource.AppPaths => "App Paths 注册表",
        AppPathSource.NameMatch => "目录名匹配",
        AppPathSource.UwpPackage => "商店包体目录",
        AppPathSource.UwpData => "商店应用数据目录",
        AppPathSource.UserDataMatch => "用户数据目录匹配",
        _ => "未知",
    };

    public string SizeText => !Measured
        ? "未测量"
        : Truncated
            ? "≥" + SizeFormatter.Format(Bytes)
            : SizeFormatter.Format(Bytes);
}

/// <summary>一个软件的占用与位置汇总。</summary>
public sealed class AppFootprint
{
    /// <summary>安装位置（测量与合计口径以此为主）。</summary>
    public AppPathInfo? Install { get; set; }

    /// <summary>数据位置（%LOCALAPPDATA% / %APPDATA% / %PROGRAMDATA% 匹配项）。</summary>
    public List<AppPathInfo> DataPaths { get; } = new();

    /// <summary>其他同样成立但未计入占用的候选位置（仅展示，避免重复计数）。</summary>
    public List<string> OtherCandidates { get; } = new();

    public bool Measured { get; set; }

    /// <summary>存在无法读取的目录（如商店包体需要管理员权限）。</summary>
    public bool Inaccessible { get; set; }

    /// <summary>测量到的安装位置字节数（不含数据目录；过宽位置不计入）。</summary>
    public long InstallBytes => Install is { Suspect: null } ? Install.Bytes : 0;

    /// <summary>测量到的数据目录字节数合计（过宽位置不计入）。</summary>
    public long DataBytes => DataPaths.Where(p => p.Suspect == null).Sum(p => p.Bytes);

    /// <summary>本软件计入合计的字节数（安装 + 数据，剔除被其他软件认领的目录与过宽位置）。</summary>
    public long CountedBytes
    {
        get
        {
            long total = 0;
            if (Install is { Measured: true, SharedWith: null, Suspect: null }) total += Install.Bytes;
            foreach (var d in DataPaths)
                if (d is { Measured: true, SharedWith: null, Suspect: null })
                    total += d.Bytes;
            return total;
        }
    }

    public bool HasAnyPath => Install != null || DataPaths.Count > 0;

    public IEnumerable<AppPathInfo> AllPaths()
    {
        if (Install != null) yield return Install;
        foreach (var d in DataPaths) yield return d;
    }

    public string LocationText
    {
        get
        {
            if (Install == null) return DataPaths.Count > 0 ? "（仅发现数据目录）" : string.Empty;
            string text = Install.Path;
            if (Install.SharedWith != null) text += $"（与他人共用：{Install.SharedWith}）";
            if (DataPaths.Count > 0) text += $"；数据 {DataPaths.Count} 处";
            return text;
        }
    }

    public string DataLocationText
    {
        get
        {
            if (DataPaths.Count == 0) return string.Empty;
            if (DataPaths.Count == 1) return DataPaths[0].Path;
            return DataPaths[0].Path + $" 等 {DataPaths.Count} 处";
        }
    }

    public string SizeDetailText
    {
        get
        {
            if (!Measured) return "未测量";
            var parts = new List<string>();
            if (Install is { Measured: true }) parts.Add($"安装 {Install.SizeText}");
            if (DataPaths.Count > 0)
                parts.Add($"数据 {SizeFormatter.Format(DataBytes)}（{DataPaths.Count} 处）");
            string text = parts.Count > 0 ? string.Join(" + ", parts) : "无法读取";
            if (Inaccessible) text += "；部分目录无权限（建议以管理员运行）";
            return text;
        }
    }
}

/// <summary>目录索引项（一次性枚举候选根目录，供所有软件复用匹配）。</summary>
public sealed class IndexedDir
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string? ParentName { get; init; }
    public int Depth { get; init; }
}

/// <summary>
/// 一次软件扫描期间的共享上下文：目录索引（只枚举一次）、App Paths 索引与
/// 「目录 → 认领软件」映射（多软件共用同一目录时只计一次占用）。
/// </summary>
public sealed class FootprintSession
{
    private readonly Dictionary<string, string> _pathOwner = new(StringComparer.OrdinalIgnoreCase);
    private List<IndexedDir>? _installIndex;
    private List<IndexedDir>? _dataIndex;
    private Dictionary<string, string>? _appPaths;

    /// <summary>安装位置候选根（按可信度排序）。用户级根走 UserContext，提权后仍指向当前登录用户。</summary>
    public static readonly string[] InstallRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(UserContext.LocalAppData, "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        UserContext.LocalAppData,
        UserContext.RoamingAppData,
    };

    /// <summary>数据目录候选根。</summary>
    public static readonly string[] DataRoots =
    {
        UserContext.LocalAppData,
        UserContext.RoamingAppData,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    };

    public IReadOnlyList<IndexedDir> InstallIndex => _installIndex ??= BuildIndex(InstallRoots);
    public IReadOnlyList<IndexedDir> DataIndex => _dataIndex ??= BuildIndex(DataRoots);
    public IReadOnlyDictionary<string, string> AppPaths => _appPaths ??= BuildAppPaths();

    public string? OwnerOf(string path) =>
        _pathOwner.TryGetValue(Normalize(path), out var owner) ? owner : null;

    public void Claim(string path, string appName) => _pathOwner[Normalize(path)] = appName;

    public void Release(string path) => _pathOwner.Remove(Normalize(path));

    public static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return path.TrimEnd('\\', '/'); }
    }

    /// <summary>枚举候选根下深度 ≤2 的目录（跳过重解析点与系统目录），只做一次。</summary>
    private static List<IndexedDir> BuildIndex(IEnumerable<string> roots)
    {
        var list = new List<IndexedDir>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            foreach (var level1 in SafeGetDirs(root))
            {
                if (IsReparse(level1)) continue;
                string name1 = Path.GetFileName(level1);
                if (name1.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                if (name1.Equals("Package Cache", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new IndexedDir { Path = level1, Name = name1, ParentName = Path.GetFileName(root), Depth = 1 });

                foreach (var level2 in SafeGetDirs(level1))
                {
                    if (IsReparse(level2)) continue;
                    list.Add(new IndexedDir
                    {
                        Path = level2,
                        Name = Path.GetFileName(level2),
                        ParentName = name1,
                        Depth = 2,
                    });
                }
            }
        }
        return list;
    }

    /// <summary>App Paths（64/32 位视图 + 当前用户）：规范化 exe 名 → 完整路径。</summary>
    private static Dictionary<string, string> BuildAppPaths()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var ap = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (ap == null) continue;
                foreach (var sub in ap.GetSubKeyNames())
                {
                    string? value = ap.GetValue(sub, null) as string;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    string exe = value.Trim().Trim('"');
                    string key = AppPathMatcher.Normalize(Path.GetFileNameWithoutExtension(sub));
                    if (key.Length > 0)
                        map.TryAdd(key, exe);
                }
            }
            catch (Exception) { /* 无法打开该视图则跳过 */ }
        }
        return map;
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static bool IsReparse(string path)
    {
        try { return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// 软件占用与位置解析：注册表 InstallLocation → DisplayIcon → 卸载程序目录 → App Paths →
/// 目录名匹配 → 商店包体/包数据；解析结果可选用 SizeCache + 单次遍历测量大小。
/// 全部为确定性规则，不做全盘模糊搜索；测量为只读操作，绝不删除任何文件。
/// </summary>
public static class AppFootprintResolver
{
    /// <summary>单目录测量上限（防止脏数据把整个公共目录算进来时耗时过长）。</summary>
    private static readonly MeasureLimits AppLimits = new() { MaxFiles = 2_000_000 };

    private const int MaxDataPaths = 3;

    public static AppFootprint Resolve(InstalledApp app, FootprintSession session, bool measure,
        CancellationToken ct = default)
    {
        var fp = new AppFootprint();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(AppPathInfo info, bool primary)
        {
            string key = FootprintSession.Normalize(info.Path);
            if (!seen.Add(key)) return;
            if (primary) fp.Install = info;
            else fp.OtherCandidates.Add($"{info.Path}（{info.SourceText}）");
        }

        if (app.IsUwp)
            ResolveUwp(app, session, fp, ct);
        else
            ResolveWin32(app, session, fp, AddCandidate, ct);

        // 认领与共享标记（多软件同目录时占用只计一次）
        foreach (var p in fp.AllPaths())
        {
            string? owner = session.OwnerOf(p.Path);
            if (owner != null && !string.Equals(owner, app.DisplayName, StringComparison.OrdinalIgnoreCase))
                p.SharedWith = owner;
            else
                session.Claim(p.Path, app.DisplayName);
        }

        if (measure)
        {
            foreach (var p in fp.AllPaths())
            {
                ct.ThrowIfCancellationRequested();
                if (p.Suspect != null) continue;
                MeasureInto(p, ct);
            }
            fp.Measured = fp.AllPaths().Any(p => p.Measured);
            fp.Inaccessible = fp.AllPaths().Any(p => p.Denied > 0 && p.Bytes == 0);
        }

        return fp;
    }

    /// <summary>
    /// 测量一个软件的占用（跳过可疑位置）。供 SoftwareScanner 在完成“位置可疑性校验”后统一调用。
    /// </summary>
    public static void Measure(InstalledApp app, CancellationToken ct = default)
    {
        var fp = app.Footprint;
        if (fp == null) return;

        foreach (var p in fp.AllPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (p.Suspect != null) continue;
            MeasureInto(p, ct);
        }
        fp.Measured = fp.AllPaths().Any(p => p.Measured);
        fp.Inaccessible = fp.AllPaths().Any(p => p.Denied > 0 && p.Bytes == 0);
    }

    /// <summary>
    /// 位置可疑性校验：若某软件的安装位置“包住”其他 ≥2 个软件的安装位置，
    /// 说明注册表把父目录（如 D:\编程）写成了安装位置，直接测量会把整个父目录（可能几十 GB）算到它头上。
    /// 这类路径标记为可疑：仍展示位置，但不测量、不计入合计。
    /// </summary>
    public static int MarkSuspiciousPaths(IReadOnlyList<InstalledApp> apps)
    {
        var pairs = apps
            .Where(a => a.Footprint?.Install != null)
            .Select(a => (App: a, Path: FootprintSession.Normalize(a.Footprint!.Install!.Path)))
            .ToArray();

        int flagged = 0;
        foreach (var (app, path) in pairs)
        {
            if (app.Footprint!.Install!.Suspect != null) continue;

            string prefix = path + Path.DirectorySeparatorChar;
            int contained = 0;
            foreach (var (other, otherPath) in pairs)
            {
                if (ReferenceEquals(other, app)) continue;
                if (otherPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) contained++;
            }

            if (contained >= 2)
            {
                app.Footprint.Install.Suspect = $"该位置包含其他 {contained} 个软件的安装目录，判定为“过宽位置”";
                flagged++;
            }
        }
        return flagged;
    }

    // ---------- 商店(UWP) ----------

    private static void ResolveUwp(InstalledApp app, FootprintSession session, AppFootprint fp, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            fp.Install = new AppPathInfo { Path = app.InstallLocation!, Source = AppPathSource.UwpPackage };

        string? family = PackageFamilyName(app.PackageFullName);
        if (family != null)
        {
            string dataDir = Path.Combine(UserContext.LocalAppData, "Packages", family);
            if (Directory.Exists(dataDir))
                fp.DataPaths.Add(new AppPathInfo { Path = dataDir, Source = AppPathSource.UwpData, IsData = true });
        }
    }

    /// <summary>PackageFullName → PackageFamilyName（Name_PublisherId）。</summary>
    private static string? PackageFamilyName(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName)) return null;
        var parts = packageFullName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"{parts[0]}_{parts[^1]}" : null;
    }

    // ---------- 传统 Win32 ----------

    private static void ResolveWin32(InstalledApp app, FootprintSession session, AppFootprint fp,
        Action<AppPathInfo, bool> addCandidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1) 注册表 InstallLocation 优先
        if (IsUsableDir(app.InstallLocation))
        {
            addCandidate(new AppPathInfo { Path = app.InstallLocation!, Source = AppPathSource.RegistryInstallLocation }, true);
        }

        // 2) DisplayIcon 所在目录
        if (fp.Install == null)
        {
            string? iconDir = DirectoryOfIcon(app.DisplayIcon);
            if (iconDir != null)
                addCandidate(new AppPathInfo { Path = iconDir, Source = AppPathSource.DisplayIcon }, true);
        }

        // 3) 卸载程序所在目录
        if (fp.Install == null)
        {
            string? uninstallerDir = DirectoryOfUninstaller(app);
            if (uninstallerDir != null)
                addCandidate(new AppPathInfo { Path = uninstallerDir, Source = AppPathSource.UninstallerPath }, true);
        }

        // 4) App Paths 注册表
        if (fp.Install == null)
        {
            string? appPathDir = DirectoryOfAppPaths(app, session);
            if (appPathDir != null)
                addCandidate(new AppPathInfo { Path = appPathDir, Source = AppPathSource.AppPaths }, true);
        }

        // 5) 目录名匹配（安装根）
        var keys = AppPathMatcher.MatchKeys(app.DisplayName, app.Publisher);
        if (keys.Count > 0)
        {
            foreach (var match in MatchIndex(session.InstallIndex, keys, app))
            {
                var info = new AppPathInfo { Path = match, Source = AppPathSource.NameMatch };
                addCandidate(info, fp.Install == null);
            }
        }

        // 6) 用户数据目录（%LOCALAPPDATA% / %APPDATA% / %PROGRAMDATA%）
        if (keys.Count > 0)
        {
            foreach (var match in MatchIndex(session.DataIndex, keys, app))
            {
                if (fp.DataPaths.Count >= MaxDataPaths) break;
                string norm = FootprintSession.Normalize(match);
                if (fp.Install != null && FootprintSession.Normalize(fp.Install.Path) == norm) continue;
                if (fp.DataPaths.Any(d => FootprintSession.Normalize(d.Path) == norm)) continue;
                // 已被其他软件认领的（多为厂商级公共目录，如 %LOCALAPPDATA%\NVIDIA Corporation）
                // 不再重复列为本软件的数据位置，避免十余个组件都指向同一目录造成误导
                if (session.OwnerOf(match) != null) continue;
                fp.DataPaths.Add(new AppPathInfo { Path = match, Source = AppPathSource.UserDataMatch, IsData = true });
            }
        }
    }

    /// <summary>在目录索引中做确定性名称匹配：严格（显示名/发布者/首末段）优先，其次规范化相等。</summary>
    private static IEnumerable<string> MatchIndex(IReadOnlyList<IndexedDir> index,
        IReadOnlyList<string> keys, InstalledApp app)
    {
        var results = new List<string>();
        var loose = new List<string>();

        foreach (var dir in index)
        {
            string? strict = AppPathMatcher.MatchDirectoryNameStrict(dir.Name, app.DisplayName, app.Publisher);
            if (strict != null)
            {
                results.Add(dir.Path);
                continue;
            }

            if (dir.Depth >= 2 && AppPathMatcher.MatchParentChildStrict(dir.Path, dir.Depth, app.DisplayName) != null)
            {
                results.Add(dir.Path);
                continue;
            }

            // 宽松：目录名 == 发布者\产品 的第二段，如 Microsoft\VS Code
            if (AppPathMatcher.MatchesLoose(dir.Name, keys))
                loose.Add(dir.Path);
        }

        foreach (var l in loose)
            if (!results.Contains(l, StringComparer.OrdinalIgnoreCase))
                results.Add(l);

        return results;
    }

    // ---------- 各来源定位 ----------

    private static bool IsUsableDir(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(path) &&
        !AppPathMatcher.IsTooBroadPath(path);

    private static string? DirectoryOfIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string raw = displayIcon.Trim();
        if (raw.StartsWith('"'))
        {
            int end = raw.IndexOf('"', 1);
            if (end > 1) raw = raw[1..end];
        }
        else
        {
            int comma = raw.LastIndexOf(',');
            if (comma > 2) raw = raw[..comma];
        }

        // 图标串可能是 "app.exe,-101" / "app.exe,0" / "a.dll,3"
        raw = raw.Trim().Trim('"');
        if (raw.Length == 0) return null;
        try { raw = Environment.ExpandEnvironmentVariables(raw); } catch (Exception) { }
        if (AppPathMatcher.IsSystemPath(raw)) return null;

        try
        {
            if (Directory.Exists(raw)) return AppPathMatcher.IsTooBroadPath(raw) ? null : raw;
            if (!File.Exists(raw)) return null;
        }
        catch (Exception) { return null; }

        string? dir = Path.GetDirectoryName(raw);
        if (string.IsNullOrEmpty(dir) || AppPathMatcher.IsTooBroadPath(dir)) return null;
        return Directory.Exists(dir) ? dir : null;
    }

    private static string? DirectoryOfUninstaller(InstalledApp app)
    {
        foreach (var cmd in new[] { app.UninstallString, app.QuietUninstallString })
        {
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(cmd); }
            catch (Exception) { continue; }

            var (exe, _) = UninstallRunner.ParseCommandLine(expanded);
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string fileName = Path.GetFileName(exe);
            if (fileName.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase)) continue;
            if (AppPathMatcher.IsSystemPath(exe)) continue;

            string? dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            if (AppPathMatcher.IsTooBroadPath(dir)) continue;

            // 临时目录下的卸载器（安装包自解压）不作为安装位置
            string temp = Path.GetTempPath();
            if (dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) continue;

            return dir;
        }
        return null;
    }

    private static string? DirectoryOfAppPaths(InstalledApp app, FootprintSession session)
    {
        var map = session.AppPaths;
        if (map.Count == 0) return null;

        var keys = AppPathMatcher.MatchKeys(app.DisplayName, app.Publisher);
        foreach (var key in keys)
        {
            if (!map.TryGetValue(key, out var exe)) continue;
            string? dir = SafeParent(exe);
            if (dir != null) return dir;
        }

        // exe 名 == 显示名首 token（如 "Code.exe" ← "Visual Studio Code"）
        foreach (var token in AppPathMatcher.NameTokens(app.DisplayName))
        {
            string key = AppPathMatcher.Normalize(token);
            if (key.Length < 3 || !map.TryGetValue(key, out var exe)) continue;
            string? dir = SafeParent(exe);
            if (dir != null) return dir;
        }
        return null;
    }

    private static string? SafeParent(string exe)
    {
        try
        {
            string path = Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"'));
            if (!File.Exists(path)) return null;
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || AppPathMatcher.IsTooBroadPath(dir)) return null;
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception) { return null; }
    }

    // ---------- 测量 ----------

    private static void MeasureInto(AppPathInfo info, CancellationToken ct)
    {
        if (!SizeCache.TryGet(info.Path, out var entry))
        {
            entry = DirectoryHelper.MeasureDirectory(info.Path, AppLimits, null, ct);
            SizeCache.Set(info.Path, entry);
        }
        info.Bytes = entry.Bytes;
        info.Files = entry.Files;
        info.Dirs = entry.Dirs;
        info.Denied = entry.Denied;
        info.Truncated = entry.Truncated;
        info.Measured = true;
    }

    // ---------- 合计 ----------

    /// <summary>列表合计：同一目录只计一次；返回（实测合计, 估算合计, 实测项数, 无法测量项数）。</summary>
    public static (long Measured, long Estimated, int MeasuredCount, int NoDataCount) Totals(
        IEnumerable<InstalledApp> apps)
    {
        long measured = 0, estimated = 0;
        int measuredCount = 0, noData = 0;
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            var fp = app.Footprint;
            if (fp == null || !fp.Measured)
            {
                continue;
            }

            bool any = false;
            foreach (var p in fp.AllPaths())
            {
                if (p.SharedWith != null) continue;                 // 与他人共用：由首次认领的软件计入
                if (!counted.Add(FootprintSession.Normalize(p.Path))) continue;
                if (p.Measured) { measured += p.Bytes; any = true; }
            }
            if (any) measuredCount++;
            else noData++;
        }

        foreach (var app in apps)
        {
            if (app.IsMeasured) continue;
            estimated += app.EstimatedBytes;
        }

        return (measured, estimated, measuredCount, noData);
    }
}
