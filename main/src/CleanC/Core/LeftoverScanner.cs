using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>残留候选的类型。</summary>
public enum LeftoverKind
{
    /// <summary>目录（安装目录 / AppData 匹配目录）。</summary>
    Directory,

    /// <summary>注册表键（Uninstall 残留 / App Paths / Software 匹配子键）。</summary>
    RegistryKey,

    /// <summary>快捷方式（.lnk）。</summary>
    Shortcut,
}

/// <summary>
/// 卸载后“残留候选”：工具只能给出确定匹配的候选并附匹配理由，
/// 由用户逐项勾选（默认全不勾）并二次确认后才删除。
/// </summary>
public sealed class LeftoverCandidate
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>候选目标：目录/文件的绝对路径、或注册表键的完整显示路径。</summary>
    public required string Target { get; init; }

    /// <summary>匹配理由（显示在“匹配规则”列）。</summary>
    public required string MatchedRule { get; init; }

    /// <summary>风险提示。</summary>
    public string Risk { get; init; } = string.Empty;

    /// <summary>删除是否需要管理员权限（Program Files / ProgramData / HKLM 项）。</summary>
    public bool RequiresElevation { get; init; }

    // 注册表候选专用
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }

    /// <summary>注册表键相对路径（如 SOFTWARE\Google\Chrome）。</summary>
    public string? KeyPath { get; init; }

    /// <summary>目录候选的实测占用（注册表/快捷方式候选为 null）。</summary>
    public long? SizeBytes { get; set; }

    public bool Denied { get; set; }

    public string SizeText => Kind != LeftoverKind.Directory
        ? "—"
        : SizeBytes == null ? "未测量" : (Denied ? "≥" : string.Empty) + SizeFormatter.Format(SizeBytes.Value);

    public string KindText => Kind switch
    {
        LeftoverKind.Directory => "目录",
        LeftoverKind.RegistryKey => "注册表",
        LeftoverKind.Shortcut => "快捷方式",
        _ => "?",
    };
}

/// <summary>
/// 卸载后的残留扫描与清理。扫描规则全部为“确定性匹配”（显示名/发布者/路径 token 精确相等），
/// 不做全注册表关键字扫描；删除复用 DirectoryHelper（拒绝重解析点、占用跳过）。
/// </summary>
public static class LeftoverScanner
{
    private const int DirScanCap = 5000;      // 每个根目录遍历上限
    private const int RegistryScanCap = 4000; // 注册表键检查上限
    private const int MaxCandidates = 120;

    // 用户级目录一律走 UserContext：提权到别的账户时它仍指向**当前登录用户**
    private static readonly string LocalAppData = UserContext.LocalAppData;
    private static readonly string AppData = UserContext.RoamingAppData;
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string UserDesktop = UserContext.Desktop;
    private static readonly string PublicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    private static readonly string UserStartMenu = Path.Combine(
        UserContext.RoamingAppData, @"Microsoft\Windows\Start Menu");
    private static readonly string PublicStartMenu = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs");

