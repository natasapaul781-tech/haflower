using System.Text;
using System.Text.Json;

namespace CleanC.Core;

/// <summary>单步执行结果。</summary>
public enum PartitionStepStatus
{
    /// <summary>成功。</summary>
    Success,

    /// <summary>失败（已停止后续步骤）。</summary>
    Failed,

    /// <summary>因前序步骤未通过而未执行。</summary>
    Skipped,

    /// <summary>用户取消（仅在步骤边界生效，绝不打断正在进行的系统调用）。</summary>
    Cancelled,
}

/// <summary>单步执行结果。</summary>
public sealed class PartitionStepResult
{
    public required int Index { get; init; }
    public required string Title { get; init; }
    public PartitionStepStatus Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string Before { get; init; } = string.Empty;
    public string After { get; init; } = string.Empty;
    public string PowerShell { get; init; } = string.Empty;
    public int ReturnValue { get; init; }
    public bool Ok => Status == PartitionStepStatus.Success;
}

/// <summary>一次分区执行的完整报告。</summary>
public sealed class PartitionExecutionReport
{
    public required PartitionOpKind Kind { get; init; }
    public required IReadOnlyList<PartitionStepResult> Steps { get; init; }
    public string Summary { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public int ElapsedMs { get; init; }

    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Ok);

    /// <summary>第一个未成功的步骤（全部成功时为 null）。</summary>
    public PartitionStepResult? FirstFailure => Steps.FirstOrDefault(s => !s.Ok);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"分区操作{(Succeeded ? "已完成" : "未完成")}：{Summary}");
        sb.AppendLine(FormattableString.Invariant(
            $"开始时间 {TimeText.Stamp(StartedAt)}　用时 {ElapsedMs / 1000.0:0.0}s"));
        foreach (var s in Steps)
        {
            sb.AppendLine($"  {s.Index}. [{StatusText(s.Status)}] {s.Title}");
            if (!string.IsNullOrWhiteSpace(s.Before) || !string.IsNullOrWhiteSpace(s.After))
                sb.AppendLine($"       前[{s.Before}] → 后[{s.After}]");
            if (!string.IsNullOrWhiteSpace(s.Detail)) sb.AppendLine($"       {s.Detail}");
        }
        if (!Succeeded && FirstFailure is { } f)
        {
            sb.AppendLine();
            sb.AppendLine("后续处理建议：");
            sb.AppendLine("  · 数据本身不会因为分区调整失败而丢失；失败通常只意味着体积没有变化。");
            sb.AppendLine("  · 若停在“收缩”之后、“新建分区”之前：磁盘上会留下一块「未分配」空间，");
            sb.AppendLine("    打开磁盘管理右键该空间新建简单卷即可，或重新运行本工具。");
            sb.AppendLine("  · 若停在「删除分区」之后、「扩容」之前：目标分区数据完好，");
            sb.AppendLine("    在磁盘管理里右键目标分区 →「扩展卷」把未分配空间并入即可。");
            sb.AppendLine("  · 详细原因请查看上方 Detail 与操作日志。");
        }
        return sb.ToString();
    }

    private static string StatusText(PartitionStepStatus s) => s switch
    {
        PartitionStepStatus.Success => "成功",
        PartitionStepStatus.Failed => "失败",
        PartitionStepStatus.Skipped => "已跳过",
        _ => "已取消",
    };
}

/// <summary>
/// 分区执行引擎（**破坏性操作**）。
///
/// 安全约定（每条都由脚本内的复验语句强制，不依赖调用方自律）：
/// ① 每个破坏性脚本开头都重新读取分区真实状态，比对 **GUID + 起始偏移 + 大小**，任一不符即 `throw` 中止；
/// ② 扩容前先**实测**目标分区紧邻右侧的连续空闲空间，少于预期就中止（不会盲扩容）；
/// ③ 格式化**只**作用于刚由本工具创建、且 GUID 完全匹配的分区，绝不接受盘符作为格式化目标；
/// ④ 收缩/删除一旦发出**绝不 kill 子进程**（中途打断分区操作是危险操作），取消只在步骤边界生效；
/// ⑤ 每一步都写操作日志（含前后状态），失败即停止后续步骤。
/// </summary>
public static class PartitionExecutor
{
    /// <summary>收缩/删除等破坏性操作的等待上限（分区调整在碎片多的盘上可能很久）。</summary>
    private const int LongOperationTimeoutMs = 4 * 60 * 60 * 1000;

    private const int QuickTimeoutMs = 5 * 60 * 1000;

    /// <summary>执行过程中的进度回调：当前步（1 起）、总步数、说明。</summary>
    public delegate void ProgressHandler(int current, int total, string message);

    // ==================== 分盘执行 ====================

