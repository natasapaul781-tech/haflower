namespace CleanC.Core;

/// <summary>
/// 一个分区里"将被永久删除的内容"的盘点结果（**只读**）。
/// 用于合盘前的风险提示：把"会删掉多少东西、都是什么"说具体，而不是只喊一句"注意数据安全"。
/// </summary>
public sealed class PartitionImpact
{
    /// <summary>被盘点的根路径（如 <c>D:\</c>）。</summary>
    public required string Root { get; init; }

    public long FileCount { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>顶层目录/文件（名称 + 字节数，按大小降序，最多若干项）。</summary>
    public IReadOnlyList<(string Name, long Bytes)> TopLevel { get; init; } =
        Array.Empty<(string, long)>();

    /// <summary>无权限而未能统计的目录数。</summary>
    public int Denied { get; init; }

    /// <summary>是否因上限截断（此时字节数为下界）。</summary>
    public bool Truncated { get; init; }

    public int ElapsedMs { get; init; }

    /// <summary>分区里没有任何文件（删除它基本没有数据损失）。</summary>
    public bool IsEmpty => FileCount == 0 && TotalBytes == 0;

    public string ScaleText => FileCount == 0
        ? "没有统计到文件"
        : $"{FileCount:N0} 个文件，合计 {SizeFormatter.Format(TotalBytes)}";
}

/// <summary>
/// 合盘前的分区内容盘点 + 风险提示文案生成。
///
/// 本类**只读**：只遍历目录读取文件大小，不移动、不复制、不删除任何文件。
/// 存在的意义：合盘会永久删除源分区，工具不替用户备份，
/// 那么至少要如实告诉用户"具体会删掉什么、不备份会有什么后果"。
/// </summary>
public static class PartitionImpactScanner
{
    /// <summary>提示里最多列出几个顶层目录。</summary>
    private const int MaxTopLevel = 8;

    public static Task<PartitionImpact> ScanAsync(char driveLetter, CancellationToken ct,
        Action<string>? log = null)
        => Task.Run(() => Scan(driveLetter, ct, log), ct);

