namespace CleanC.Core;

/// <summary>开发者环境扫描范围。</summary>
public enum ScanScope
{
    /// <summary>当前用户目录 + 已知常用位置（快）。</summary>
    UserProfile,

    /// <summary>整个系统盘（慢）；仅进入当前用户的 Users 子树，系统目录一律绕过。</summary>
    WholeDrive,

    /// <summary>所有就绪的固定磁盘（最全，最慢）；系统目录同样绕过。</summary>
    AllFixedDrives,
}

/// <summary>扫描过程中的回调集合（全部在后台线程触发，由调用方回贴 UI）。</summary>
public sealed class DevScanCallbacks
{
    public Action<string>? Log { get; init; }
    public Action<int>? DirsVisited { get; init; }

    /// <summary>测量进度（已完成, 总数）。</summary>
    public Action<int, int>? MeasureProgress { get; init; }
}

/// <summary>一次扫描的结果。</summary>
public sealed class DevScanResult
{
    public required IReadOnlyList<DevItem> Items { get; init; }
    public int VisitedDirs { get; init; }

    /// <summary>仅统计（不提供删除）的条目数与其合计占用。</summary>
    public int ReportOnlyCount { get; init; }
    public long ReportOnlyBytes { get; init; }

    /// <summary>与上层条目重叠的条目数。</summary>
    public int OverlapCount { get; init; }

    /// <summary>可清理项合计（不含重叠项与仅统计项）。</summary>
    public long DeletableBytes { get; init; }

    public int TruncatedCount { get; init; }
    public int DeniedCount { get; init; }
    public int ElapsedMs { get; init; }

    /// <summary>因条目数上限而未列入的条目数。</summary>
    public int HiddenCount { get; init; }
}

/// <summary>
/// 开发者环境扫描/删除引擎：发现虚拟环境、依赖目录、构建产物、全局包与各类包缓存，
/// 并在删除前逐项做“范围 + 签名 + 符号链接”三重安全复验。
/// 工具链本体、虚拟磁盘等只统计（IsReportOnly），不提供删除。
/// 不接触 UI 控件，由调用方负责跨线程回贴。
/// </summary>
public sealed class DevScanner
{
    // ---------- 常量路径 ----------
    // 用户级目录一律走 UserContext：提权到别的账户时它仍指向**当前登录用户**
    private static readonly string LocalAppData = UserContext.LocalAppData;
    private static readonly string AppData = UserContext.RoamingAppData;
    private static readonly string UserProfile = UserContext.Profile;
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string DriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    // ---------- 分类 ----------
    private const string CatPython = "Python 虚拟环境";
    private const string CatNode = "Node.js 相关";
    private const string CatRust = "Rust 相关";
    private const string CatGo = "Go 相关";
    private const string CatJava = "Java 相关";
    private const string CatPhp = "PHP 相关";
    private const string CatCache = "语言包缓存";
    private const string CatAi = "AI / 模型缓存";
    private const string CatBuild = "项目构建产物";
    private const string CatIde = "IDE 相关";
    private const string CatToolchain = "工具链本体（仅统计）";
    private const string CatVirtualDisk = "虚拟磁盘与容器（仅统计）";

    private const int MaxDetectedItems = 800;      // 最终列表上限（超出按大小保留最大者）
    private const int DetectionHardCap = 3000;     // 探测阶段上限（防止超大仓库撑爆内存）
    private const int MaxPycGroups = 60;           // Python 缓存聚合分组上限
    private const int MaxPycPathsPerGroup = 200;   // 每组聚合子目录上限

    // ---------- 状态（每次扫描重建） ----------
    private readonly List<string> _allowedRoots = new();
    private readonly List<string> _trustedPaths = new();
    private readonly HashSet<string> _exactSkip = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pycGroups = new(StringComparer.OrdinalIgnoreCase);
    private int _visited;
    private int _hidden;