    /// <summary>
    /// 执行分盘：收缩源分区 → 在未分配空间新建分区 → 格式化为 NTFS。
    /// </summary>
    public static async Task<PartitionExecutionReport> ExecuteSplitAsync(
        DiskProbeResult probe, PartitionPlan plan, ProgressHandler? progress, Action<string>? log, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<PartitionStepResult>();
        var started = DateTime.Now;

        if (plan.Kind != PartitionOpKind.Split)
            throw new ArgumentException("计划类型不是分盘。", nameof(plan));

        log?.Invoke($"=== 开始执行分盘：{plan.Title} ===");
        OperationLog.LogPartitionPlan(PartitionScriptWriter.BuildPreview(plan));

        // ---------- 步骤 0：执行前复验（不写入 results，失败直接抛给调用方） ----------
        progress?.Invoke(0, 3, "正在复验分区状态...");
        var current = DiskLayoutService.GetPartition(plan.Source.DiskNumber, plan.Source.PartitionNumber, ct, log);
        if (current is null)
            throw new InvalidOperationException(
                $"复验失败：找不到 磁盘 {plan.Source.DiskNumber} 分区 {plan.Source.PartitionNumber}。" +
                "分区可能已被其它程序改动，请点「重新检测」后重新分析。");

        if (!current.IsOperable)
            throw new InvalidOperationException($"复验失败：目标分区当前类型为「{current.KindText}」，不属于可操作的基本数据分区。");

        if (!plan.Source.MatchesExactly(current))
            throw new InvalidOperationException(
                $"复验失败：分区状态与计划不一致（计划 {plan.Source}，实际 偏移{current.OffsetBytes} 大小{current.SizeBytes}）。" +
                "分区可能已被其它程序调整，为安全起见已中止。请重新检测并重新生成计划。");

        log?.Invoke($"复验通过：{current.DisplayName}，GUID {current.Guid}");

        // ---------- 步骤 1：收缩 ----------
        var shrinkBefore = SizeFormatter.Format((long)current.SizeBytes);
        var (shrinkOk, shrinkDetail, shrinkReturn, shrinkAfter) = await Task.Run(
            () => Shrink(plan.Source, plan.SourceSizeAfterBytes, log, ct), ct).ConfigureAwait(false);

        results.Add(new PartitionStepResult
        {
            Index = 1,
            Title = $"收缩 {current.LetterText} 到 {SizeFormatter.Format(plan.SourceSizeAfterBytes)}",
            Status = shrinkOk ? PartitionStepStatus.Success : PartitionStepStatus.Failed,
            Detail = shrinkDetail,
            Before = shrinkBefore,
            After = shrinkAfter,
            ReturnValue = shrinkReturn,
            PowerShell = StepPowerShell(plan, 1),
        });
        OperationLog.LogPartitionStep("收缩分区", shrinkBefore, shrinkAfter, shrinkOk, shrinkDetail);

        if (!shrinkOk)
            return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 1);

        // ---------- 步骤 2：新建分区 ----------
        progress?.Invoke(2, 3, "正在新建分区...");
        var letter = plan.NewDriveLetter ?? throw new InvalidOperationException("计划缺少新分区盘符。");

        // 关键：**实测**刚释放出来的那块空间，而不是照计划里的"算术差值"去建。
        // 计划的数字是几何预估；Windows 建分区要求起点与终点都落在 1 MB 边界上，
        // 照算术值去建会报 Not enough available capacity（真事故：收缩成功、新建失败）。
        var (measuredOffset, measuredSize, measureDetail) = MeasureFreedExtent(probe, plan, log, ct);

        // 实测比计划少一点点是正常的（1 MB 对齐 + 末尾保留区）；少太多说明空间被别的程序动过，
        // 此时**不要**闷头建一个小得多的分区，而是停下来讲清楚。
        const long toleranceBytes = 64L * 1024 * 1024;
        if (measuredSize > 0 && measuredSize < plan.FreedBytes - toleranceBytes)
            throw new InvalidOperationException(
                $"实测可建空间只有 {SizeFormatter.Format(measuredSize)}，比计划的 {SizeFormatter.Format(plan.FreedBytes)} " +
                $"少了 {SizeFormatter.Format(plan.FreedBytes - measuredSize)}（{measureDetail}）。" +
                "收缩之后磁盘布局可能被其它程序改动过，为避免建出一个比预期小得多的分区，已中止。" +
                "请在「磁盘分区」窗口点「重新检测」，或直接用「用未分配空间新建分区…」把现有空间建成分区。");

        long newSize = measuredSize > 0 ? measuredSize : plan.FreedBytes;
        long? newOffset = measuredSize > 0 ? measuredOffset : null;

