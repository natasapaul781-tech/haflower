namespace CleanC.Core;

/// <summary>扫描发现的“开发环境 / 依赖目录 / 包缓存”项，可勾选后在安全规则下删除。</summary>
public sealed class DevItem
{
    /// <summary>规范化路径（去尾分隔符），用于跨扫描去重。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（目录名或类型名）。</summary>
    public required string Name { get; init; }

    /// <summary>类型，如 “Python 虚拟环境”。</summary>
    public required string Type { get; init; }

    /// <summary>分组名（列表分组）。</summary>
    public required string Category { get; init; }

    /// <summary>绝对路径（聚合型条目为承载它们的项目/容器目录，其本身不会被删除）。</summary>
    public required string Path { get; init; }

    public required string Description { get; init; }

    /// <summary>风险提示（显示在警告列）。</summary>
    public string? Warning { get; init; }

    /// <summary>是否默认勾选（纯缓存=true；环境/全局包=false）。</summary>
    public bool Recommended { get; init; }

    /// <summary>删除是否需要管理员权限（ProgramData 下的 Conda 环境、WindowsApps 等）。</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>检测签名：删除前必须再次校验通过，防止目录被替换/改为其他用途后误删。</summary>
    public required DevSignature Signature { get; init; }

    /// <summary>已知路径检查点（仅 KnownPath 签名使用）。</summary>
    public string? ExpectedPath { get; init; }

    /// <summary>
    /// 仅统计项（工具链本体、虚拟磁盘、AI 工具运行时等）：只展示占用与位置，
    /// 本工具**不提供删除**（不可勾选，删除引擎也会二次拒绝）。
    /// </summary>
    public bool IsReportOnly { get; init; }

    /// <summary>目标是否为文件（如 WSL/Docker 的 .vhdx 虚拟磁盘）。</summary>
    public bool IsFileTarget { get; init; }

    /// <summary>聚合型条目的额外子路径（逐个校验后删除，绝不动承载它们的父目录）。</summary>
    public List<string> ExtraPaths { get; } = new();

    /// <summary>父项路径：存在时表示与已识别的父项重叠（合计只计父项，避免重复）。</summary>
    public string? ParentPath { get; set; }

    /// <summary>扫描时估算的大小（字节）。</summary>
    public long Size { get; set; }

    /// <summary>检测到的已安装库/包数量（无法统计时为 0）。</summary>
    public int PackageCount { get; set; }

    /// <summary>已安装库样例（最多 20 个）。</summary>
    public List<string> TopPackages { get; } = new();

    /// <summary>测量结果细节（文件数 / 无权限 / 截断）。</summary>
    public long FileCount { get; set; }
    public bool Denied { get; set; }
    public bool Truncated { get; set; }

    public bool IsOverlapping => ParentPath != null;

    /// <summary>所有删除目标（聚合项为多个子路径；普通项为自身）。</summary>
    public IEnumerable<string> AllTargetPaths()
    {
        if (ExtraPaths.Count > 0)
        {
            foreach (var p in ExtraPaths) yield return p;
            yield break;
        }
        yield return Path;
    }

    public string SizeText
    {
        get
        {
            string text = SizeFormatter.Format(Size);
            if (Truncated) text = "≥" + text;
            return text;
        }
    }

    public string LibText => PackageCount > 0 ? $"{PackageCount} 个" : "—";

    /// <summary>警告列文案：仅统计/重叠/风险合并展示。</summary>
    public string WarningText
    {
        get
        {
            var parts = new List<string>();
            if (IsReportOnly) parts.Add("仅统计（本工具不提供删除）");
            if (IsOverlapping) parts.Add("与上层条目重叠（合计不重复计算）");
            if (!string.IsNullOrEmpty(Warning)) parts.Add(Warning!);
            return string.Join("；", parts);
        }
    }

    /// <summary>列表“类型”列的补充标记。</summary>
    public string KindText => IsReportOnly
        ? "仅统计"
        : ExtraPaths.Count > 0 ? $"聚合 {ExtraPaths.Count} 处"
        : IsFileTarget ? "文件"
        : "目录";

