using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>已安装软件的来源类型。</summary>
public enum AppSource
{
    /// <summary>HKLM 64 位视图（SOFTWARE\...\Uninstall）。</summary>
    System64,

    /// <summary>HKLM 32 位视图（WOW6432Node\...\Uninstall）。</summary>
    System32,

    /// <summary>当前用户（HKCU\...\Uninstall）。</summary>
    User,

    /// <summary>Microsoft Store / UWP 应用（Get-AppxPackage）。</summary>
    Uwp,
}

/// <summary>一个可被勾选、卸载的已安装软件条目（来自注册表 Uninstall 项或商店包）。</summary>
public sealed class InstalledApp
{
    /// <summary>唯一标识：注册表条目的 hive+view+键路径，或 UWP 的 PackageFullName。</summary>
    public required string Id { get; init; }

    /// <summary>显示名称（DisplayName；UWP 为包名，尽力映射商店友好名）。</summary>
    public required string DisplayName { get; init; }

    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public DateTime? InstallDate { get; init; }

    /// <summary>预估占用字节数（EstimatedSize KB 换算；UWP 为 0）。</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>注册表记录的安装位置（原始值，可能为空或失效）。</summary>
    public string? InstallLocation { get; init; }

    /// <summary>注册表 DisplayIcon（用于推断安装目录的兜底来源）。</summary>
    public string? DisplayIcon { get; init; }

    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }

    /// <summary>是否为 MSI（WindowsInstaller=1）。</summary>
    public bool IsMsi { get; init; }

    /// <summary>MSI ProductCode（键名 {GUID} 或从卸载串提取）。</summary>
    public string? ProductCode { get; init; }

    public AppSource Source { get; init; }

    /// <summary>注册表条目所在 hive（仅 Win32）。</summary>
    public RegistryHive Hive { get; init; }

    /// <summary>注册表条目所在视图（仅 Win32；枚举与删除必须一致）。</summary>
    public RegistryView View { get; init; }

    /// <summary>注册表键的完整相对路径（如 SOFTWARE\...\Uninstall\{GUID}，仅 Win32）。</summary>
    public string? KeyPath { get; init; }

    /// <summary>UWP 包全名。</summary>
    public string? PackageFullName { get; init; }

    /// <summary>商店应用是否为“全机安装”（其他用户/预配），卸载需 -AllUsers。</summary>
    public bool IsOtherUserPackage { get; init; }

    // ---------- 占用与位置解析结果（由 AppFootprintResolver 填充，可变） ----------

    /// <summary>解析出的安装/数据位置与实测占用。</summary>
    public AppFootprint? Footprint { get; set; }

    /// <summary>是否已实测占用（区别于注册表估算值）。</summary>
    public bool IsMeasured => Footprint is { Measured: true };

    /// <summary>实测占用字节数（含数据目录，剔除与他人共用的目录）。</summary>
    public long MeasuredBytes => Footprint?.CountedBytes ?? 0;

    /// <summary>是否已有可信的注册表大小与安装位置（否则扫描后会自动实测）。</summary>
    public bool HasReliableSize =>
        !IsUwp && EstimatedBytes > 0 &&
        !string.IsNullOrWhiteSpace(InstallLocation) && SafeDirectoryExists(InstallLocation);

    private static bool SafeDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    // ---------- 便捷属性 ----------

    public bool IsUwp => Source == AppSource.Uwp;

    public bool RequiresElevation => Source is AppSource.System64 or AppSource.System32;

    /// <summary>
    /// 危险分级：商店(UWP)应用或无官方卸载程序（非 MSI 且 UninstallString 为空）的条目
    /// 属于「深度软件卸载」，风险高于常规卸载。
    /// </summary>
    public bool IsDeep => IsUwp || (!IsMsi && string.IsNullOrWhiteSpace(UninstallString));

    /// <summary>深度页分组名。</summary>
    public string DeepCategory => IsUwp ? "应用商店（UWP）" : "无官方卸载程序（仅强制删除）";

    /// <summary>风险说明（列表“风险说明”列与详情对话框展示，明确卸载后果）。</summary>
    public string RiskText
    {
        get
        {
            string text = IsUwp
                ? "商店应用：无官方卸载程序；预装项删除后可能影响系统功能，可从商店重装"
                : (!IsMsi && string.IsNullOrWhiteSpace(UninstallString))
                    ? "未注册卸载命令：仅能强制删除目录与注册表项，可能残留引用或影响依赖"
                    : IsMsi
                        ? "Windows Installer 卸载（通常自动创建系统还原点），一般安全"
                        : "运行官方卸载程序，由其清理文件与注册表引用；卸载后建议复查残留";
            if (IsOtherUserPackage) text += "；全机(其他用户)安装的商店包，卸载使用 -AllUsers";
            return RequiresElevation ? text + "；需管理员权限" : text;
        }
    }

    /// <summary>是否支持静默卸载（MSI 或提供 QuietUninstallString）。</summary>
    public bool CanSilentUninstall => IsMsi || !string.IsNullOrWhiteSpace(QuietUninstallString);

    public string SourceText => Source switch
    {
        AppSource.System64 => "系统·64",
        AppSource.System32 => "系统·32",
        AppSource.User => "用户",
        AppSource.Uwp => IsOtherUserPackage ? "商店(UWP)·全机" : "商店(UWP)",
        _ => "?",
    };

    public string Category => Source switch
    {
        AppSource.Uwp => "应用商店（UWP）",
        AppSource.User => "用户安装",
        _ => "系统安装（需管理员）",
    };

    /// <summary>占用文案：实测优先，其次注册表估算值，最后 “—”。</summary>
    public string SizeText
    {
        get
        {
            if (Footprint is { Measured: true })
            {
                long counted = Footprint.CountedBytes;
                bool truncated = Footprint.AllPaths().Any(p => p.Truncated);
                if (counted > 0) return (truncated ? "≥" : string.Empty) + SizeFormatter.Format(counted);
                if (Footprint.AllPaths().Any(p => p.Measured && p.Bytes == 0) && Footprint.Inaccessible) return "无法读取";
            }
            if (Footprint?.Install?.Suspect != null) return "—";   // 位置过宽：不做估算，避免误导
            return EstimatedBytes > 0 ? "约 " + SizeFormatter.Format(EstimatedBytes) : "—";
        }
    }

    /// <summary>占用数据来源说明（详情对话框展示）。</summary>
    public string SizeSourceText =>
        Footprint?.Install?.Suspect != null ? $"{Footprint.Install.Suspect}，已跳过测量与合计"
        : Footprint is { Measured: true } ? Footprint.SizeDetailText
        : EstimatedBytes > 0 ? $"注册表 EstimatedSize 估算（{SizeFormatter.Format(EstimatedBytes)}，可能未含更新与用户数据）"
        : "无可用数据（未实测）";

    public string DateText => TimeText.Date(InstallDate);

    public string VersionText => string.IsNullOrEmpty(DisplayVersion) ? "—" : DisplayVersion;

    public string PublisherText => string.IsNullOrEmpty(Publisher) ? "" : Publisher;

    /// <summary>安装位置：解析结果优先（含与他人共用标注），注册表原值兜底。</summary>
    public string LocationText
    {
        get
        {
            if (Footprint?.Install != null)
            {
                string text = Footprint.Install.Path;
                if (Footprint.Install.SharedWith != null) text += $"（共用：{Footprint.Install.SharedWith}）";
                if (Footprint.Install.Suspect != null) text += "（位置过宽，未计入占用）";
                return text;
            }
            if (!string.IsNullOrEmpty(InstallLocation)) return InstallLocation;
            return Footprint is { DataPaths.Count: > 0 } ? "（仅发现数据目录）" : "—";
        }
    }

    /// <summary>安装位置的来源说明。</summary>
    public string LocationSourceText => Footprint?.Install?.SourceText ?? "注册表 InstallLocation";

    /// <summary>数据位置（%LOCALAPPDATA% / %APPDATA% / %PROGRAMDATA% 名称匹配项）。</summary>
    public string DataLocationText => Footprint?.DataLocationText ?? string.Empty;

    /// <summary>卸载方式提示（显示在“卸载方式”列）。</summary>
    public string MethodText
    {
        get
        {
            if (IsUwp) return "Remove-AppxPackage";
            if (IsMsi) return "msiexec /x（可静默）";
            if (!string.IsNullOrWhiteSpace(QuietUninstallString)) return "静默/官方卸载程序";
            if (string.IsNullOrWhiteSpace(UninstallString)) return "无官方卸载程序";
            return "官方卸载程序";
        }
    }

    /// <summary>注册表键的显示路径（如 HKLM\SOFTWARE\...\Uninstall\{GUID}）。</summary>
    public string KeyDisplayPath
    {
        get
        {
            if (string.IsNullOrEmpty(KeyPath)) return string.Empty;
            string hive = Hive == RegistryHive.LocalMachine
                ? (View == RegistryView.Registry32 ? "HKLM(32位)" : "HKLM")
                : "HKCU";
            return $"{hive}\\{KeyPath}";
        }
    }
}