        log?.Invoke(measuredSize > 0
            ? $"实测可建尺寸：{SizeFormatter.Format(newSize)}（计划 {SizeFormatter.Format(plan.FreedBytes)}）" +
              (newSize != plan.FreedBytes ? "——差异来自 1 MB 对齐，属正常" : string.Empty)
            : $"无法实测空闲空间（{measureDetail}），按计划尺寸 {SizeFormatter.Format(newSize)} 尝试。");

        var (createOk, createDetail, newPart) = await Task.Run(
            () => CreatePartitionAdaptive(plan.DiskNumber, newOffset, newSize, letter, log, ct), ct).ConfigureAwait(false);

        results.Add(new PartitionStepResult
        {
            Index = 2,
            Title = $"新建 {letter}:（{SizeFormatter.Format(newSize)}）",
            Status = createOk ? PartitionStepStatus.Success : PartitionStepStatus.Failed,
            Detail = createDetail,
            Before = "（不存在）",
            After = newPart is null ? "（未创建）" : $"磁盘{newPart.DiskNumber} 分区{newPart.PartitionNumber}，{SizeFormatter.Format((long)newPart.SizeBytes)}",
            PowerShell = StepPowerShell(plan, 2),
        });
        OperationLog.LogPartitionStep("新建分区", "（不存在）",
            newPart is null ? "（未创建）" : newPart.DisplayName, createOk, createDetail);