    /// <summary>
    /// 扫描：先探测已知位置（快速、确定性），再对扫描根递归遍历检测环境/依赖/构建产物目录，
    /// 最后并行测量各项大小。返回的每一项均绑定检测签名，供删除前复验。
    /// </summary>
    public async Task<DevScanResult> ScanAsync(ScanScope scope, IReadOnlyList<string> extraRoots,
        DevScanCallbacks? callbacks, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _allowedRoots.Clear();
            _trustedPaths.Clear();
            _exactSkip.Clear();
            _pycGroups.Clear();
            _visited = 0;
            _hidden = 0;
            ct.ThrowIfCancellationRequested();

            // 1) 扫描根
            var roots = new List<string>();
            if (scope == ScanScope.UserProfile)
            {
                roots.Add(UserProfile);
            }
            else if (scope == ScanScope.WholeDrive)
            {
                roots.Add(DriveRoot);
            }
            else
            {
                roots.Add(UserProfile);
                foreach (var d in SafeGetFixedDrives())
                    if (!roots.Contains(d, StringComparer.OrdinalIgnoreCase))
                        roots.Add(d);
            }
            foreach (var r in extraRoots)
                if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r) && !roots.Contains(r, StringComparer.OrdinalIgnoreCase))
                    roots.Add(r);

            // 已被更上层扫描根覆盖的根（如用户目录已在 C:\ 之下）不再单独遍历，避免重复走查
            roots = roots
                .Where(r => !roots.Any(other =>
                    !string.Equals(other, r, StringComparison.OrdinalIgnoreCase) &&
                    r.StartsWith(other.TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _allowedRoots.AddRange(roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase));
            callbacks?.Log?.Invoke($"扫描范围：{string.Join("、", _allowedRoots)}");

            // 已由已知路径探测覆盖的目录，遍历时不再进入（省时且避免重复）
            _exactSkip.Add(Normalize(Path.Combine(UserProfile, "go")));
            _exactSkip.Add(Normalize(Path.Combine(UserProfile, ".nuget")));

            var items = new Dictionary<string, DevItem>(StringComparer.OrdinalIgnoreCase);

            AddProbes(items, callbacks?.Log, ct);
            AddCondaEnvs(items, callbacks?.Log, ct);
            foreach (var root in _allowedRoots)
                Walk(root, scope != ScanScope.UserProfile, items, callbacks?.Log, callbacks?.DirsVisited, ct);

            AddPycCacheGroups(items, callbacks?.Log);

            // 测量大小（并行；只读）
            var list = items.Values.ToList();
            MeasureItems(list, callbacks, ct);

            // 重叠标记（父子关系：合计只计上层条目）
            MarkOverlaps(list);

            // 条目上限：按大小保留最大的若干项
            if (list.Count > MaxDetectedItems)
            {
                var keep = list.OrderByDescending(i => i.Size).Take(MaxDetectedItems).ToHashSet();
                _hidden += list.Count - keep.Count;
                list = list.Where(keep.Contains).ToList();
            }

            list = list
                .OrderByDescending(i => i.Size)
                .ThenBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();

            long deletable = 0, reportOnly = 0;
            foreach (var it in list)
            {
                if (it.IsOverlapping) continue;
                if (it.IsReportOnly) reportOnly += it.Size;
                else deletable += it.Size;
            }

            var result = new DevScanResult
            {
                Items = list,
                VisitedDirs = _visited,
                ReportOnlyCount = list.Count(i => i.IsReportOnly),
                ReportOnlyBytes = reportOnly,
                OverlapCount = list.Count(i => i.IsOverlapping),
                DeletableBytes = deletable,
                TruncatedCount = list.Count(i => i.Truncated),
                DeniedCount = list.Count(i => i.Denied),
                ElapsedMs = (int)sw.ElapsedMilliseconds,
                HiddenCount = _hidden,
            };
            callbacks?.Log?.Invoke($"扫描完成：共检查 {_visited} 个目录，发现 {list.Count} 项" +
                                   $"（可清理 {SizeFormatter.Format(deletable)} / 仅统计 {SizeFormatter.Format(reportOnly)}），" +
                                   $"用时 {sw.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s。");
            return result;
        }, ct);
    }

    // ---------- 已知路径探测 ----------

    /// <summary>探测规格：只做确定性路径判断，存在才生成条目。</summary>
    private sealed record ProbeSpec(
        string Type,
        string Category,
        string Path,
        string Description,
        string? Warning,
        bool Recommended,
        bool Elevation,
        Func<string, (int Count, List<string> Top)>? Counter = null,
        bool ReportOnly = false,
        bool IsFile = false,
        string? Name = null);

    private void AddProbes(Dictionary<string, DevItem> items, Action<string>? log, CancellationToken ct)
    {
        var specs = new List<ProbeSpec>
        {
            // ---------- Node.js ----------
            new("npm 全局包", CatNode, Path.Combine(AppData, "npm", "node_modules"),
                "npm 全局安装的命令行工具（npm install -g 的产物）。删除后需重新执行 npm install -g。",
                "全局命令行工具将被移除，需重新安装（默认不勾选）。", false, false, CountNodeModules),
            new("npm 内容寻址缓存", CatNode, Path.Combine(UserProfile, ".npm", "_cacache"),
                "npm 的 _cacache 内容寻址缓存（包下载缓存）。删除后重新安装时需重新下载。", null, true, false),
            new("Yarn 1.x 包缓存", CatNode, Path.Combine(LocalAppData, "Yarn", "Cache"),
                "Yarn 1.x 的包下载缓存。清理后下次安装需重新下载。", null, true, false),
            new("Yarn Berry 包缓存", CatNode, Path.Combine(LocalAppData, "Yarn", "Berry", "cache"),
                "Yarn Berry (v2+) 的包缓存。清理后下次安装需重新下载。", null, true, false),
            new("Yarn Berry 包缓存（用户目录）", CatNode, Path.Combine(UserProfile, ".yarn", "berry", "cache"),
                "Yarn Berry 在用户目录下的全局缓存（enableGlobalCache 默认布局）。清理后需重新下载。", null, true, false),
            new("pnpm 存储（用户目录布局）", CatNode, Path.Combine(UserProfile, ".pnpm-store"),
                "pnpm 内容寻址存储（旧版/自定义布局）。清理后下次安装需重新下载。", null, true, false),
            new("Bun 包缓存", CatNode, Path.Combine(UserProfile, ".bun", "install", "cache"),
                "Bun 的包下载缓存。清理后下次安装需重新下载。", null, true, false),
            new("Playwright 浏览器缓存", CatNode, Path.Combine(LocalAppData, "ms-playwright"),
                "Playwright 下载的 Chromium/Firefox/WebKit 浏览器二进制。删除后需重新执行 playwright install。",
                "已有项目若再次运行测试会重新下载浏览器（默认不勾选）。", false, false),
            new("Playwright 浏览器缓存（用户目录）", CatNode, Path.Combine(UserProfile, ".cache", "ms-playwright"),
                "Playwright 浏览器二进制（用户目录布局）。删除后需重新执行 playwright install。",
                "已有项目再次运行测试会重新下载（默认不勾选）。", false, false),
            new("Puppeteer 浏览器缓存", CatNode, Path.Combine(UserProfile, ".cache", "puppeteer"),
                "Puppeteer 下载的 Chrome/Chromium 二进制。删除后需重新下载。", "需重新下载浏览器（默认不勾选）。", false, false),
            new("Selenium 驱动缓存", CatNode, Path.Combine(UserProfile, ".cache", "selenium"),
                "Selenium Manager 下载的浏览器驱动。删除后自动重新下载。", null, true, false),
            new("Dart / Flutter pub 缓存", CatNode, Path.Combine(LocalAppData, "Pub", "Cache"),
                "Dart / Flutter 的 pub 包缓存（hosted、git）。删除后需重新 pub get。", null, true, false),

            // ---------- AI / 模型缓存 ----------
            new("HuggingFace 模型缓存", CatAi, Path.Combine(UserProfile, ".cache", "huggingface"),
                "HuggingFace Hub 下载的模型与数据集缓存（hub、datasets）。删除后需重新下载。",
                "模型体积可能数十 GB，删除后需联网重新下载（默认不勾选）。", false, false),
            new("PyTorch 预训练权重缓存", CatAi, Path.Combine(UserProfile, ".cache", "torch"),
                "PyTorch Hub / torch.hub 下载的预训练权重缓存。删除后需重新下载。",
                "删除后需联网重新下载（默认不勾选）。", false, false),
            new("Whisper 模型缓存", CatAi, Path.Combine(UserProfile, ".cache", "whisper"),
                "OpenAI Whisper 语音模型缓存。删除后需重新下载。", null, false, false),
            new("KaggleHub 下载缓存", CatAi, Path.Combine(UserProfile, ".cache", "kagglehub"),
                "KaggleHub 数据集/模型下载缓存。删除后需重新下载。", null, true, false),
            new("Keras 模型缓存", CatAi, Path.Combine(UserProfile, ".keras"),
                "Keras 保存/下载的模型缓存。删除后需重新下载。", null, false, false),
            new("Matplotlib 缓存", CatAi, Path.Combine(UserProfile, ".matplotlib"),
                "Matplotlib 字体与示例缓存，删除后首次绘图自动重建。", null, true, false),
            new("Ollama 本地模型", CatAi, Path.Combine(UserProfile, ".ollama", "models"),
                "Ollama 下载的大模型文件（blobs、manifests）。删除后需重新 ollama pull。",
                "删除后需要重新下载模型（可能数十 GB，默认不勾选）。", false, false),

            // ---------- Python ----------
            new("Conda 包缓存", CatCache, Path.Combine(UserProfile, ".conda", "pkgs"),
                "Conda 已下载/解包的程序包缓存（pkgs）。删除后创建环境时重新下载。", null, true, false),
            new("Conda 包缓存（anaconda3）", CatCache, Path.Combine(UserProfile, "anaconda3", "pkgs"),
                "Anaconda 安装目录下的包缓存。删除后创建环境时重新下载。", null, true, false),
            new("Conda 包缓存（miniconda3）", CatCache, Path.Combine(UserProfile, "miniconda3", "pkgs"),
                "Miniconda 安装目录下的包缓存。删除后创建环境时重新下载。", null, true, false),
            new("Conda 包缓存（ProgramData）", CatCache, Path.Combine(ProgramData, "anaconda3", "pkgs"),
                "系统级 Anaconda 的包缓存。删除后创建环境时重新下载。", null, true, true),
            new("Pipenv 虚拟环境集合", CatPython, Path.Combine(UserProfile, ".virtualenvs"),
                "Pipenv 管理的全部虚拟环境。删除后每个项目需重新 pipenv install。",
                "所有 Pipenv 环境将被整体删除（默认不勾选）。", false, false),
            new("uv 包缓存", CatCache, Path.Combine(LocalAppData, "uv", "cache"),
                "uv（Python 包管理器）的包下载缓存。清理后下次安装需重新下载。", null, true, false),
            new("uv 包缓存（用户目录）", CatCache, Path.Combine(UserProfile, ".cache", "uv"),
                "uv 在用户目录下的包缓存（XDG 布局）。清理后下次安装需重新下载。", null, true, false),
            new("Poetry 包缓存", CatCache, Path.Combine(LocalAppData, "pypoetry", "Cache"),
                "Poetry 的包下载缓存。清理后下次安装需重新下载。", null, true, false),

            // ---------- Rust / Go ----------
            new("Cargo 注册表缓存", CatRust, Path.Combine(UserProfile, ".cargo", "registry"),
                "Cargo 已下载的 crate 源码与索引缓存（registry）。删除后构建时重新联网下载。", null, true, false,
                d => CountFilesRecursive(Path.Combine(d, "cache"), ".crate")),
            new("Cargo Git 依赖缓存", CatRust, Path.Combine(UserProfile, ".cargo", "git"),
                "Cargo 从 Git 拉取的依赖检出缓存。删除后构建时重新克隆。", null, true, false),
            new("Go 模块缓存", CatGo, Path.Combine(GopathRoot(), "pkg", "mod"),
                "Go 模块下载缓存（GOPATH\\pkg\\mod，含 cache\\download）。删除后 go build 重新下载。", null, true, false,
                d => CountFilesRecursive(Path.Combine(d, "cache", "download"), ".zip")),
            new("Go 构建缓存", CatGo, GocacheRoot(),
                "Go 编译中间产物缓存（GOCACHE）。删除后下次编译更慢，无数据损失。", null, true, false),

            // ---------- Java / Android ----------
            new("Maven 本地仓库", CatJava, Path.Combine(UserProfile, ".m2", "repository"),
                "Maven 下载的依赖仓库（groupId 目录）。删除后构建时从中央仓库重新下载。", null, true, false,
                d => CountFilesRecursive(d, ".pom")),
            new("Gradle 缓存", CatJava, Path.Combine(UserProfile, ".gradle"),
                "Gradle 依赖缓存与 wrapper 发行版（caches、wrapper）。删除后下次构建重新下载。", null, true, false,
                d => CountFilesRecursive(Path.Combine(d, "caches", "modules-2", "files-2.1"), ".jar")),
            new("Kotlin/Native 编译器缓存", CatJava, Path.Combine(UserProfile, ".konan"),
                "Kotlin/Native 下载的编译器与依赖（konan）。删除后重新编译时下载。", null, true, false),
            new("Android 构建缓存", CatJava, Path.Combine(UserProfile, ".android", "build-cache"),
                "Android Gradle 插件的构建缓存。删除后首次构建变慢。", null, true, false),
            new("Android SDK 下载缓存", CatJava, Path.Combine(UserProfile, ".android", "cache"),
                "Android SDK 组件下载缓存。删除后重新下载。", null, true, false),

            // ---------- .NET / Visual Studio ----------
            new(".NET 模板引擎缓存", CatCache, Path.Combine(UserProfile, ".templateengine"),
                "dotnet new 模板缓存（TemplateEngine）。删除后重新生成。", null, true, false),
            new("Visual Studio 安装包缓存", CatIde, Path.Combine(LocalAppData, "Microsoft", "VisualStudio", "Packages"),
                "Visual Studio 安装器的已下载包缓存（可用于修复/修改安装）。删除后修复安装需重新下载。",
                "删除后修复/修改 VS 安装需要重新联网下载（默认不勾选）。", false, false),
            new("VS 解决方案缓存（.vs）", CatIde, Path.Combine(UserProfile, ".vs"),
                "Visual Studio 的 .vs 隐藏目录（IntelliSense、临时索引）。删除后重新打开解决方案时重建。",
                "会丢失部分解决方案级临时状态（默认不勾选）。", false, false),

            // ---------- VS Code / JetBrains / 引擎 ----------
            new("VS Code 扩展", CatIde, Path.Combine(UserProfile, ".vscode", "extensions"),
                "VS Code 已安装的扩展目录。删除后扩展市场中的扩展需重新安装。",
                "已安装扩展将被全部卸载（默认不勾选）。", false, false,
                d =>
                {
                    var dirs = SafeGetDirs(d);
                    return (dirs.Length, dirs.Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Take(20).ToList());
                }),
            new("VS Code 缓存", CatIde, Path.Combine(AppData, "Code", "Cache"),
                "VS Code 的 Cache/CachedData 缓存，删除后自动重建。", null, true, false),
            new("VS Code 日志", CatIde, Path.Combine(AppData, "Code", "logs"),
                "VS Code 运行日志与崩溃记录。删除后自动重建。", null, true, false),
            new("VS Code 工作区状态", CatIde, Path.Combine(AppData, "Code", "User", "workspaceStorage"),
                "VS Code 各工作区的临时状态（展开状态、撤销历史）。删除后工作区状态重置。",
                "会重置各工作区的界面状态（默认不勾选）。", false, false),
            new("JetBrains 守护进程缓存", CatIde, Path.Combine(LocalAppData, "JetBrains", "Daemon"),
                "JetBrains 工具的守护进程缓存，删除后自动重建。", null, true, false),
            new("JetBrains Toolbox 应用", CatToolchain, Path.Combine(LocalAppData, "JetBrains", "Toolbox", "apps"),
                "JetBrains Toolbox 下载的 IDE 本体（含多个版本）。仅供查看占用，本工具不提供删除。",
                null, false, false, ReportOnly: true),
            new("Unity 编辑器缓存", CatIde, Path.Combine(LocalAppData, "Unity", "cache"),
                "Unity 编辑器下载/包缓存。删除后 Unity 重新下载所需包。", null, true, false),
            new("Unreal 派生数据缓存（DDC）", CatIde, Path.Combine(LocalAppData, "UnrealEngine", "Common", "DerivedDataCache"),
                "Unreal Engine 的着色器/派生数据缓存（DDC）。删除后首次打开工程会重新构建。",
                "删除后首次打开工程明显变慢（默认不勾选）。", false, false),
            new("UnrealBuildTool 缓存", CatIde, Path.Combine(LocalAppData, "UnrealBuildTool"),
                "UnrealBuildTool 的编译缓存。删除后重新编译时重建。", null, true, false),
        };

        // JetBrains 各产品目录：caches / system / log / tmp
        foreach (var prod in SafeGetDirs(Path.Combine(LocalAppData, "JetBrains")))
        {
            string product = Path.GetFileName(prod);
            if (product.Equals("Daemon", StringComparison.OrdinalIgnoreCase) ||
                product.Equals("Toolbox", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var sub in new[] { "caches", "system", "log", "tmp" })
            {
                string path = Path.Combine(prod, sub);
                if (!Directory.Exists(path)) continue;
                specs.Add(new($"{product} {sub}", CatIde, path,
                    $"JetBrains IDE（{product}）的 {sub} 目录：索引与缓存。关闭 IDE 后可安全删除，重新打开时自动重建。",
                    null, true, false));
            }
        }

        // 工具链本体（仅统计）
        specs.AddRange(new[]
        {
            new ProbeSpec("Rust 工具链（rustup）", CatToolchain, Path.Combine(UserProfile, ".rustup"),
                "Rust 工具链本体（toolchains、下载缓存）。仅供查看占用，本工具不提供删除。", null, false, false, ReportOnly: true),
            new ProbeSpec(".NET SDK/运行时（用户目录）", CatToolchain, Path.Combine(UserProfile, ".dotnet"),
                ".NET 用户级安装（SDK、运行时、全局工具）。仅供查看占用，本工具不提供删除。", null, false, false, ReportOnly: true),
            new ProbeSpec(".NET SDK/运行时（Program Files）", CatToolchain, Path.Combine(ProgramFiles, "dotnet"),
                "系统级 .NET SDK 与运行时。仅供查看占用，本工具不提供删除。", null, false, false, ReportOnly: true),
            new ProbeSpec("Android SDK 本体", CatToolchain, Path.Combine(LocalAppData, "Android", "Sdk"),
                "Android SDK（platform-tools、build-tools、系统镜像等）。仅供查看占用，本工具不提供删除。",
                null, false, false, ReportOnly: true),
            new ProbeSpec("Android 模拟器镜像（AVD）", CatToolchain, Path.Combine(UserProfile, ".android", "avd"),
                "Android 模拟器的虚拟设备镜像。仅供查看占用，本工具不提供删除（请在 AVD Manager 中删除）。",
                null, false, false, ReportOnly: true),
            new ProbeSpec("Python（用户级安装）", CatToolchain, Path.Combine(LocalAppData, "Programs", "Python"),
                "用户级安装的 Python 解释器与已装包。仅供查看占用，本工具不提供删除。", null, false, false, ReportOnly: true),
            new ProbeSpec("VS Code 本体（用户级安装）", CatToolchain, Path.Combine(LocalAppData, "Programs", "Microsoft VS Code"),
                "VS Code 程序本体。仅供查看占用，本工具不提供删除。", null, false, false, ReportOnly: true),
            new ProbeSpec("AI 工具运行时缓存", CatToolchain, Path.Combine(UserProfile, ".cache", "codex-runtimes"),
                "AI 命令行工具下载的运行时缓存。仅供查看占用，本工具不提供删除（删除会导致工具重新下载运行时）。",
                null, false, false, ReportOnly: true),
        });

        foreach (var spec in specs)
        {
            ct.ThrowIfCancellationRequested();
            AddProbe(items, spec, log);
        }

        // 虚拟磁盘与容器（仅统计，文件型目标）
        foreach (var spec in VirtualDiskSpecs())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in ExpandPathPattern(spec.Path).Take(12))
            {
                var item = new DevItem
                {
                    Id = Normalize(file),
                    Name = spec.Name ?? Path.GetFileName(file),
                    Type = spec.Type,
                    Category = CatVirtualDisk,
                    Path = file,
                    Description = spec.Description,
                    Warning = null,
                    Recommended = false,
                    RequiresElevation = false,
                    Signature = DevSignature.ReportOnlyFile,
                    IsReportOnly = true,
                    IsFileTarget = true,
                };
                if (items.TryAdd(item.Id, item))
                    log?.Invoke($"  发现：{item.Type} = {Path.GetFileName(file)}（{file}）");
            }
        }
    }

    /// <summary>虚拟磁盘 / 容器数据（WSL、Docker Desktop）：仅统计占用与位置，不提供删除。</summary>
    private static IEnumerable<ProbeSpec> VirtualDiskSpecs()
    {
        yield return new ProbeSpec("WSL/Docker 虚拟磁盘", CatVirtualDisk,
            Path.Combine(LocalAppData, "Docker", "wsl", "data", "ext4.vhdx"),
            "Docker Desktop 的 WSL 数据盘（镜像、容器、卷都在其中）。仅供查看占用，本工具不提供删除（删除会丢失全部镜像与容器）。",
            null, false, false, ReportOnly: true, IsFile: true);
        yield return new ProbeSpec("WSL/Docker 虚拟磁盘", CatVirtualDisk,
            Path.Combine(LocalAppData, "Docker", "wsl", "disk", "docker_data.vhdx"),
            "Docker Desktop 的数据盘（新版布局）。仅供查看占用，本工具不提供删除。",
            null, false, false, ReportOnly: true, IsFile: true);
        yield return new ProbeSpec("WSL/Docker 虚拟磁盘", CatVirtualDisk,
            Path.Combine(LocalAppData, "Docker", "wsl", "main", "ext4.vhdx"),
            "Docker Desktop 的 docker-desktop 发行版数据盘。仅供查看占用，本工具不提供删除。",
            null, false, false, ReportOnly: true, IsFile: true);
        yield return new ProbeSpec("WSL 发行版虚拟磁盘", CatVirtualDisk,
            Path.Combine(LocalAppData, "wsl", "*", "ext4.vhdx"),
            "WSL2 发行版的 ext4 虚拟磁盘（含根文件系统）。仅供查看占用，本工具不提供删除（如需收缩请用 wsl --manage --resize 或导出重建）。",
            null, false, false, ReportOnly: true, IsFile: true);
        yield return new ProbeSpec("WSL 发行版虚拟磁盘（商店布局）", CatVirtualDisk,
            Path.Combine(LocalAppData, "Packages", "CanonicalGroupLimited*", "LocalState", "*.vhdx"),
            "商店版 WSL 发行版（Ubuntu 等）的虚拟磁盘。仅供查看占用，本工具不提供删除。",
            null, false, false, ReportOnly: true, IsFile: true);
    }

    private void AddProbe(Dictionary<string, DevItem> items, ProbeSpec spec, Action<string>? log)
    {
        if (spec.ReportOnly)
        {
            if (!Directory.Exists(spec.Path)) return;
            var ro = new DevItem
            {
                Id = Normalize(spec.Path) + "|report",
                Name = spec.Name ?? spec.Type,
                Type = spec.Type,
                Category = spec.Category,
                Path = spec.Path,
                Description = spec.Description,
                Warning = spec.Warning,
                Recommended = false,
                RequiresElevation = false,
                Signature = DevSignature.ReportOnlyDir,
                IsReportOnly = true,
            };
            if (items.TryAdd(ro.Id, ro))
                log?.Invoke($"  发现（仅统计）：{spec.Type}（{spec.Path}）");
            return;
        }

        if (!Directory.Exists(spec.Path)) return;

        // 安全闸门：部分探测路径来自环境变量（GOCACHE/GOPATH 等），在别人的电脑上可能指向
        // 盘根或系统目录。不安全就不列出（更不会删除），并留下可追溯的日志。
        if (SafeTargets.Reject(spec.Path) is { } unsafeWhy)
        {
            log?.Invoke($"  [跳过] {spec.Type}（{spec.Path}）：目标路径不安全——{unsafeWhy}");
            return;
        }

        var (count, top) = spec.Counter?.Invoke(spec.Path) ?? (0, new List<string>());
        var item = MakeItem(spec.Name ?? spec.Type, spec.Type, spec.Category, spec.Path,
            spec.Description, spec.Warning, spec.Recommended, spec.Elevation,
            DevSignature.KnownPath, spec.Path, count, top, 0);

        if (items.TryAdd(item.Id, item))
        {
            _trustedPaths.Add(Normalize(spec.Path));
            log?.Invoke($"  发现：{spec.Type}（{spec.Path}）");
        }
    }

    /// <summary>探测各位置下的 Conda 独立环境（只枚举 envs 容器，绝不包含 base 安装本体）。</summary>
    private void AddCondaEnvs(Dictionary<string, DevItem> items, Action<string>? log, CancellationToken ct)
    {
        string drive = DriveRoot.TrimEnd('\\');
        var containers = new[]
        {
            Path.Combine(UserProfile, ".conda", "envs"),
            Path.Combine(UserProfile, "anaconda3", "envs"),
            Path.Combine(UserProfile, "miniconda3", "envs"),
            Path.Combine(UserProfile, "miniforge3", "envs"),
            Path.Combine(UserProfile, "mambaforge", "envs"),
            Path.Combine(drive, "ProgramData", "anaconda3", "envs"),
            Path.Combine(drive, "ProgramData", "miniconda3", "envs"),
            Path.Combine(drive, "ProgramData", "miniforge3", "envs"),
        };

        foreach (var c in containers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(c)) continue;
            bool elevated = SafeTargets.IsSameOrUnder(c, ProgramData);
            foreach (var env in SafeGetDirs(c))
            {
                ct.ThrowIfCancellationRequested();
                if (IsReparsePoint(env)) continue;

                var (count, top) = CountSitePackages(env);
                var item = MakeItem(
                    Path.GetFileName(env), "Conda 环境", CatPython, env,
                    "Conda 独立虚拟环境（可随时用 conda env create 重建）。",
                    elevated
                        ? "环境将被整体删除；需管理员权限（默认不勾选）。"
                        : "环境将被整体删除，其内安装的库一并移除；删除后需重新创建环境并安装依赖（默认不勾选）。",
                    recommended: false, elevation: elevated,
                    signature: DevSignature.CondaEnv, expectedPath: null, count, top, 0);

                if (items.TryAdd(item.Id, item))
                {
                    _trustedPaths.Add(Normalize(env));
                    log?.Invoke($"  发现：Conda 环境（{env}）");
                }
            }
        }
    }

    // ---------- 项目构建产物规则（检测与删除前复验共用同一判定，保证一致性） ----------

    private sealed record ProjectRule(
        string Name,
        DevSignature Signature,
        string Type,
        string Category,
        string Description,
        string Warning,
        Func<string, bool> Detect);

    private static readonly ProjectRule[] ProjectRules =
    {
        new("target", DevSignature.CargoTarget, "Rust 构建产物", CatRust,
            "Cargo 编译产物目录（target/debug、release）。删除后下次构建完全重新编译。",
            "构建产物，可安全删除；下次构建将全量重编译并下载依赖（默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, ".rustc_info.json")) || File.Exists(Path.Combine(dir, "CACHEDIR.TAG"))),

        new("target", DevSignature.MavenTarget, "Maven 构建产物", CatJava,
            "Maven 编译输出目录（target/classes、test-classes、jar）。删除后 mvn package 重新生成。",
            "构建产物，可安全删除；下次构建会重新编译（默认不勾选）。",
            dir => Directory.Exists(Path.Combine(dir, "maven-status"))),

        new("build", DevSignature.GradleBuild, "Gradle 构建产物", CatJava,
            "Gradle 工程的 build 输出目录。删除后下次构建重新生成。",
            "构建产物，可安全删除；下次构建变慢（默认不勾选）。",
            dir => File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "build.gradle"))
                || File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "build.gradle.kts"))
                || File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "settings.gradle"))
                || File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "settings.gradle.kts"))),

        new(".gradle", DevSignature.ProjectGradle, "Gradle 工程缓存", CatJava,
            "工程内的 .gradle 缓存（配置缓存、文件哈希）。删除后下次构建重建。",
            "缓存，可安全删除；下次构建变慢（默认不勾选）。",
            dir => Directory.Exists(Path.Combine(dir, "fileHashes"))
                || Directory.Exists(Path.Combine(dir, "configuration-cache"))
                || Directory.Exists(Path.Combine(dir, "vcs-1"))),

        new("vendor", DevSignature.ComposerVendor, "Composer 依赖目录", CatPhp,
            "PHP Composer 项目的 vendor 依赖目录。删除后需 composer install 重新安装。",
            "将删除整个 vendor 目录；删除后需重新安装依赖（默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "autoload.php"))),

        new("vendor", DevSignature.GoVendor, "Go vendor 依赖目录", CatGo,
            "Go modules 的 vendor 目录（依赖副本）。删除后用 go mod download 恢复。",
            "将删除整个 vendor 目录；需重新 go mod vendor（默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "modules.txt"))),

        new("obj", DevSignature.DotNetObj, ".NET 中间产物（obj）", CatBuild,
            ".NET/MSBuild 的 obj 中间目录（NuGet 还原信息、生成代码）。删除后重新 build 生成。",
            "中间产物，可安全删除；下次构建自动重建（默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "project.assets.json"))
                || SafeGetFiles(dir).Any(f => f.EndsWith(".csproj.nuget.g.props", StringComparison.OrdinalIgnoreCase))),

        new("bin", DevSignature.DotNetBin, ".NET 输出（bin）", CatBuild,
            ".NET 项目的 bin 输出目录（Debug/Release 编译结果）。删除后重新 build 生成。",
            "编译输出，可安全删除；下次构建自动重建（默认不勾选）。",
            dir => HasProjectSibling(dir) &&
                   (Directory.Exists(Path.Combine(dir, "Debug")) || Directory.Exists(Path.Combine(dir, "Release")))),

        new("Library", DevSignature.UnityLibrary, "Unity 工程缓存（Library）", CatIde,
            "Unity 工程导入缓存（Library）。删除后重新打开工程会重新导入全部资源。",
            "删除后首次打开工程需重新导入资源（可能很久，默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "ArtifactDB"))
                || File.Exists(Path.Combine(dir, "LastSceneManagerSetup.txt"))
                || Directory.Exists(Path.Combine(dir, "ScriptAssemblies"))),

        new("Intermediate", DevSignature.UnrealIntermediate, "Unreal 中间产物（Intermediate）", CatIde,
            "Unreal 工程的 Intermediate 中间产物。删除后重新生成工程文件/编译时重建。",
            "删除后下次编译明显变慢（默认不勾选）。",
            dir => HasUnrealSibling(dir)),

        new(".next", DevSignature.FrontendBuildCache, "Next.js 构建缓存（.next）", CatBuild,
            "Next.js 的生产构建与缓存目录。删除后 next build 重新生成。",
            "删除后需重新构建（开发服务器首次启动变慢，默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "BUILD_ID")) || File.Exists(Path.Combine(dir, "build-manifest.json"))),

        new(".nuxt", DevSignature.FrontendBuildCache, "Nuxt 构建缓存（.nuxt）", CatBuild,
            "Nuxt 的构建输出与缓存目录。删除后重新 nuxt build。",
            "删除后需重新构建（默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "nitro.json")) || Directory.Exists(Path.Combine(dir, "dist"))),

        new(".svelte-kit", DevSignature.FrontendBuildCache, "SvelteKit 构建缓存", CatBuild,
            "SvelteKit 的生成代码与构建输出。删除后重新 vite build。",
            "删除后需重新构建（默认不勾选）。",
            dir => Directory.Exists(Path.Combine(dir, "generated"))),

        new(".turbo", DevSignature.FrontendBuildCache, "Turborepo 缓存（.turbo）", CatBuild,
            "Turborepo 的任务缓存。删除后任务重新执行。",
            "删除后任务缓存失效（重新执行构建，默认不勾选）。",
            dir => Directory.Exists(Path.Combine(dir, "cache"))),

        new(".parcel-cache", DevSignature.FrontendBuildCache, "Parcel 缓存", CatBuild,
            "Parcel 打包器的缓存目录。删除后重新打包。",
            "删除后需重新打包（默认不勾选）。",
            dir => File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "package.json"))),

        new(".angular", DevSignature.FrontendBuildCache, "Angular 构建缓存", CatBuild,
            "Angular CLI 的构建缓存（.angular/cache）。删除后重新构建。",
            "删除后首次构建变慢（默认不勾选）。",
            dir => Directory.Exists(Path.Combine(dir, "cache"))),

        new(".dart_tool", DevSignature.FrontendBuildCache, "Dart/Flutter 工具缓存", CatBuild,
            "Dart/Flutter 工程的 .dart_tool（包配置与构建缓存）。删除后 pub get 重新生成。",
            "删除后需重新 pub get（默认不勾选）。",
            dir => File.Exists(Path.Combine(dir, "package_config.json"))),

        new(".expo", DevSignature.FrontendBuildCache, "Expo 缓存（.expo）", CatBuild,
            "Expo 工程的本地缓存与设置。删除后重新生成。",
            "删除后需重新生成（默认不勾选）。",
            dir => SafeGetFiles(dir).Any(f => f.EndsWith("devices.json", StringComparison.OrdinalIgnoreCase))
                || File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "app.json"))),
    };

    private static readonly Dictionary<string, ProjectRule[]> RulesByName = BuildRulesByName();

    private static Dictionary<string, ProjectRule[]> BuildRulesByName()
    {
        var map = new Dictionary<string, ProjectRule[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ProjectRules.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            map[group.Key] = group.ToArray();
        return map;
    }

    private static bool HasProjectSibling(string dir)
    {
        string? parent = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(parent)) return false;
        return SafeGetFiles(parent).Any(f =>
            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUnrealSibling(string dir)
    {
        string? parent = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(parent)) return false;
        return Directory.Exists(Path.Combine(parent, "Binaries")) ||
               Directory.Exists(Path.Combine(parent, "Saved")) ||
               SafeGetFiles(parent).Any(f => f.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 递归遍历（用户目录 / 系统盘 / 全部固定磁盘 / 用户添加目录） ----------

    private void Walk(string root, bool multiRoot, Dictionary<string, DevItem> items,
        Action<string>? log, Action<int>? dirsVisited, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= DetectionHardCap) break;

            var dir = stack.Pop();
            if (!Directory.Exists(dir) || IsReparsePoint(dir)) continue;

            bool isRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(dir);

            _visited++;
            if (_visited % 200 == 0) dirsVisited?.Invoke(_visited);

            // 整盘/多盘模式：Users 下仅进入当前用户的目录（他人目录与系统目录一律绕过）
            if (multiRoot && !isRoot && name.Equals("Users", StringComparison.OrdinalIgnoreCase))
            {
                string cur = Path.GetFileName(UserProfile);
                foreach (var u in SafeGetDirs(dir))
                    if (string.Equals(Path.GetFileName(u), cur, StringComparison.OrdinalIgnoreCase))
                        stack.Push(u);
                continue;
            }

            if (!isRoot && (IsGenericSkipName(name) || IsIdeInstallDir(name) || _exactSkip.Contains(Normalize(dir))))
                continue;

            // ---- 检测（命中则不进入该目录内部） ----
            if (!isRoot && IsPythonVenv(dir))
            {
                AddDetected(items, dir, Path.GetFileName(dir), "Python 虚拟环境", CatPython,
                    "python -m venv / virtualenv 创建的虚拟环境，包含环境内全部已安装库。",
                    "环境将被整体删除；删除后需重新创建环境并 pip install 依赖（默认不勾选）。",
                    DevSignature.PythonVenv, log);
                continue;
            }

            if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                var (count, top) = CountNodeModules(dir);
                if (count > 0)
                    AddDetected(items, dir, "node_modules", "Node 依赖目录", CatNode,
                        "Node.js 项目的依赖目录（npm/pnpm/yarn install 的产物）。",
                        "将删除整个 node_modules 目录；删除后需重新 install 依赖（默认不勾选）。",
                        DevSignature.NodeModules, log, top: top, count: count);
                continue; // node_modules 一律不继续下探
            }

            if (!isRoot && IsPythonCacheDirName(name))
            {
                if (PythonCacheHasContent(dir))
                    AddPycCachePath(dir);
                continue;
            }

            if (!isRoot && RulesByName.TryGetValue(name, out var rules))
            {
                ProjectRule? hit = null;
                foreach (var rule in rules)
                {
                    bool match;
                    try { match = rule.Detect(dir); }
                    catch (Exception) { match = false; }
                    if (match) { hit = rule; break; }
                }
                if (hit != null)
                {
                    AddDetected(items, dir, name, hit.Type, hit.Category,
                        hit.Description, hit.Warning, hit.Signature, log);
                    continue;
                }
            }

            foreach (var sub in SafeGetDirs(dir))
                stack.Push(sub);
        }
    }

    private void AddDetected(Dictionary<string, DevItem> items, string dir, string name, string type, string category,
        string description, string warning, DevSignature signature, Action<string>? log,
        int count = 0, List<string>? top = null)
    {
        var (c, t) = count > 0 || top != null
            ? (count, top ?? new List<string>())
            : CountPackagesFor(type, dir);

        var item = MakeItem(name, type, category, dir, description, warning,
            recommended: false, elevation: false, signature: signature, expectedPath: null, c, t, size: 0);

        if (items.TryAdd(item.Id, item))
        {
            _trustedPaths.Add(Normalize(dir));
            log?.Invoke($"  检测到：{type}（{dir}）");
        }
    }

    private static (int Count, List<string> Top) CountPackagesFor(string type, string dir)
    {
        return type switch
        {
            "Python 虚拟环境" => CountSitePackages(dir),
            "Node 依赖目录" => CountNodeModules(dir),
            "Composer 依赖目录" => CountComposerVendor(dir),
            _ => (0, new List<string>()),
        };
    }

    // ---------- Python 字节码/工具缓存聚合 ----------

    private static bool IsPythonCacheDirName(string name) =>
        name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".pytest_cache", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".mypy_cache", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".ruff_cache", StringComparison.OrdinalIgnoreCase);

    /// <summary>缓存目录内确实有内容才聚合（空目录没有清理价值，也避免噪音）。</summary>
    private static bool PythonCacheHasContent(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 记录一个 Python 缓存目录：向上寻找所属工程根（最多 6 层，按 pyproject.toml/setup.py/.git 等标记判断），
    /// 同一工程的缓存聚合成一个条目（删除时逐个删除这些缓存目录，绝不动工程根本身）。
    /// </summary>
    private void AddPycCachePath(string cacheDir)
    {
        string? projectRoot = null;
        string? current = Path.GetDirectoryName(cacheDir);
        for (int i = 0; i < 6 && current != null; i++)
        {
            if (HasPythonProjectMarker(current)) { projectRoot = current; break; }
            current = Path.GetDirectoryName(current);
        }
        if (projectRoot == null) return;   // 找不到工程根：不聚合（避免误删/误显示）

        if (!_pycGroups.TryGetValue(projectRoot, out var list))
        {
            if (_pycGroups.Count >= MaxPycGroups) return;
            list = new List<string>();
            _pycGroups[projectRoot] = list;
        }
        if (list.Count >= MaxPycPathsPerGroup) return;
        list.Add(cacheDir);
    }

    private static bool HasPythonProjectMarker(string dir)
    {
        foreach (var marker in new[] { "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile", "poetry.lock" })
            if (File.Exists(Path.Combine(dir, marker))) return true;
        return Directory.Exists(Path.Combine(dir, ".git"));
    }

    private void AddPycCacheGroups(Dictionary<string, DevItem> items, Action<string>? log)
    {
        foreach (var kv in _pycGroups)
        {
            string root = kv.Key;
            var paths = kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) continue;

            var item = new DevItem
            {
                Id = Normalize(root) + "|pyc",
                Name = Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root,
                Type = "Python 字节码/工具缓存",
                Category = CatBuild,
                Path = root,
                Description = $"该工程内的 Python 缓存目录（__pycache__ / .pytest_cache / .mypy_cache / .ruff_cache）共 {paths.Count} 处。" +
                              "删除只删除这些缓存目录，不会动源码与工程结构；下次运行时自动重建。",
                Warning = "缓存，可安全删除；下次运行 Python 时自动重新生成（默认不勾选）。",
                Recommended = false,
                RequiresElevation = false,
                Signature = DevSignature.PycCacheAggregate,
            };
            item.ExtraPaths.AddRange(paths);
            if (items.TryAdd(item.Id, item))
                log?.Invoke($"  检测到：Python 字节码/工具缓存（{root}，{paths.Count} 处）");
        }
    }

    // ---------- 测量与重叠 ----------

    /// <summary>并行测量各项大小（只读；文件型目标取文件长度；聚合项累加子目录）。</summary>
    private void MeasureItems(List<DevItem> items, DevScanCallbacks? callbacks, CancellationToken ct)
    {
        int total = items.Count;
        if (total == 0) return;

        int done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(4, Environment.ProcessorCount)),
            CancellationToken = ct,
        };

        Parallel.ForEach(items, options, item =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (item.IsFileTarget)
                {
                    var fi = new FileInfo(item.Path);
                    item.Size = fi.Exists ? fi.Length : 0;
                }
                else
                {
                    var entry = DirectoryHelper.MeasureDirectory(item.Path, MeasureLimits.Unlimited, null, ct);
                    item.Size = entry.Bytes;
                    item.FileCount = entry.Files;
                    item.Denied = entry.Denied > 0;
                    item.Truncated = entry.Truncated;

                    if (item.ExtraPaths.Count > 0)
                    {
                        long extra = 0;
                        foreach (var p in item.ExtraPaths)
                        {
                            var e2 = DirectoryHelper.MeasureDirectory(p, MeasureLimits.Unlimited, null, ct);
                            extra += e2.Bytes;
                            item.FileCount += e2.Files;
                        }
                        item.Size += extra;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* 单项测量失败不影响整体 */ }

            int n = Interlocked.Increment(ref done);
            if (n == total || n % 4 == 0)
                callbacks?.MeasureProgress?.Invoke(n, total);
        });
    }

    /// <summary>标记父子重叠：合计时只计上层条目，避免同一空间被重复累加。</summary>
    private static void MarkOverlaps(List<DevItem> items)
    {
        var byPath = new Dictionary<string, DevItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
            byPath[Normalize(it.Path)] = it;

        foreach (var it in items)
        {
            if (it.ExtraPaths.Count > 0) continue;      // 聚合项的 Path 是工程根，不算重叠
            string? parent = Path.GetDirectoryName(Normalize(it.Path));
            int guard = 0;
            while (parent != null && guard++ < 12)
            {
                if (byPath.TryGetValue(parent, out var upper) && !ReferenceEquals(upper, it))
                {
                    it.ParentPath = upper.Path;
                    break;
                }
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    // ---------- 删除（带三重安全复验） ----------

    /// <summary>逐项删除：仅统计项跳过 → 范围校验 → 签名复验 → 符号链接拒绝 → 执行删除（占用自动跳过）。</summary>
    public Task<CleanSummary> DeleteItemsAsync(IEnumerable<DevItem> items,
        Action<int, int>? progress, Action<string>? log, CancellationToken ct) => Task.Run(() =>
        {
            var summary = new CleanSummary();
            var arr = items.ToArray();
            summary.ItemsCount = arr.Length;

            for (int i = 0; i < arr.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i, arr.Length);
                var it = arr[i];
                log?.Invoke($"清理：{it.Type} - {it.Path} ...");

                if (it.IsReportOnly || it.IsFileTarget)
                {
                    summary.Errors.Add($"{it.Path}: 仅统计项（工具链本体/虚拟磁盘等），不提供删除。");
                    log?.Invoke("  ~ 仅统计项，已跳过。");
                    continue;
                }

                long freed = 0;
                bool anyLeft = false;
                foreach (var target in it.AllTargetPaths())
                {
                    if (!IsTrusted(target))
                    {
                        summary.Errors.Add($"{target}: 不在本次扫描允许的范围内，已拒绝删除。");
                        log?.Invoke("  ~ 不在允许范围内，已拒绝删除。");
                        continue;
                    }

                    if (!SignatureStillValid(it, target))
                    {
                        summary.Errors.Add($"{target}: 已非可安全删除的开发产物（签名校验未通过），已跳过。");
                        log?.Invoke("  ~ 签名校验未通过（目录可能已被替换/改动），已跳过。");
                        continue;
                    }

                    try
                    {
                        var r = DirectoryHelper.DeleteDirectory(target);
                        freed += r.FreedBytes;
                        summary.Errors.AddRange(r.Errors);
                    }
                    catch (Exception ex)
                    {
                        // 单项异常不应中断整批删除（别的电脑上可能有权限/占用/长路径等意外）
                        summary.Errors.Add($"{target}: 删除失败（{ex.Message}）");
                        log?.Invoke($"  ~ 删除失败：{ex.Message}");
                    }
                    if (Directory.Exists(target)) anyLeft = true;
                }

                summary.FreedBytes += freed;
                if (!anyLeft)
                    log?.Invoke($"  [完成] 释放 {SizeFormatter.Format(freed)}");
                else
                    log?.Invoke($"  [部分完成] 释放 {SizeFormatter.Format(freed)}，目录仍有残留（可能被占用）；");
            }

            progress?.Invoke(arr.Length, arr.Length);
            return summary;
        }, ct);

    // ---------- 安全校验 ----------

    /// <summary>范围校验：路径必须在本次扫描根之下，或为探测到的精确已知路径。</summary>
    private bool IsTrusted(string path)
    {
        string p = Normalize(path);
        foreach (var root in _allowedRoots)
        {
            // 注意：盘根（"C:\"）的结尾分隔符**不会被** TrimEndingDirectorySeparator 去掉，
            // 直接拼 root + '\' 会得到 "C:\\"，令任何子路径都匹配不上（整盘扫描模式下范围校验形同失效）。
            string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (p.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return _trustedPaths.Contains(p);
    }

    /// <summary>签名复验：删除前确认目标仍是当初检测到的开发产物，防止误删被替换/改名的目录。</summary>
    private static bool SignatureStillValid(DevItem item, string target)
    {
        string p = target;
        if (!Directory.Exists(p) || IsReparsePoint(p)) return false;

        switch (item.Signature)
        {
            case DevSignature.KnownPath:
                return item.ExpectedPath != null &&
                       string.Equals(Normalize(p), Normalize(item.ExpectedPath), StringComparison.OrdinalIgnoreCase);

            case DevSignature.PythonVenv:
                return IsPythonVenv(p);

            case DevSignature.CondaEnv:
                return IsCondaEnv(p);

            case DevSignature.NodeModules:
                return string.Equals(Path.GetFileName(p), "node_modules", StringComparison.OrdinalIgnoreCase) &&
                       CountNodeModules(p).Count > 0;

            case DevSignature.PycCacheAggregate:
                return IsPythonCacheDirName(Path.GetFileName(p)) && PythonCacheHasContent(p);

            case DevSignature.ReportOnlyDir:
            case DevSignature.ReportOnlyFile:
                return false;

            default:
                var rule = ProjectRules.FirstOrDefault(r => r.Signature == item.Signature);
                if (rule == null) return false;
                if (!string.Equals(Path.GetFileName(p), rule.Name, StringComparison.OrdinalIgnoreCase)) return false;
                try { return rule.Detect(p); }
                catch (Exception) { return false; }
        }
    }

    // ---------- 库统计 ----------

    /// <summary>统计 Python/conda 环境的 site-packages：优先统计 *.dist-info / *.egg-info 元数据，缺失时退化为包目录计数。</summary>
    private static (int Count, List<string> Top) CountSitePackages(string envDir)
    {
        string sp = Path.Combine(envDir, "Lib", "site-packages");
        if (!Directory.Exists(sp)) return (0, new List<string>());

        int count = 0;
        var names = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(sp))
            {
                string n = Path.GetFileName(entry);
                if (n.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (n.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)) { count++; names.Add(NormalizePkgName(n[..^10])); }
                else if (n.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)) { count++; names.Add(NormalizePkgName(n[..^9])); }
                else if (n.EndsWith(".egg", StringComparison.OrdinalIgnoreCase)) { count++; names.Add(n[..^4]); }
            }
        }
        catch (Exception) { /* 权限/占用异常：退回目录计数 */ }

        if (count == 0)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(sp))
                {
                    string n = Path.GetFileName(d);
                    if (n.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(Path.Combine(d, "__init__.py")))
                    {
                        count++;
                        names.Add(n);
                    }
                }
            }
            catch (Exception) { }
        }

        return (count, names.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList());
    }

    /// <summary>解析包名：去除 .dist-info 后缀与尾部的版本段（如 pip-24.1 → pip、setuptools-83.0.0-py3.13 → setuptools）。</summary>
    private static string NormalizePkgName(string name)
    {
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length > 1 && LooksLikeVersion(parts[^1]))
            parts = parts[..^1];
        return string.Join('-', parts);
    }

    private static bool LooksLikeVersion(string part) =>
        part.Length > 0 && (char.IsDigit(part[0]) ||
                            ((part.StartsWith("py", StringComparison.OrdinalIgnoreCase) ||
                              part.StartsWith("cp", StringComparison.OrdinalIgnoreCase)) &&
                             part.Length > 2 && char.IsDigit(part[2])));

    /// <summary>统计 node_modules 顶层已装的包（直接子目录含 package.json；@scope 展开一级）。</summary>
    private static (int Count, List<string> Top) CountNodeModules(string nm)
    {
        int count = 0;
        var names = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories(nm))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('@'))
                {
                    foreach (var sub in SafeGetDirs(d))
                        if (File.Exists(Path.Combine(sub, "package.json")))
                        {
                            count++;
                            if (names.Count < 20) names.Add($"{name}/{Path.GetFileName(sub)}");
                        }
                }
                else if (File.Exists(Path.Combine(d, "package.json")))
                {
                    count++;
                    if (names.Count < 20) names.Add(name);
                }
            }
        }
        catch (Exception) { }
        return (count, names);
    }

    /// <summary>统计 composer vendor 顶层包（子目录含 composer.json）。</summary>
    private static (int Count, List<string> Top) CountComposerVendor(string vendor)
    {
        int count = 0;
        var names = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories(vendor))
            {
                var name = Path.GetFileName(d);
                if (File.Exists(Path.Combine(d, "composer.json")))
                {
                    count++;
                    if (names.Count < 20) names.Add(name);
                }
            }
        }
        catch (Exception) { }
        return (count, names);
    }

    /// <summary>递归统计某目录树内按扩展名匹配的文件数（带上限保护）。</summary>
    private static (int Count, List<string> Top) CountFilesRecursive(string root, string pattern)
    {
        int count = 0;
        var names = new List<string>();
        if (!Directory.Exists(root)) return (0, names);

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && count < 200_000)
        {
            var d = stack.Pop();
            foreach (var f in SafeGetFiles(d))
                if (f.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    if (names.Count < 20) names.Add(Path.GetFileName(f));
                }
            foreach (var dd in SafeGetDirs(d))
                stack.Push(dd);
        }
        return (count, names);
    }

    // ---------- 通用工具 ----------

    private static DevItem MakeItem(string name, string type, string category, string path,
        string description, string? warning, bool recommended, bool elevation,
        DevSignature signature, string? expectedPath, int count, List<string> top, long size)
    {
        var item = new DevItem
        {
            Id = Normalize(path),
            Name = name,
            Type = type,
            Category = category,
            Path = path,
            Description = description,
            Warning = warning,
            Recommended = recommended,
            RequiresElevation = elevation,
            Signature = signature,
            ExpectedPath = expectedPath,
            Size = size,
            PackageCount = count,
        };
        if (top.Count > 0)
            item.TopPackages.AddRange(top.Take(20));
        return item;
    }

    /// <summary>是否为 Python 虚拟环境：标准 venv 检测 pyvenv.cfg；旧版 virtualenv 检测 activate.bat（避免将打包的 Python 运行时误判为虚拟环境）。</summary>
    private static bool IsPythonVenv(string dir) =>
        File.Exists(Path.Combine(dir, "pyvenv.cfg")) ||
        (File.Exists(Path.Combine(dir, "Scripts", "activate.bat")) &&
         File.Exists(Path.Combine(dir, "Scripts", "python.exe")) &&
         Directory.Exists(Path.Combine(dir, "Lib", "site-packages")));

    /// <summary>是否为 Conda 环境：含 conda-meta\history 且存在 python.exe。</summary>
    private static bool IsCondaEnv(string dir) =>
        File.Exists(Path.Combine(dir, "conda-meta", "history")) &&
        (File.Exists(Path.Combine(dir, "python.exe")) || File.Exists(Path.Combine(dir, "Scripts", "python.exe")));

    private static bool IsReparsePoint(string path)
    {
        try { return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    /// <summary>遍历时整棵跳过的目录名（系统/其他用户/工具链本体/已由探测覆盖的容器）。</summary>
    private static readonly HashSet<string> GenericSkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Application Data", "OneDriveTemp",
        "$Recycle.Bin", "System Volume Information", "$WinREAgent",
        ".git", ".svn", ".hg", ".vscode-server",
        ".nuget", ".cargo", ".gradle", ".m2", ".vscode", ".conda",
        "anaconda3", "miniconda3", "miniforge3", "mambaforge",
        ".bun", ".cache", ".local", ".npm", ".pnpm-store", ".espressif",
        ".rustup", ".dotnet", ".android",
        "site-packages",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "PerfLogs", "Recovery", "Boot", "WindowsApps", "WinSxS",
    };

    /// <summary>
    /// 是否整棵跳过该目录。目录名大小写由创建者决定（<c>onedrive</c>/<c>WINDOWS</c> 都真实存在），
    /// 因此一律忽略大小写比较；OneDrive 还要按前缀匹配——不同机器的同步根叫
    /// <c>OneDrive</c> / <c>OneDrive - Contoso</c> / <c>OneDrive - 个人</c>，只匹配等号会整棵遍历云同步目录。
    /// </summary>
    private static bool IsGenericSkipName(string name) =>
        GenericSkipNames.Contains(name) ||
        name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase);

    /// <summary>IDE / 命令行工具、AI 智能体工具自身安装目录：其内部的 node_modules 等属于程序固有文件，不应列为可清理项。</summary>
    private static bool IsIdeInstallDir(string name) =>
        name is ".zcode" or ".dsh" or ".cursor" or ".windsurf" or ".zed" or ".codeium"
            or ".coze" or ".codex" or ".claude" or ".gemini" or ".aider" or ".ollama" or ".kimi"
            or "JetBrains" or ".mise" or ".asdf" or ".nvm" or ".pyenv" or ".sdkman" or ".deno"
        || name.StartsWith("PyCharm", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("IntelliJ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WebStorm", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("GoLand", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("PhpStorm", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Rider", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLion", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("RubyMine", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DataGrip", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AppCode", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Android Studio", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Sublime Text", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GOPATH 根：环境变量可能是 <c>D:\go;E:\go</c> 这样的**分号列表**，也可能指向别处；
    /// 只取第一个真实存在的目录，否则退回 <c>%UserProfile%\go</c>。
    /// </summary>
    private static string GopathRoot()
    {
        string? raw = SafeEnv("GOPATH");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                if (Directory.Exists(p)) return p;
            }
        }
        return Path.Combine(UserProfile, "go");
    }

    /// <summary>
    /// GOCACHE 根。环境变量是本机字符串，在别人的电脑上完全可能指向盘根或系统目录
    /// （<c>GOCACHE=D:\</c>），因此必须过一遍安全闸门；不安全就退回默认位置。
    /// </summary>
    private static string GocacheRoot()
    {
        string? raw = SafeEnv("GOCACHE");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception) { full = raw; }
            if (SafeTargets.IsSafe(full) && Directory.Exists(full)) return full;
        }
        return Path.Combine(LocalAppData, "go-build");
    }

    private static string? SafeEnv(string name)
    {
        try
        {
            string? v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return path.TrimEnd('\\', '/'); }
    }

    /// <summary>展开只含通配符的路径模式（支持最后 1~3 段中的 *）；只返回真实存在的路径。</summary>
    private static IEnumerable<string> ExpandPathPattern(string pattern)
    {
        if (!pattern.Contains('*'))
        {
            if (File.Exists(pattern) || Directory.Exists(pattern)) yield return pattern;
            yield break;
        }

        string root = Path.GetPathRoot(pattern) ?? string.Empty;
        var segments = pattern[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string> { root.TrimEnd('\\') };

        foreach (var segment in segments)
        {
            var next = new List<string>();
            foreach (var current in results)
            {
                if (!segment.Contains('*'))
                {
                    next.Add(Path.Combine(current, segment));
                    continue;
                }

                string dir = string.IsNullOrEmpty(current) ? root : current;
                if (!Directory.Exists(dir)) continue;
                string search = segment.Equals("*.vhdx", StringComparison.OrdinalIgnoreCase) ? "*.vhdx" : "*";
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(dir, search))
                        if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) == 0)
                            next.Add(d);
                    if (segment.Contains('.'))
                        foreach (var f in Directory.EnumerateFiles(dir, search))
                            next.Add(f);
                }
                catch (Exception) { }
            }
            results = next;
        }

        foreach (var r in results)
        {
            if (File.Exists(r) || Directory.Exists(r)) yield return r;
        }
    }

    private static IEnumerable<string> SafeGetFixedDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception) { yield break; }

        foreach (var d in drives)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; }
            catch (Exception) { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static string[] SafeGetFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