    /// <summary>
    /// 对一个（卸载未完成的）应用扫描残留候选：卸载注册表项、安装目录、AppData 匹配目录、
    /// 快捷方式、App Paths 项与 Software 根下的匹配子键；目录候选会实测占用并按大小排序。
    /// </summary>
    public static List<LeftoverCandidate> Scan(InstalledApp app, CancellationToken ct = default)
    {
        var list = new List<LeftoverCandidate>();
        if (app.IsUwp) return list; // 商店包数据在包容器/虚拟化中，不做残留扫描

        // 1) 卸载注册表项仍存在
        if (SoftwareScanner.IsEntryStillRegistered(app) && !string.IsNullOrEmpty(app.KeyPath))
            Add(list, new LeftoverCandidate
            {
                Kind = LeftoverKind.RegistryKey,
                Target = app.KeyDisplayPath,
                MatchedRule = "卸载注册表项仍存在（官方卸载未完成）",
                Risk = "删除后将无法再通过“程序和功能”卸载它",
                RequiresElevation = app.Hive == RegistryHive.LocalMachine,
                Hive = app.Hive,
                View = app.View,
                KeyPath = app.KeyPath,
            });

        // 2) 安装目录：优先注册表 InstallLocation；缺失或带引号的条目从卸载程序路径推断
        //    （官方卸载程序通常位于安装目录，推断目录仅作为候选，仍须用户确认）
        if (!IsPathExists(app.InstallLocation))
        {
            string? derived = TryDeriveInstallDir(app);
            if (derived != null)
                AddDirCandidate(app, derived, "由卸载程序路径推断的安装目录", list);
        }
        else
        {
            AddDirCandidate(app, app.InstallLocation!, "注册表记录的安装位置仍存在", list);
        }

        // 3) AppData / ProgramData 匹配目录（深度 ≤ 2）
        foreach (var root in new[] { LocalAppData, AppData, ProgramData })
            ScanDirTree(root, app, list);

        // 4) 快捷方式
        foreach (var root in new[] { UserDesktop, PublicDesktop, Path.Combine(UserStartMenu, "Programs"), PublicStartMenu })
            ScanShortcuts(root, app, list);

        // 5) 注册表候选：App Paths + Software 根（一级匹配，按首 token 下探一级）
        ScanRegistryCandidates(app, list);

        // 6) 目录候选实测占用（带上限保护），并按大小降序排列：目录优先、最大的在前
        MeasureDirectories(list, ct);
        return list
            .OrderByDescending(c => c.Kind == LeftoverKind.Directory)
            .ThenByDescending(c => c.SizeBytes ?? -1)
            .ToList();
    }

    /// <summary>测量目录候选的占用（单目录最长 30 秒，超限显示 “≥”），供用户判断清理价值。</summary>
    private static void MeasureDirectories(List<LeftoverCandidate> list, CancellationToken ct)
    {
        foreach (var c in list)
        {
            ct.ThrowIfCancellationRequested();
            if (c.Kind != LeftoverKind.Directory || !Directory.Exists(c.Target)) continue;

            try
            {
                if (!SizeCache.TryGet(c.Target, out var entry))
                {
                    entry = DirectoryHelper.MeasureDirectory(c.Target,
                        new MeasureLimits { MaxMilliseconds = 30_000 }, null, ct);
                    SizeCache.Set(c.Target, entry);
                }
                c.SizeBytes = entry.Bytes;
                c.Denied = entry.Denied > 0 || entry.Truncated;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // 单个候选测量失败（权限/占用/路径异常）不应中断整批
                c.SizeBytes = null;
                c.Denied = true;
            }
        }
    }

