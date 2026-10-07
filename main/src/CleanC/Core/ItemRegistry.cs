namespace CleanC.Core;

/// <summary>
/// 定义全部“可清理项”。中等清理范围：
/// 用户临时文件 + 各类软件缓存 + 第三方软件缓存（探测本机已装软件，见 ThirdPartyCacheScanner）
/// + Windows 临时文件 + Windows Update 下载缓存 + 缩略图缓存 + 清空回收站。
/// 刻意排除 Downloads（用户个人文件）、Prefetch、WinSxS、系统还原/休眠文件（列为深度项，见 README）。
/// </summary>
public static class ItemRegistry
{
    // 用户级目录一律走 UserContext：提权到别的账户时它仍指向**当前登录用户**
    private static readonly string LocalAppData = UserContext.LocalAppData;
    private static readonly string UserProfile = UserContext.Profile;
    // 优先用 SpecialFolder.Windows（随系统盘变化），环境变量只在缺失时兜底
    private static readonly string SystemRoot = ResolveSystemRoot();
    private static readonly string SystemDrive = Path.GetPathRoot(SystemRoot)?.TrimEnd('\\') ?? "C:";

    // 目标路径
    // 用户临时目录：Path.GetTempPath() 在 TMP/TEMP/USERPROFILE 全部缺失时返回 **Windows 目录**
    // （已实测），因此这里必须校验；不安全时该项不生成（见 CreateAll）。
    private static readonly string? UserTemp = ResolveUserTemp();
    private static readonly string WinTemp = Path.Combine(SystemRoot, "Temp");
    private static readonly string WuDownload = Path.Combine(SystemRoot, @"SoftwareDistribution\Download");

    // 各工具链缓存：优先尊重环境变量（用户把缓存挪到别的盘时仍然能命中），否则用默认位置
    private static readonly string NpmCache =
        Env("npm_config_cache") ?? Path.Combine(LocalAppData, "npm-cache");
    private static readonly string PnpmStore =
        Env("PNPM_HOME") is { } pnpmHome ? Path.Combine(pnpmHome, "store") : Path.Combine(LocalAppData, @"pnpm\store");
    private static readonly string PnpmCache = Path.Combine(LocalAppData, "pnpm-cache");
    private static readonly string PipCache =
        Env("PIP_CACHE_DIR") ?? Path.Combine(LocalAppData, @"pip\cache");
    private static readonly string NugetHttpCache = Path.Combine(LocalAppData, @"NuGet\v3-cache");
    private static readonly string NugetGlobalPackages =
        Env("NUGET_PACKAGES") ?? Path.Combine(UserProfile, @".nuget\packages");
    private static readonly string ExplorerCacheDir = Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer");
    private static readonly string CrashDumps = Path.Combine(LocalAppData, "CrashDumps");
    private static readonly string WerReports = Path.Combine(LocalAppData, @"Microsoft\Windows\WER");
    private static readonly string D3DShaderCache = Path.Combine(LocalAppData, "D3DSCache");
    private static readonly string NvidiaDxCache = Path.Combine(LocalAppData, @"NVIDIA\DXCache");
    private static readonly string NvidiaGlCache = Path.Combine(LocalAppData, @"NVIDIA\GLCache");
    private static readonly string INetCache = Path.Combine(LocalAppData, @"Microsoft\Windows\INetCache");

    private const string CatUser = "用户与缓存";
    private const string CatSystem = "系统（需管理员）";
    private const string CatMisc = "系统与杂项";