    /// <summary>明细文案（详情对话框）。</summary>
    public string DetailText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"类型：{Type}（{KindText}）");
            sb.AppendLine($"名称：{Name}");
            sb.AppendLine($"路径：{Path}");
            sb.AppendLine($"大小：{SizeText}" + (FileCount > 0 ? $"（{FileCount:N0} 个文件）" : string.Empty));
            if (Denied) sb.AppendLine("提示：部分子目录无权限统计，实际占用可能更大（建议以管理员运行）。");
            if (Truncated) sb.AppendLine("提示：达到统计上限，显示的大小为下界。");
            if (ExtraPaths.Count > 0)
            {
                sb.AppendLine($"聚合的 {ExtraPaths.Count} 个子目录（删除只删这些目录，不动父目录）：");
                foreach (var p in ExtraPaths.Take(20))
                    sb.AppendLine("  · " + p);
                if (ExtraPaths.Count > 20) sb.AppendLine($"  ... 其余 {ExtraPaths.Count - 20} 个");
            }
            if (IsOverlapping) sb.AppendLine($"重叠：该目录位于已识别条目 {ParentPath} 之内，合计中只计上层条目。");
            if (PackageCount > 0) sb.AppendLine($"已安装库：{LibText}");
            if (TopPackages.Count > 0)
            {
                sb.AppendLine($"库清单（前 {TopPackages.Count} 个）：");
                foreach (var p in TopPackages)
                    sb.AppendLine("  · " + p);
            }
            if (!string.IsNullOrEmpty(WarningText))
            {
                sb.AppendLine();
                sb.AppendLine("注意：" + WarningText);
            }
            return sb.ToString();
        }
    }
}

/// <summary>检测签名（删除前的安全复验依据）。校验不通过则拒绝删除。</summary>
public enum DevSignature
{
    /// <summary>精确的已知路径；复验 = 路径与扫描时一致且目录仍存在。</summary>
    KnownPath,

    /// <summary>含 pyvenv.cfg（或旧版 virtualenv 布局）的 Python 虚拟环境。</summary>
    PythonVenv,

    /// <summary>Conda 独立环境（envs 下的子目录或含 conda-meta 的目录）。</summary>
    CondaEnv,

    /// <summary>含 package.json 子目录的 node_modules。</summary>
    NodeModules,

    /// <summary>含 autoload.php 的 vendor（Composer）。</summary>
    ComposerVendor,

    /// <summary>含 modules.txt 的 vendor（Go modules）。</summary>
    GoVendor,

    /// <summary>含 .rustc_info.json 或 CACHEDIR.TAG 的 target（Cargo 构建产物）。</summary>
    CargoTarget,

    /// <summary>含 maven-status 的 target（Maven 构建产物）。</summary>
    MavenTarget,

    /// <summary>Gradle 工程 build 目录（同级有 build.gradle/.kts 或 settings.gradle/.kts）。</summary>
    GradleBuild,

    /// <summary>项目内 .gradle 缓存目录（含 fileHashes/configuration-cache/vcs-1）。</summary>
    ProjectGradle,

    /// <summary>.NET obj 中间产物（含 project.assets.json 或 *.csproj.nuget.g.props）。</summary>
    DotNetObj,

    /// <summary>.NET bin 输出（同级有 *.csproj/*.sln，且含 Debug/Release）。</summary>
    DotNetBin,

    /// <summary>Unity 工程 Library（含 ArtifactDB/LastSceneManagerSetup.txt/ScriptAssemblies）。</summary>
    UnityLibrary,

    /// <summary>Unreal 工程 Intermediate（同级有 Binaries/Saved/Config）。</summary>
    UnrealIntermediate,

    /// <summary>前端框架构建缓存（.next/.nuxt/.svelte-kit/.turbo/.parcel-cache/.angular/.dart_tool/.expo）。</summary>
    FrontendBuildCache,

    /// <summary>Python 字节码/工具缓存聚合（__pycache__、.pytest_cache、.mypy_cache、.ruff_cache）。</summary>
    PycCacheAggregate,

    /// <summary>仅统计数据（目录）：不提供删除。</summary>
    ReportOnlyDir,

    /// <summary>仅统计数据（文件，如 .vhdx 虚拟磁盘）：不提供删除。</summary>
    ReportOnlyFile,
}