    /// <summary>删除选中的残留候选（可取消；逐项记录日志）。</summary>
    public static CleanResult Delete(IReadOnlyList<LeftoverCandidate> items,
        Action<string>? log, CancellationToken ct)
    {
        var r = new CleanResult();
        foreach (var c in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                DeleteOne(c, r, log);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单项异常（权限/占用/路径异常）不中断整批
                r.Errors.Add($"{c.Target}: 删除失败（{ex.Message}）");
                log?.Invoke($"    [失败] {c.Target}：{ex.Message}");
            }
        }
        return r;
    }

    private static void DeleteOne(LeftoverCandidate c, CleanResult r, Action<string>? log)
    {
        switch (c.Kind)
        {
            case LeftoverKind.Directory:
                if (!Directory.Exists(c.Target)) { log?.Invoke($"    目录已不存在：{c.Target}"); return; }

                // 删除期复验（与扫描期同一套判定）：目标可能来自注册表脏数据
                string? unsafeWhy = SafeTargets.Reject(c.Target);
                if (AppPathMatcher.IsTooBroadPath(c.Target) || unsafeWhy is not null)
                {
                    string reason = unsafeWhy ?? "位置过宽（盘根/平台目录/用户目录）";
                    r.Errors.Add($"{c.Target}: 已拒绝删除（{reason}）。");
                    log?.Invoke($"    [拒绝] {c.Target} —— {reason}");
                    return;
                }

                var rr = DirectoryHelper.DeleteDirectory(c.Target);
                r.FreedBytes += rr.FreedBytes;
                r.Errors.AddRange(rr.Errors);
                log?.Invoke(!Directory.Exists(c.Target)
                    ? $"    [完成] 删除目录 {c.Target}"
                    : $"    [部分] 目录 {c.Target} 仍有残留");
                return;

            case LeftoverKind.Shortcut:
                if (DirectoryHelper.DeleteFile(c.Target, r.Errors))
                    log?.Invoke($"    [完成] 删除快捷方式 {c.Target}");
                return;

            case LeftoverKind.RegistryKey:
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(c.Hive, c.View);
                    baseKey.DeleteSubKeyTree(c.KeyPath!, throwOnMissingSubKey: false);
                    log?.Invoke($"    [完成] 删除注册表项 {c.Target}");
                }
                catch (Exception ex)
                {
                    r.Errors.Add($"删除注册表项 {c.Target} 失败：{ex.Message}");
                }
                return;
        }
    }

    // ---------- 安装目录候选 ----------

    private static bool IsPathExists(string? path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path);

    private static void AddDirCandidate(InstalledApp app, string path, string rule, List<LeftoverCandidate> list)
    {
        // 注册表里的 InstallLocation 是**脏数据高发区**：把父目录（D:\、C:\Program Files、
        // C:\Games、%LOCALAPPDATA%\Programs）写成安装位置的软件并不少见。这里必须在入列前就
        // 用与软件占用解析同一套口径（AppPathMatcher.IsTooBroadPath）+ 删除安全闸门过滤，
        // 否则用户勾选一行就可能清空整个共享目录。
        if (AppPathMatcher.IsTooBroadPath(path))
        {
            OperationLog.LogSystem($"残留候选已跳过（安装位置过宽）：{path}（{app.DisplayName}）");
            return;
        }
        if (SafeTargets.Reject(path) is { } why)
        {
            OperationLog.LogSystem($"残留候选已跳过（{why}）：{path}（{app.DisplayName}）");
            return;
        }

        Add(list, new LeftoverCandidate
        {
            Kind = LeftoverKind.Directory,
            Target = path,
            MatchedRule = rule,
            Risk = "可能被多个程序共用的目录，删除前请确认其中没有其他软件文件",
            RequiresElevation = PathNeedsElevation(path),
        });
    }

    /// <summary>从 UninstallString 推断安装目录（仅当安装位置为空时才用）：解析出裸 exe 路径，排除系统目录与 msiexec。</summary>
    private static string? TryDeriveInstallDir(InstalledApp app)
    {
        if (app.IsUwp || app.IsMsi || string.IsNullOrWhiteSpace(app.UninstallString)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(app.UninstallString);
        var (exe, _) = UninstallRunner.ParseCommandLine(expanded);

        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        string fileName = Path.GetFileName(exe);
        if (fileName.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase)) return null;

        string dir = Path.GetDirectoryName(exe) ?? string.Empty;
        if (string.IsNullOrEmpty(dir) || dir.Length < 4 || !Directory.Exists(dir)) return null;

        // 系统目录不作为残留候选（如 cmd / system32 下的卸载器）；Windows 目录随系统盘变化
        if (AppPathMatcher.IsSystemPath(dir)) return null;

        return dir;
    }

    // ---------- 目录候选 ----------

    private static void ScanDirTree(string root, InstalledApp app, List<LeftoverCandidate> list)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        if (list.Count >= MaxCandidates) return;

        var displayTokens = NameTokens(app.DisplayName);
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        int visited = 0;

        while (stack.Count > 0 && visited < DirScanCap && list.Count < MaxCandidates)
        {
            var (dir, depth) = stack.Pop();
            if (depth >= 2) continue;
            if (IsReparsePoint(dir)) continue;
            visited++;

            foreach (var sub in SafeGetDirs(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("Packages", StringComparison.OrdinalIgnoreCase)) continue; // 商店包数据，不扫

                string? rule = MatchDir(sub, depth + 1, name, displayTokens, app);
                if (rule != null)
                {
                    Add(list, new LeftoverCandidate
                    {
                        Kind = LeftoverKind.Directory,
                        Target = sub,
                        MatchedRule = rule,
                        RequiresElevation = PathNeedsElevation(sub),
                    });
                }
                stack.Push((sub, depth + 1));
            }
        }
    }

    private static string? MatchDir(string path, int depth, string name, string[] displayTokens, InstalledApp app)
    {
        // 规则 1：目录名 == 显示名 / 发布者
        // 「发布者」一级匹配太宽：Publisher = "Tencent" 会让 %APPDATA%\Tencent（腾讯全家桶的数据根）
        // 变成候选；"Microsoft"/"Google"/"Adobe" 同理。因此发布者命中必须先过通用名过滤。
        if (string.Equals(name, app.DisplayName, StringComparison.OrdinalIgnoreCase))
            return "目录名 == 显示名";
        if (!string.IsNullOrEmpty(app.Publisher) &&
            !AppPathMatcher.IsGenericName(app.Publisher) &&
            string.Equals(name, app.Publisher, StringComparison.OrdinalIgnoreCase))
            return "目录名 == 发布者";

        // 规则 2：末两级路径 == 显示名首末 token（如 Google\Chrome ← “Google Chrome”）
        var p = path.TrimEnd('\\').Split('\\');
        if (depth >= 2 && displayTokens.Length >= 2 && p.Length >= 2 &&
            p[^2].Equals(displayTokens[0], StringComparison.OrdinalIgnoreCase) &&
            p[^1].Equals(displayTokens[^1], StringComparison.OrdinalIgnoreCase))
            return "路径与显示名一致（如 Google\\Chrome）";
        return null;
    }

    // ---------- 快捷方式候选 ----------

    private static void ScanShortcuts(string root, InstalledApp app, List<LeftoverCandidate> list)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || list.Count >= MaxCandidates) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                if (list.Count >= MaxCandidates) break;
                // Win32 通配符有短名（8.3）怪癖："*.lnk" 也可能匹配到 foo.lnkx，删除前再确认一次扩展名
                if (!Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(f);
                if (string.Equals(name, app.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(app.Publisher) && string.Equals(name, app.Publisher, StringComparison.OrdinalIgnoreCase)))
                {
                    Add(list, new LeftoverCandidate
                    {
                        Kind = LeftoverKind.Shortcut,
                        Target = f,
                        MatchedRule = "开始菜单/桌面快捷方式文件名匹配",
                        RequiresElevation = f.StartsWith(PublicDesktop, StringComparison.OrdinalIgnoreCase) ||
                                             f.StartsWith(PublicStartMenu, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
        }
        catch (Exception) { /* 无权限或不存在则跳过 */ }
    }

    // ---------- 注册表候选 ----------

    private static void ScanRegistryCandidates(InstalledApp app, List<LeftoverCandidate> list)
    {
        if (list.Count >= MaxCandidates) return;

        // App Paths（64/32 两视图）
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                 })
        {
            if (list.Count >= MaxCandidates) break;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var ap = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (ap == null) continue;
                foreach (var subName in ap.GetSubKeyNames())
                {
                    string? val = ap.GetValue(subName, null) as string;
                    bool nameHit = string.Equals(subName, app.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                                   (!string.IsNullOrEmpty(app.Publisher) && string.Equals(subName, app.Publisher, StringComparison.OrdinalIgnoreCase));
                    bool pathHit = !string.IsNullOrEmpty(app.InstallLocation) && !string.IsNullOrEmpty(val) &&
                                   val.StartsWith(app.InstallLocation, StringComparison.OrdinalIgnoreCase);
                    if (nameHit || pathHit)
                    {
                        Add(list, new LeftoverCandidate
                        {
                            Kind = LeftoverKind.RegistryKey,
                            Target = $"{(view == RegistryView.Registry32 ? "HKLM(32位)" : "HKLM")}\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\{subName}",
                            MatchedRule = nameHit ? "App Paths 子键名匹配" : "App Paths 指向安装位置",
                            RequiresElevation = true,
                            Hive = hive,
                            View = view,
                            KeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{subName}",
                        });
                    }
                }
            }
            catch (Exception) { }
        }

        // Software 根：一级匹配 + 按显示名首 token 下探一级
        var firstTokens = NameTokens(app.DisplayName)
            .Concat(NameTokens(app.Publisher ?? string.Empty))
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (firstTokens.Length == 0) return;

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            if (list.Count >= MaxCandidates) break;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var sw = baseKey.OpenSubKey(@"SOFTWARE");
                if (sw == null) continue;

                int checkedKeys = 0;
                foreach (var subName in sw.GetSubKeyNames())
                {
                    if (++checkedKeys > RegistryScanCap) break;

                    // 一级匹配：**只认显示名，不认发布者**。
                    // 若按发布者命中，HKCU|HKLM\SOFTWARE\<厂商> 会被整体 DeleteSubKeyTree，
                    // 一次确认就删掉该厂商**所有产品**的设置（Tencent/Valve/NVIDIA/Adobe…），
                    // 而 HKCU 那一支连管理员权限都不需要。
                    bool firstLevelOk = string.Equals(subName, app.DisplayName, StringComparison.OrdinalIgnoreCase);
                    if (firstLevelOk && !AppPathMatcher.IsGenericName(subName))
                    {
                        AddReg(list, hive, view, subName, "Software 根一级子键名 == 显示名");
                    }
                    else if (firstTokens.Contains(subName, StringComparer.OrdinalIgnoreCase))
                    {
                        // 下探一级（只枚举名字，不读值）
                        using var sub = sw.OpenSubKey(subName);
                        if (sub == null) continue;
                        foreach (var subSub in sub.GetSubKeyNames())
                        {
                            if (++checkedKeys > RegistryScanCap) break;
                            string rule;
                            if (string.Equals(subSub, app.DisplayName, StringComparison.OrdinalIgnoreCase))
                                rule = "二级子键名 == 显示名";
                            else if (!string.IsNullOrEmpty(app.Publisher) &&
                                     !AppPathMatcher.IsGenericName(app.Publisher) &&
                                     string.Equals(subSub, app.Publisher, StringComparison.OrdinalIgnoreCase))
                                rule = "二级子键名 == 发布者";
                            else if (string.Equals(subName, FirstToken(app.DisplayName), StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(subSub, SecondToken(app.DisplayName), StringComparison.OrdinalIgnoreCase))
                                rule = "路径与显示名一致（如 Google\\Chrome）";
                            else
                                continue;

                            AddReg(list, hive, view, $"{subName}\\{subSub}", rule);
                        }
                    }
                }
            }
            catch (Exception) { }
        }
    }

    private static void AddReg(List<LeftoverCandidate> list, RegistryHive hive, RegistryView view,
        string keyPath, string rule)
    {
        Add(list, new LeftoverCandidate
        {
            Kind = LeftoverKind.RegistryKey,
            Target = $"{(hive == RegistryHive.LocalMachine ? (view == RegistryView.Registry32 ? "HKLM(32位)" : "HKLM") : "HKCU")}\\SOFTWARE\\{keyPath}",
            MatchedRule = rule,
            Risk = "仅删除该精确键及其子树；不会扫描其他引用",
            RequiresElevation = hive == RegistryHive.LocalMachine,
            Hive = hive,
            View = view,
            KeyPath = $@"SOFTWARE\{keyPath}",
        });
    }

    // ---------- 通用 ----------

    private static void Add(List<LeftoverCandidate> list, LeftoverCandidate c)
    {
        if (list.Any(x => x.Kind == c.Kind && string.Equals(x.Target, c.Target, StringComparison.OrdinalIgnoreCase)))
            return;
        list.Add(c);
    }

    private static string[] NameTokens(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string FirstToken(string s)
    {
        var t = NameTokens(s);
        return t.Length > 0 ? t[0] : string.Empty;
    }

    private static string SecondToken(string s)
    {
        var t = NameTokens(s);
        return t.Length > 1 ? t[1] : string.Empty;
    }

    private static bool PathNeedsElevation(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3) return false;
        // 用带分隔符边界的比较：裸 StartsWith 会让 "C:\Program Files Extra\..." 误判为 Program Files
        return SafeTargets.IsSameOrUnder(path, ProgramFiles)
            || SafeTargets.IsSameOrUnder(path, ProgramFilesX86)
            || SafeTargets.IsSameOrUnder(path, ProgramData);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