    /// <summary>返回全部清理项（目标路径全部不存在的项不列出，避免“0 B”噪音）。</summary>
    public static IReadOnlyList<CleanItem> CreateAll()
    {
        var list = new List<CleanItem>();

        // 用户临时文件：路径解析失败（TMP/TEMP/USERPROFILE 异常）时**不生成该项**，
        // 而不是退回 Path.GetTempPath() 的 Windows 目录兜底（实测会同到 C:\WINDOWS）。
        string? userTempRaw = UserTemp;
        if (!string.IsNullOrEmpty(userTempRaw))
        {
            string userTemp = userTempRaw;
            list.Add(FolderItem("user-temp", "用户临时文件", CatUser,
                $"Windows 与各程序的临时文件（{userTemp}），最安全、最常清理。",
                userTemp, recommended:true, elevation:false, warning:null));
        }

        list.AddRange(new[]
        {
            FolderItem("npm-cache", "npm 缓存", CatUser,
                "npm 安装包下载缓存（%LocalAppData%\\npm-cache），需重新下载。",
                NpmCache, recommended:true, elevation:false, warning:null),

            FolderItem("pnpm-cache", "pnpm 缓存", CatUser,
                "pnpm 仓库与缓存（store / pnpm-cache），清理后需重新下载。",
                PnpmStore, recommended:true, elevation:false, warning:null, extraPath:PnpmCache),

            FolderItem("pip-cache", "pip 缓存", CatUser,
                "Python pip 安装包缓存（%LocalAppData%\\pip\\cache）。",
                PipCache, recommended:true, elevation:false, warning:null),

            FolderItem("nuget-http-cache", "NuGet HTTP 缓存", CatUser,
                "NuGet 包下载缓存（%LocalAppData%\\NuGet\\v3-cache）。",
                NugetHttpCache, recommended:true, elevation:false, warning:null),

            FolderItem("nuget-global", "NuGet 全局包", CatUser,
                "已还原的 NuGet 包（.nuget\\packages），恢复项目时重新下载。",
                NugetGlobalPackages, recommended:false, elevation:false,
                warning:"默认不勾选（需重新下载）。"),

            FolderItemMulti("crash-dumps", "崩溃转储文件", CatUser,
                "程序崩溃时生成的转储（%LocalAppData%\\CrashDumps），仅用于排错，可安全删除。",
                new[] { CrashDumps }, recommended: true, elevation: false,
                warning: "删除后无法再分析历史崩溃现场。"),

            FolderItemMulti("wer-reports", "Windows 错误报告", CatUser,
                "Windows 错误报告存档与队列（%LocalAppData%\\Microsoft\\Windows\\WER）。",
                new[] { WerReports }, recommended: true, elevation: false,
                warning: "仅错误报告数据，不影响系统功能。"),

            FolderItemMulti("d3d-shader-cache", "DirectX 着色器缓存", CatUser,
                "DirectX 编译的着色器缓存（%LocalAppData%\\D3DSCache），删除后游戏/应用首次运行重建。",
                new[] { D3DShaderCache }, recommended: true, elevation: false,
                warning: "首次运行游戏可能出现短暂卡顿。"),

            FolderItemMulti("nvidia-shader-cache", "NVIDIA 着色器缓存", CatUser,
                "NVIDIA 驱动的 DXCache / GLCache 着色器缓存，删除后自动重建（仅 NVIDIA 机器存在）。",
                new[] { NvidiaDxCache, NvidiaGlCache }, recommended: true, elevation: false,
                warning: "首次运行游戏可能出现短暂卡顿。"),

            FolderItemMulti("inet-cache", "系统网络缓存（INetCache）", CatUser,
                "WinINet 网页/下载临时缓存（%LocalAppData%\\Microsoft\\Windows\\INetCache）。",
                new[] { INetCache }, recommended: true, elevation: false,
                warning: "被占用的文件自动跳过。"),

            ThumbnailItem(),

            FolderItem("win-temp", "Windows 临时文件", CatSystem,
                $"系统临时目录（{WinTemp}），需要管理员权限。",
                WinTemp, recommended:true, elevation:true, warning:"需要管理员权限。"),

            WuUpdateItem(),

            RecycleBinItem(),
        });

        // 目标路径全部不存在的项不列出（未安装/已清理/非本机存在的目录）
        var visible = list.Where(i => i.Paths.Count > 0 && i.Paths.Any(PathExists)).ToList();

        // 安全闸门：目标可能来自环境变量，在别的电脑上完全可能指向盘根/系统目录，一律不列出
        for (int i = visible.Count - 1; i >= 0; i--)
        {
            var item = visible[i];
            string? why = item.Paths.Select(SafeTargets.Reject).FirstOrDefault(r => r is not null);
            if (why is null) continue;
            OperationLog.LogSystem($"清理项「{item.Name}」已跳过：目标路径不安全（{why}）：" +
                                   string.Join("；", item.Paths));
            visible.RemoveAt(i);
        }

        // 第三方软件缓存：仅显示本机已安装（缓存目录存在）的软件，见 ThirdPartyCacheScanner
        visible.AddRange(ThirdPartyCacheScanner.CreateItems());
        return visible;
    }