        if (!createOk || newPart is null)
            return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 2);

        // 新建后立刻复验：拿到的分区必须与 New-Partition 的返回值完全一致（GUID/偏移/大小）。
        // 与「新建分区」页共用同一段逻辑，避免两条路径各写一份后慢慢走样。
        var verifyNew = DiskLayoutService.GetPartition(newPart.DiskNumber, newPart.PartitionNumber, ct, log);
        var fmtStep = await FormatCreatedAsync(plan, newPart, verifyNew, 3, log, ct).ConfigureAwait(false);
        results.Add(fmtStep);
        if (fmtStep.Status == PartitionStepStatus.Success)
            OperationLog.LogPartitionStep("格式化新分区", verifyNew?.DisplayName ?? newPart.DisplayName,
                "NTFS", true, fmtStep.Detail);

        return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 3);
    }

    // ==================== 新建分区执行（用已有的未分配空间） ====================

    /// <summary>
    /// 在磁盘上**已有的未分配空间**里创建分区并格式化（两步：新建 → 格式化）。
    ///
    /// 与分盘的区别：不收缩任何分区，因此没有"不可逆的收缩"在前，整个流程都不会影响现有数据。
    /// 它也是「收缩成功但新建失败」这类半完成状态的修复路径。
    ///
    /// 执行前会**重新枚举该磁盘的分区**，确认计划里那段空间此刻仍然空着——
    /// 计划可能是几分钟前生成的，期间用户可能在磁盘管理里动过手。
    /// </summary>
    public static async Task<PartitionExecutionReport> ExecuteCreateAsync(
        DiskProbeResult probe, PartitionPlan plan, ProgressHandler? progress, Action<string>? log, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<PartitionStepResult>();
        var started = DateTime.Now;

        if (plan.Kind != PartitionOpKind.Create)
            throw new ArgumentException("计划类型不是新建分区。", nameof(plan));
        if (plan.NewDriveLetter is not { } letter)
            throw new InvalidOperationException("计划缺少新分区盘符。");

        log?.Invoke($"=== 开始执行新建分区：{plan.Title} ===");
        OperationLog.LogPartitionPlan(PartitionScriptWriter.BuildPreview(plan));

        // ---------- 复验：这段未分配空间现在仍然空着吗？ ----------
        progress?.Invoke(0, 2, "正在复验未分配空间...");
        long start = plan.NewPartitionStartBytes;
        long end = start + plan.FreedBytes;
        var nowParts = DiskLayoutService.GetPartitionsOfDisk(plan.DiskNumber, ct, log);
        if (nowParts.Count == 0)
            throw new InvalidOperationException("复验失败：读取不到该磁盘的分区列表（可能需要管理员权限），已中止。");

        foreach (var p in nowParts)
        {
            bool overlaps = (long)p.OffsetBytes < end && (long)p.EndBytes > start;
            if (overlaps)
                throw new InvalidOperationException(
                    $"复验失败：计划要用的位置（{SizeFormatter.Format(start)} ~ {SizeFormatter.Format(end)}）" +
                    $"现在与「{p.DisplayName}」重叠了——磁盘布局已变化，请点「重新检测」后重新生成计划。");
        }
        log?.Invoke($"复验通过：该位置的未分配空间仍然存在（{SizeFormatter.Format(plan.FreedBytes)}）。");

        // ---------- 步骤 1：新建分区（显式指定起始偏移，精确落在刚核验过的位置上） ----------
        var (createOk, createDetail, newPart) = await Task.Run(
            () => CreatePartitionAdaptive(plan.DiskNumber, start, plan.FreedBytes, letter, log, ct), ct).ConfigureAwait(false);

        results.Add(new PartitionStepResult
        {
            Index = 1,
            Title = $"新建 {letter}:（{SizeFormatter.Format(plan.FreedBytes)}）",
            Status = createOk ? PartitionStepStatus.Success : PartitionStepStatus.Failed,
            Detail = createDetail,
            Before = "（不存在）",
            After = newPart is null ? "（未创建）" : $"磁盘{newPart.DiskNumber} 分区{newPart.PartitionNumber}，{SizeFormatter.Format((long)newPart.SizeBytes)}",
            PowerShell = StepPowerShell(plan, 1),
        });
        OperationLog.LogPartitionStep("新建分区", "（不存在）",
            newPart is null ? "（未创建）" : newPart.DisplayName, createOk, createDetail);

        if (!createOk || newPart is null)
            return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 1);

        // ---------- 步骤 2：复验身份 + 格式化 ----------
        var verify = DiskLayoutService.GetPartition(newPart.DiskNumber, newPart.PartitionNumber, ct, log);
        var fmtStep = await FormatCreatedAsync(plan, newPart, verify, 2, log, ct).ConfigureAwait(false);
        results.Add(fmtStep);
        if (fmtStep.Status == PartitionStepStatus.Success)
            OperationLog.LogPartitionStep("格式化新分区", verify?.DisplayName ?? newPart.DisplayName,
                $"NTFS（卷标「{plan.NewVolumeLabel}」）", true, fmtStep.Detail);

        return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 2);
    }

    /// <summary>
    /// 复验刚创建的分区身份，然后格式化它（分盘与新建分区**共用**这一步）。
    /// 任一身份字段不符就跳过格式化并说明原因——绝不会去格式化一个"可能是别的分区"的对象。
    /// </summary>
    private static async Task<PartitionStepResult> FormatCreatedAsync(
        PartitionPlan plan, PartitionInfo created, PartitionInfo? verify, int index, Action<string>? log, CancellationToken ct)
    {
        string letterText = plan.NewDriveLetter is { } l ? $"{l}:" : "新分区";
        string title = $"把 {letterText} 格式化为 NTFS（卷标「{plan.NewVolumeLabel}」）";

        if (verify is null)
            return Skipped(index, title,
                "无法复验刚创建的分区，为安全起见**拒绝格式化**（避免误格式化其它分区）。" +
                "请在磁盘管理中确认新分区后手动格式化。");

        if (!string.IsNullOrWhiteSpace(created.Guid) && !string.IsNullOrWhiteSpace(verify.Guid) &&
            !created.Guid.Equals(verify.Guid, StringComparison.OrdinalIgnoreCase))
            return Skipped(index, title,
                $"刚创建的分区 GUID（{created.Guid}）与复验结果（{verify.Guid}）不一致，" +
                "为安全起见**拒绝格式化**。请在磁盘管理中手动确认。");

        string guid = string.IsNullOrWhiteSpace(verify.Guid) ? created.Guid : verify.Guid;

        var (fmtOk, fmtDetail, fmtReturn) = await Task.Run(
            () => FormatNewPartition(verify, guid, plan.NewVolumeLabel, log, ct), ct).ConfigureAwait(false);

        return new PartitionStepResult
        {
            Index = index,
            Title = title,
            Status = fmtOk ? PartitionStepStatus.Success : PartitionStepStatus.Failed,
            Detail = fmtDetail,
            Before = verify.DisplayName,
            After = fmtOk ? $"{letterText} NTFS 可用" : "（未格式化）",
            ReturnValue = fmtReturn,
            PowerShell = StepPowerShell(plan, index),
        };
    }

    private static PartitionStepResult Skipped(int index, string title, string detail) => new()
    {
        Index = index,
        Title = title,
        Status = PartitionStepStatus.Skipped,
        Detail = detail,
    };

    // ==================== 合盘执行（删除 + 扩容） ====================

    /// <summary>
    /// 执行合盘的破坏性阶段：删除源分区 → 把释放出的紧邻空闲空间并入目标分区。
    /// **调用方必须先完成数据搬迁与逐文件校验**（本方法只负责分区层面的动作与复验）。
    /// </summary>
    public static async Task<PartitionExecutionReport> ExecuteMergeAsync(
        DiskProbeResult probe, PartitionPlan plan, ProgressHandler? progress, Action<string>? log, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<PartitionStepResult>();
        var started = DateTime.Now;

        if (plan.Kind != PartitionOpKind.Merge || plan.Target is not { } targetId)
            throw new ArgumentException("计划类型不是合盘或缺少目标分区。", nameof(plan));

        log?.Invoke($"=== 开始执行合盘（删除 + 扩容）：{plan.Title} ===");
        OperationLog.LogPartitionPlan(PartitionScriptWriter.BuildPreview(plan));

        // ---------- 删除前最终复验（对源与目标**同时**复验） ----------
        progress?.Invoke(0, 2, "正在复验源分区与目标分区...");

        var src = DiskLayoutService.GetPartition(plan.Source.DiskNumber, plan.Source.PartitionNumber, ct, log);
        if (src is null)
            throw new InvalidOperationException("复验失败：找不到要删除的源分区，请重新检测。");
        if (!src.IsOperable)
            throw new InvalidOperationException($"复验失败：源分区当前类型为「{src.KindText}」，不属于可操作的基本数据分区。");
        if (!plan.Source.MatchesExactly(src))
            throw new InvalidOperationException(
                $"复验失败：源分区状态与计划不一致（计划 {plan.Source}，实际 偏移{src.OffsetBytes} 大小{src.SizeBytes}），已中止。");

        var tgt = DiskLayoutService.GetPartition(targetId.DiskNumber, targetId.PartitionNumber, ct, log);
        if (tgt is null)
            throw new InvalidOperationException("复验失败：找不到目标分区，请重新检测。");
        if (!tgt.IsOperable)
            throw new InvalidOperationException($"复验失败：目标分区当前类型为「{tgt.KindText}」，不属于可操作的基本数据分区。");
        if (!targetId.Matches(tgt))
            throw new InvalidOperationException("复验失败：目标分区身份与计划不一致，已中止。");

        // 源分区不能被系统占用为页面文件/休眠文件所在位置
        if (src.DriveLetter is { } srcLetter)
        {
            long paging = DiskLayoutService.MeasurePagingFiles(srcLetter);
            if (paging > 0)
                throw new InvalidOperationException(
                    $"复验失败：{srcLetter}: 根目录下仍有页面文件/休眠文件（合计 {SizeFormatter.Format(paging)}），" +
                    "删除该分区会破坏系统。请先把页面文件移到别的盘、并关闭休眠（powercfg /h off），再重试。");
        }

        log?.Invoke($"复验通过。源 {src.DisplayName}（GUID {src.Guid}）→ 目标 {tgt.DisplayName}（GUID {tgt.Guid}）");

        // ---------- 步骤 1：删除源分区 ----------
        var delBefore = SizeFormatter.Format((long)src.SizeBytes);
        var (delOk, delDetail, delReturn) = await Task.Run(() => RemovePartition(plan.Source, log, ct), ct).ConfigureAwait(false);

        results.Add(new PartitionStepResult
        {
            Index = 1,
            Title = $"删除源分区 {src.LetterText}（{delBefore}）",
            Status = delOk ? PartitionStepStatus.Success : PartitionStepStatus.Failed,
            Detail = delDetail,
            Before = src.DisplayName,
            After = delOk ? "（已删除，空间变为未分配）" : "（未删除）",
            ReturnValue = delReturn,
            PowerShell = StepPowerShell(plan, 6, 0),
        });
        OperationLog.LogPartitionStep("删除源分区", delBefore, delOk ? "已删除" : "未删除", delOk, delDetail);

        if (!delOk)
            return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 2);

        // ---------- 步骤 2：扩容目标分区（先实测紧邻空闲空间，再并入） ----------
        progress?.Invoke(2, 2, "正在实测并扩容目标分区...");
        long expectedFree = plan.FreedBytes;
        var (extOk, extDetail, extReturn, extBefore, extAfter) = await Task.Run(
            () => ExtendAdjacent(targetId, expectedFree, log, ct), ct).ConfigureAwait(false);

        results.Add(new PartitionStepResult
        {
            Index = 2,
            Title = $"把释放出的空间并入 {tgt.LetterText}",
            Status = extOk ? PartitionStepStatus.Success : PartitionStepStatus.Failed,
            Detail = extDetail,
            Before = extBefore,
            After = extAfter,
            ReturnValue = extReturn,
            PowerShell = StepPowerShell(plan, 6, 1),
        });
        OperationLog.LogPartitionStep("扩容目标分区", extBefore, extAfter, extOk, extDetail);

        return Finish(plan, results, started, (int)sw.ElapsedMilliseconds, ct, log, progress, 2);
    }

    // ==================== 底层脚本（脚本模板见 PartitionScripts，守卫可被单元断言） ====================

    /// <summary>
    /// 实测"刚释放出来的那段空间"现在能建出多大的分区。
    ///
    /// 枚举该磁盘当前的分区：源分区（收缩后）的末尾就是这段空间的起点，
    /// 终点取其后第一个分区的起点，没有则取磁盘末尾减去末尾保留区（GPT 备份分区表）。
    /// 起点向上取整、尺寸向下取整到 1 MB —— 这两个取整是"能建出来"的前提。
    /// </summary>
    private static (long Offset, long Size, string Detail) MeasureFreedExtent(
        DiskProbeResult probe, PartitionPlan plan, Action<string>? log, CancellationToken ct)
    {
        var disk = probe.Layout.DiskByNumber(plan.DiskNumber);
        var parts = DiskLayoutService.GetPartitionsOfDisk(plan.DiskNumber, ct, log);
        if (disk is null) return (0, 0, "探测结果里找不到该磁盘");
        if (parts.Count == 0) return (0, 0, "枚举不到该磁盘的分区（可能需要管理员权限）");

        var src = parts.FirstOrDefault(p => p.PartitionNumber == plan.Source.PartitionNumber);
        if (src is null) return (0, 0, $"找不到源分区（磁盘 {plan.DiskNumber} 分区 {plan.Source.PartitionNumber}）");

        long start = (long)src.EndBytes;
        var next = parts
            .Where(p => p.PartitionNumber != src.PartitionNumber && p.OffsetBytes >= src.EndBytes)
            .OrderBy(p => p.OffsetBytes)
            .FirstOrDefault();

        long end = next is not null
            ? (long)next.OffsetBytes
            : (long)disk.SizeBytes - FreeExtent.ReservedTailBytes;

        long creatable = PartitionPlanBuilder.CreatableSize(start, end);
        long offset = (long)DiskLayoutSnapshot.AlignUp((ulong)start);

        return creatable > 0
            ? (offset, Math.Min(creatable, plan.FreedBytes), $"起点 {start} → 对齐 {offset}，可建 {creatable}")
            : (0, 0, $"源分区之后没有可用的未分配空间（起点 {start}，终点 {end}）");
    }

    /// <summary>
    /// 新建分区：先按**显式偏移**建（精确落在已核验的位置上）；若系统因为对齐/容量拒绝，
    /// 再让 Windows 自行选择位置重试一次（尺寸不变，本身已是 1 MB 整数倍）。
    /// 两次都失败才报告失败——这样不同 Windows 版本/不同磁盘的对齐策略也能一次点通。
    /// </summary>
    private static (bool Ok, string Detail, PartitionInfo? NewPartition) CreatePartitionAdaptive(
        int diskNumber, long? offsetBytes, long sizeBytes, char letter, Action<string>? log, CancellationToken ct)
    {
        if (offsetBytes is not { } offset)
            return CreatePartition(diskNumber, null, sizeBytes, letter, log, ct);

        var first = CreatePartition(diskNumber, offset, sizeBytes, letter, log, ct);
        if (first.Ok) return first;
        if (!IsAlignmentOrCapacityFailure(first.Detail)) return first;

        log?.Invoke("按指定偏移新建未成功（对齐/容量限制），改为让 Windows 自行选择位置重试一次……");
        var second = CreatePartition(diskNumber, null, sizeBytes, letter, log, ct);
        if (second.Ok)
            return (true, second.Detail + "（位置由 Windows 自行对齐选择）", second.NewPartition);

        return (false,
            $"按指定偏移失败：{first.Detail}；改为自动选位重试也失败：{second.Detail}", null);
    }

    /// <summary>失败原因是否属于"对齐/容量"这一类（这类重试一次有意义，其它错误重试没意义）。</summary>
    private static bool IsAlignmentOrCapacityFailure(string detail)
    {
        if (string.IsNullOrEmpty(detail)) return false;
        string[] keys =
        {
            "Not enough available capacity",
            "not aligned",
            "is not aligned",
            "Invalid Parameter",
            "The parameter is incorrect",
            "not supported",
            "容量不足",
            "未对齐",
            "不支持",
        };
        return keys.Any(k => detail.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>收缩分区（MSFT_Partition.Resize）。脚本内先复验 GUID/偏移/大小。</summary>
    private static (bool Ok, string Detail, int ReturnValue, string After) Shrink(
        PartitionIdentity id, long targetBytes, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"执行收缩：{id} → {SizeFormatter.Format(targetBytes)}");
        var (ok, root, detail) = RunDestructive(
            PartitionScripts.Shrink(id, targetBytes), LongOperationTimeoutMs, ct, log);

        int rv = RootInt(root, "ReturnValue");
        ulong after = RootUInt64(root, "After");

        if (!ok || rv != 0)
            return (false, BuildFailure("收缩", rv, RootString(root, "Extended"), detail), rv, "（未变化）");

        return (true,
            $"已收缩到 {SizeFormatter.Format((long)after)}（Windows 返回码 0）",
            rv, SizeFormatter.Format((long)after));
    }

    /// <summary>在未分配空间新建分区并分配盘符。</summary>
    private static (bool Ok, string Detail, PartitionInfo? NewPartition) CreatePartition(
        int diskNumber, long sizeBytes, char letter, Action<string>? log, CancellationToken ct)
        => CreatePartition(diskNumber, null, sizeBytes, letter, log, ct);

    /// <summary>在未分配空间新建分区并分配盘符（可指定已对齐的起始偏移）。</summary>
    private static (bool Ok, string Detail, PartitionInfo? NewPartition) CreatePartition(
        int diskNumber, long? offsetBytes, long sizeBytes, char letter, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"执行新建分区：磁盘 {diskNumber}，" +
                    (offsetBytes is { } off ? $"起始 {off}，" : string.Empty) +
                    $"{SizeFormatter.Format(sizeBytes)}，盘符 {letter}:");
        var (ok, root, detail) = RunDestructive(
            PartitionScripts.CreatePartition(diskNumber, offsetBytes, sizeBytes, letter), QuickTimeoutMs, ct, log);

        if (!ok || !RootBool(root, "Ok"))
            return (false, BuildFailure("新建分区", RootInt(root, "ReturnValue"),
                RootString(root, "Extended"), detail), null);

        var arr = root is { } rp ? rp.ArrayOf("Partition") : Array.Empty<JsonElement>();
        if (arr.Count == 0)
            return (false, "新建分区成功但未返回分区信息，已中止后续格式化。", null);

        var part = DiskLayoutService.ParsePartition(arr[0]);
        return (true,
            $"已创建 磁盘{part.DiskNumber} 分区{part.PartitionNumber}，GUID {part.Guid}，" +
            $"偏移 {part.OffsetBytes}，大小 {SizeFormatter.Format((long)part.SizeBytes)}",
            part);
    }

    /// <summary>
    /// 格式化**刚创建**的分区。身份三重校验（GUID + 偏移 + 大小）+ 必须带盘符，任一不符即拒绝。
    /// </summary>
    private static (bool Ok, string Detail, int ReturnValue) FormatNewPartition(
        PartitionInfo created, string guid, string label, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"执行格式化（仅作用于 GUID {guid}）：{created.DisplayName}，卷标「{label}」");
        var (ok, root, detail) = RunDestructive(
            PartitionScripts.FormatNewPartition(created, guid, label), QuickTimeoutMs, ct, log);

        if (!ok || !RootBool(root, "Ok"))
            return (false, BuildFailure("格式化", RootInt(root, "ReturnValue"),
                RootString(root, "Extended"), detail), -1);

        return (true, $"已格式化为 NTFS，卷标「{PartitionPlanBuilder.SanitizeLabel(label)}」", 0);
    }

    /// <summary>删除分区（脚本内先复验 GUID/偏移/大小）。</summary>
    private static (bool Ok, string Detail, int ReturnValue) RemovePartition(
        PartitionIdentity id, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"执行删除分区：{id}");
        var (ok, root, detail) = RunDestructive(
            PartitionScripts.RemovePartition(id), LongOperationTimeoutMs, ct, log);

        if (!ok || !RootBool(root, "Ok"))
            return (false, BuildFailure("删除分区", RootInt(root, "ReturnValue"),
                RootString(root, "Extended"), detail), -1);

        return (true, "源分区已删除，空间变为未分配。", 0);
    }

    /// <summary>
    /// 把目标分区**紧邻右侧**的连续空闲空间全部并入：先实测 GetSupportedSize 的 SizeMax，
    /// 确认空闲量不少于预期（源分区容量）后才执行 Resize——避免“删了分区却没能扩容”。
    /// </summary>
    private static (bool Ok, string Detail, int ReturnValue, string Before, string After) ExtendAdjacent(
        PartitionIdentity targetId, long expectedFreeBytes, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"执行扩容：先实测紧邻空闲空间，确认不少于 {SizeFormatter.Format(expectedFreeBytes)} 后再并入");
        var (ok, root, detail) = RunDestructive(
            PartitionScripts.ExtendAdjacent(targetId, expectedFreeBytes), LongOperationTimeoutMs, ct, log);

        ulong before = RootUInt64(root, "Before");
        ulong after = RootUInt64(root, "After");
        int rv = RootInt(root, "ReturnValue");
        string beforeText = before > 0 ? SizeFormatter.Format((long)before) : "（未知）";
        string afterText = after > 0 ? SizeFormatter.Format((long)after) : "（未变化）";

        if (!ok || !RootBool(root, "Ok"))
            return (false, BuildFailure("扩容", rv, RootString(root, "Extended"), detail), rv, beforeText, afterText);

        ulong gained = after > before ? after - before : 0;
        return (true,
            $"已并入 {SizeFormatter.Format((long)gained)}（{beforeText} → {afterText}）",
            rv, beforeText, afterText);
    }

    // ==================== 子进程与结果处理 ====================

    /// <summary>
    /// 执行破坏性脚本：**绝不 kill 子进程**（中途终止分区操作是危险操作），
    /// 取消或超时只放弃等待并把控制权交回调用方。
    /// </summary>
    private static (bool Ok, JsonElement? Root, string Detail) RunDestructive(
        string script, int timeoutMs, CancellationToken ct, Action<string>? log)
    {
        var (code, output, err) = PowerShellRunner.Run(script, timeoutMs, killOnTimeout: false, ct);

        var roots = PowerShellRunner.ParseJsonAsList(output);
        var root = roots.Count > 0 ? roots[0] : (JsonElement?)null;

        // 脚本自身用 try/catch 把结果写进 JSON；退出码非 0 且没有 JSON 才是真正的调用失败
        if (root is null)
        {
            string detail = string.IsNullOrWhiteSpace(err)
                ? $"PowerShell 退出码 {code}，且没有返回可解析结果"
                : $"PowerShell 退出码 {code}：{Truncate(err.Trim(), 300)}";
            log?.Invoke("  执行失败：" + detail);
            return (false, null, detail);
        }

        bool ok = RootBool(root, "Ok");
        string msg = RootString(root, "Extended");
        log?.Invoke(ok ? "  执行成功。" : "  执行未成功：" + msg);
        return (ok, root, msg);
    }

    private static string BuildFailure(string action, int returnValue, string? extended, string fallback)
    {
        var sb = new StringBuilder();
        sb.Append($"{action}未成功");
        if (returnValue >= 0 || returnValue == -1)
            sb.Append($"（返回码 {returnValue}：{DiskLayoutService.ExplainReturnCode(returnValue).TrimEnd('。')}）");
        string extra = string.IsNullOrWhiteSpace(extended) ? fallback : extended;
        if (!string.IsNullOrWhiteSpace(extra)) sb.Append("；系统提示：" + extra.Trim());
        return sb.ToString();
    }

    private static PartitionExecutionReport Finish(PartitionPlan plan, List<PartitionStepResult> results,
        DateTime startedAt, int elapsedMs, CancellationToken ct, Action<string>? log,
        ProgressHandler? progress, int failedAt)
    {
        int total = plan.Steps.Count;
        for (int i = failedAt + 1; i <= total; i++)
        {
            var planned = plan.Steps.FirstOrDefault(s => s.Index == i);
            results.Add(new PartitionStepResult
            {
                Index = i,
                Title = planned?.Title ?? $"第 {i} 步",
                Status = ct.IsCancellationRequested ? PartitionStepStatus.Cancelled : PartitionStepStatus.Skipped,
                Detail = ct.IsCancellationRequested ? "用户已取消（未执行）。" : "因前序步骤未成功，已跳过。",
                PowerShell = planned?.PowerShell ?? string.Empty,
            });
        }

        bool ok = results.All(r => r.Ok);
        string summary = ok
            ? $"{plan.Title} —— 全部步骤成功"
            : $"{plan.Title} —— 在“{results.FirstOrDefault(r => !r.Ok)?.Title}”处停止";

        var report = new PartitionExecutionReport
        {
            Kind = plan.Kind,
            Steps = results,
            Summary = summary,
            StartedAt = startedAt,
            ElapsedMs = elapsedMs,
        };

        OperationLog.LogPartitionResult(ok, summary);
        log?.Invoke(report.ToText());
        progress?.Invoke(results.Count, total, ok ? "完成" : "已停止");
        return report;
    }

    /// <summary>取计划里对应步骤的等价命令（用于结果报告，方便用户核对/手工复现）。</summary>
    private static string StepPowerShell(PartitionPlan plan, int stepIndex, int offset = 0)
    {
        var step = plan.Steps.FirstOrDefault(s => s.Index == stepIndex + offset)
                   ?? plan.Steps.FirstOrDefault(s => s.Index == stepIndex);
        return step?.PowerShell ?? string.Empty;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "...");

    // ---------- 可空 JSON 取值的便捷封装（扩展方法无法作用于 JsonElement?） ----------

    private static bool RootBool(JsonElement? root, string name) => root is { } r && r.GetBool(name);

    private static int RootInt(JsonElement? root, string name) => root is { } r ? r.GetInt32(name) : -1;

    private static string RootString(JsonElement? root, string name) => root is { } r ? r.GetString(name) : string.Empty;

    private static ulong RootUInt64(JsonElement? root, string name) => root is { } r ? r.GetUInt64(name) : 0UL;
}
