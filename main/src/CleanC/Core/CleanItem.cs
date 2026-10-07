using System.Text;

namespace CleanC.Core;

/// <summary>单个清理项执行后的结果：实际释放字节数 + 遇到的错误/跳过项。</summary>
public sealed class CleanResult
{
    public long FreedBytes { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>一次清理的总览。</summary>
public sealed class CleanSummary
{
    public long FreedBytes { get; set; }
    public List<string> Errors { get; } = new();
    public int ItemsCount { get; set; }

    public string ErrorSummary()
    {
        if (Errors.Count == 0) return "全部清理完毕，无错误。";
        var sb = new StringBuilder();
        sb.AppendLine($"完成，但有 {Errors.Count} 个提示/跳过：");
        foreach (var e in Errors.Take(20))
            sb.AppendLine(" - " + e);
        if (Errors.Count > 20)
            sb.AppendLine($" ... 其余 {Errors.Count - 20} 条省略。");
        return sb.ToString();
    }
}

/// <summary>一个可被勾选、分析并清理的对象（如某类缓存、临时目录等）。</summary>
public sealed class CleanItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }

    /// <summary>是否需要管理员权限。</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>是否默认勾选（勾选仍需用户点“清理”并再次确认）。</summary>
    public bool Recommended { get; init; } = true;

    /// <summary>面向用户的警告说明（如：删除后会重新联网下载）。</summary>
    public string? Warning { get; init; }

    /// <summary>该项实际对应的位置（目录/文件绝对路径），用于列表“位置”列展示。</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>位置文案：首个路径 + “等 N 个目录”；第三方通用规则条目在警告列另有完整路径说明。</summary>
    public string LocationText => Paths.Count switch
    {
        0 => "—",
        1 => Paths[0],
        _ => $"{Paths[0]} 等 {Paths.Count} 个位置",
    };

    /// <summary>估算该清理项当前占用字节数。</summary>
    public required Func<CancellationToken, Task<long>> Estimate { get; init; }

    /// <summary>执行清理，返回释放的字节数与错误列表。</summary>
    public required Func<CancellationToken, Task<CleanResult>> Clean { get; init; }
}