    private static bool PathExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path) || File.Exists(path); }
        catch (Exception) { return false; }
    }

    // ---------- 路径解析（均带兜底与安全校验，见 SafeTargets） ----------

    /// <summary>Windows 目录：优先 SpecialFolder.Windows（随系统盘变化），最后才用 SystemRoot 环境变量。</summary>
    private static string ResolveSystemRoot()
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

        return @"C:\Windows";
    }

    /// <summary>
    /// 用户临时目录。**绝不**直接采信 <c>Path.GetTempPath()</c>：实测在 TMP/TEMP/USERPROFILE
    /// 全部缺失时它返回 Windows 目录，会导致「用户临时文件」去清空 Windows。
    /// 取 UserContext 解析出的 <c>&lt;LocalAppData&gt;\Temp</c>（已过安全闸门）；不可用则返回 null（该项不生成）。
    /// </summary>
    private static string? ResolveUserTemp()
    {
        if (UserContext.TempDir is { Length: > 0 } t) return t;

        OperationLog.LogSystem("用户临时目录无法安全确定（TEMP/TMP 缺失或指向系统目录），" +
                               "已跳过「用户临时文件」清理项。");
        return null;
    }

    private static string? Env(string name)
    {
        try
        {
            string? v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- 通用构造 ----------

    private static CleanItem FolderItem(
        string id, string name, string category, string description,
        string path, bool recommended, bool elevation, string? warning, string? extraPath = null)
    {
        var paths = new List<string> { path };
        if (!string.IsNullOrEmpty(extraPath)) paths.Add(extraPath);
        return FolderItemMulti(id, name, category, description, paths, recommended, elevation, warning);
    }

    /// <summary>多路径清理项：分析统计全部路径，清理逐个清空内容（保留目录本身）。</summary>
    private static CleanItem FolderItemMulti(
        string id, string name, string category, string description,
        IReadOnlyList<string> paths, bool recommended, bool elevation, string? warning)
    {
        var snapshot = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return new CleanItem
        {
            Id = id,
            Name = name,
            Category = category,
            Description = description,
            Recommended = recommended,
            RequiresElevation = elevation,
            Warning = warning,
            Paths = snapshot,
            Estimate = ct => Task.Run(() =>
            {
                long total = 0;
                foreach (var p in snapshot)
                    total += DirectoryHelper.MeasureDirectory(p, MeasureLimits.Unlimited, null, ct).Bytes;
                return total;
            }, ct),
            Clean = ct => Task.Run(() =>
            {
                var r = new CleanResult();
                foreach (var p in snapshot)
                {
                    var rr = DirectoryHelper.DeleteFolderContents(p);
                    r.FreedBytes += rr.FreedBytes;
                    r.Errors.AddRange(rr.Errors.Select(e => $"{Path.GetFileName(p)}: {e}"));
                }
                return r;
            }, ct)
        };
    }

    private static CleanItem ThumbnailItem()
    {
        var targetDir = ExplorerCacheDir;
        string[] Files()
        {
            try { return Directory.GetFiles(targetDir, "thumbcache_*.db"); }
            catch (Exception) { return Array.Empty<string>(); }
        }
        return new CleanItem
        {
            Id = "thumbcache",
            Name = "缩略图缓存",
            Category = CatUser,
            Description = "资源管理器缩略图缓存（thumbcache_*.db），删除后自动重建。",
            Recommended = true,
            RequiresElevation = false,
            Warning = "部分文件需重启后刷新。",
            Paths = new[] { targetDir },
            Estimate = ct => Task.Run(() =>
            {
                long t = 0;
                foreach (var f in Files())
                    try { t += new FileInfo(f).Length; } catch (Exception) { }
                return t;
            }, ct),
            Clean = ct => Task.Run<CleanResult>(() =>
            {
                var r = new CleanResult();
                foreach (var f in Files())
                {
                    long len = 0;
                    try { len = new FileInfo(f).Length; } catch (Exception) { }
                    if (DirectoryHelper.DeleteFile(f, r.Errors))
                        r.FreedBytes += len;
                }
                return r;
            }, ct)
        };
    }

    private static CleanItem WuUpdateItem()
    {
        return new CleanItem
        {
            Id = "wu-download",
            Name = "Windows Update 下载缓存",
            Category = CatSystem,
            Description = "Windows 更新下载包（SoftwareDistribution\\Download），需停止更新服务。",
            Recommended = true,
            RequiresElevation = true,
            Warning = "需管理员权限；停止更新服务清理。",
            Paths = new[] { WuDownload },
            Estimate = ct => Task.Run(() => DirectoryHelper.GetDirectorySize(WuDownload), ct),
            Clean = ct => Task.Run<CleanResult>(() =>
            {
                ElevationHelper.ControlWindowsService("stop", "wuauserv");
                ElevationHelper.ControlWindowsService("stop", "bits");
                try
                {
                    return DirectoryHelper.DeleteFolderContents(WuDownload);
                }
                finally
                {
                    ElevationHelper.ControlWindowsService("start", "wuauserv");
                    ElevationHelper.ControlWindowsService("start", "bits");
                }
            }, ct)
        };
    }

    private static CleanItem RecycleBinItem()
    {
        // 回收站是按卷的：位置列要列出所有固定卷，否则多分区机器上会以为只清了 C 盘。
        var roots = DirectoryHelper.RecycleBinRoots();
        return new CleanItem
        {
            Id = "recycle-bin",
            Name = "清空回收站",
            Category = CatMisc,
            Description = $"删除回收站中的文件（当前用户，共 {roots.Count} 个卷；永久清空，不可恢复）。",
            Recommended = true,
            RequiresElevation = false,
            Warning = "永久清空，无法再恢复。",
            Estimate = ct => Task.Run(() => DirectoryHelper.GetRecycleBinSize(), ct),
            Paths = roots.Count > 0 ? roots : new[] { Path.Combine(SystemDrive + "\\", "$Recycle.Bin") },
            Clean = ct => Task.Run<CleanResult>(() =>
            {
                var r = new CleanResult();
                long before = DirectoryHelper.GetRecycleBinSize();
                bool ok = DirectoryHelper.EmptyRecycleBin(r.Errors);
                if (ok)
                    r.FreedBytes = before;
                else
                    r.Errors.Add("回收站可能已空，或未获得访问权限。");

                // 回收站是**按账户**隔离的：提权到别的管理员账户时，系统只会清提权账户的那个。
                // 这时如实说明，并给出可执行的做法（用普通权限再跑一次本程序）。
                if (UserContext.IsOtherUser)
                    r.Errors.Add("提示：当前以其它账户的管理员权限运行，系统清理的是该账户的回收站。" +
                                 "要清空你自己的回收站，请以普通权限（直接双击）再运行一次本程序并只勾选本项。");
                return r;
            }, ct)
        };
    }
}