    public static PartitionImpact Scan(char driveLetter, CancellationToken ct, Action<string>? log = null)
    {
        if (DiskLayoutService.ParseLetter(driveLetter.ToString()) is not { } letter)
            return new PartitionImpact { Root = string.Empty };

        string root = $"{letter}:\\";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        log?.Invoke($"正在盘点 {root} 里将被删除的内容（只读遍历）...");

        PartitionImpact impact;
        try
        {
            // 复用占用分析引擎：MaxDepth=1 → 顶层目录各自的大小（正好是"会删掉哪些东西"）
            var snap = SpaceAnalyzer.Analyze(root, new AnalyzeOptions { MaxDepth = 1, MaxMilliseconds = 180_000 },
                p => log?.Invoke($"  已统计 {p.Files:N0} 个文件 / {SizeFormatter.Format(p.Bytes)}"), ct);

            var top = snap.TopLevel
                .Where(n => n.TotalBytes > 0)
                .OrderByDescending(n => n.TotalBytes)
                .Take(MaxTopLevel)
                .Select(n => (n.Name, n.TotalBytes))
                .ToArray();

            impact = new PartitionImpact
            {
                Root = root,
                FileCount = snap.FileCount,
                TotalBytes = snap.TotalBytes,
                TopLevel = top,
                Denied = snap.Denied,
                Truncated = snap.Truncated,
                ElapsedMs = snap.ElapsedMs,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("盘点失败：" + ex.Message);
            impact = new PartitionImpact { Root = root, ElapsedMs = (int)sw.ElapsedMilliseconds };
        }

        log?.Invoke($"盘点完成：{impact.ScaleText}，用时 {sw.ElapsedMilliseconds} ms" +
                    (impact.Denied > 0 ? $"（{impact.Denied} 个目录无权限，未统计）" : string.Empty));
        return impact;
    }

    /// <summary>
    /// 生成"删除这个分区会发生什么"的提示正文（用于危险确认弹窗与说明书）。
    /// 措辞刻意具体：数量、体积、不备份的具体后果、以及建议先做的三件事。
    /// </summary>
    public static string BuildWarningText(PartitionImpact impact, PartitionInfo source, PartitionInfo target,
        long extendableBytes)
    {
        string src = source.DriveLetter is { } c ? c + ":" : "源分区";
        string dst = target.DriveLetter is { } d ? d + ":" : "目标分区";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【即将删除分区 {src}】");
        sb.AppendLine();
        sb.AppendLine($"这个分区（{SizeFormatter.Format((long)source.SizeBytes)}）里的**全部文件都会被永久删除**：" +
                      $"共 {impact.ScaleText}。");
        if (impact.Truncated)
            sb.AppendLine("（注：分区内容过多，统计在上限处停止，实际只会更多。）");
        if (impact.Denied > 0)
            sb.AppendLine($"（有 {impact.Denied} 个目录因权限不足未能统计，实际内容可能更多。）");
        if (impact.IsEmpty)
            sb.AppendLine("（该分区当前没有统计到任何文件，删除它基本没有数据损失。）");
        sb.AppendLine();

        if (impact.TopLevel.Count > 0)
        {
            sb.AppendLine("将被删除的主要内容：");
            foreach (var (name, bytes) in impact.TopLevel)
                sb.AppendLine($"    {name,-34}{SizeFormatter.Format(bytes),12}");
            long shown = impact.TopLevel.Sum(t => t.Bytes);
            if (impact.TotalBytes > shown)
                sb.AppendLine($"    {"……以及其它内容",-34}{SizeFormatter.Format(impact.TotalBytes - shown),12}");
            sb.AppendLine();
        }

        sb.AppendLine("**本工具不会替你备份任何文件。**");
        sb.AppendLine();
        sb.AppendLine("没有备份会发生什么：");
        sb.AppendLine("  · 删除分区后，这些文件将**无法访问**——这比格式化更彻底：分区表条目被移除，");
        sb.AppendLine("    在资源管理器里连盘符都不再出现。");
        sb.AppendLine("  · Windows 没有「撤销」。删除分区是**不可逆**操作。");
        sb.AppendLine("  · 数据恢复软件只有在「删除后没有向这块磁盘写入任何新数据」的前提下才可能成功，");
        sb.AppendLine("    而且**成功率不保证**；一旦把空间并入目标分区并开始写入，恢复可能性会大幅下降甚至归零。");
        sb.AppendLine("  · 如果里面装过软件或游戏：需要重新安装、重新下载（可能数十 GB）。");
        sb.AppendLine("  · 如果里面有工程文件、虚拟机镜像、数据库、照片等：可能**永久丢失**。");
        sb.AppendLine("  · 如果该分区被 BitLocker 加密：删除后即使有恢复密钥也无法恢复已被移除的密钥材料。");
        sb.AppendLine();
        sb.AppendLine("建议先做完这三件事再回来：");
        sb.AppendLine("  1. 把还需要的东西复制到其它磁盘、移动硬盘或网盘——复制完**随手打开几个文件确认能正常打开**，");
        sb.AppendLine("     只看文件数量相同是不够的；");
        sb.AppendLine("  2. 关掉正在使用该分区文件的程序（下载工具、虚拟机、云同步、杀毒扫描等）；");
        sb.AppendLine("  3. 笔记本接上电源，确认过程中不会断电、不会强制关机。");
        sb.AppendLine();
        sb.AppendLine($"确认无误后，删除 {src} 会把这 {SizeFormatter.Format(extendableBytes)} 空间并入 {dst}，");
        sb.AppendLine($"完成后 {dst} 将变为约 {SizeFormatter.Format((long)target.SizeBytes + extendableBytes)}。");
        return sb.ToString();
    }
}
