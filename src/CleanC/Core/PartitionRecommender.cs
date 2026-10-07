namespace CleanC.Core;

/// <summary>推荐值计算输入（全部来自本机实测，不含任何写死的机器无关假设）。</summary>
public sealed class RecommendInput
{
    /// <summary>待分盘卷的总容量（字节，= Get-Volume Size）。</summary>
    public required ulong VolumeTotalBytes { get; init; }

    /// <summary>待分盘卷的剩余空间（字节，= Get-Volume SizeRemaining）。</summary>
    public required ulong VolumeFreeBytes { get; init; }

    /// <summary>
    /// 该卷根目录下 pagefile.sys + hiberfil.sys + swapfile.sys 的实测大小之和。
    /// 这些文件必须留在本卷上（或需先迁走），因此要计入“必须保留”。
    /// </summary>
    public long PagingFileBytes { get; init; }

    /// <summary>物理磁盘总容量（用于按比例给出“新分区最小建议值”）。</summary>
    public ulong DiskTotalBytes { get; init; }

    /// <summary>
    /// 由 GetSupportedSize 得到的“最多可收缩出多少字节”。
    /// null 表示尚未分析——此时无法给出上限，UI 必须先分析再允许执行。
    /// </summary>
    public ulong? MaxShrinkableBytes { get; init; }
}

/// <summary>推荐值构成中的一行（用于 UI 逐项展示“这个数是怎么来的”）。</summary>
public sealed class RecommendLine
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string Note { get; init; } = string.Empty;
}

/// <summary>推荐值计算结果。</summary>
public sealed class RecommendResult
{
    // ---- 构成分项（字节） ----
    public long UsedBytes { get; init; }
    public long SysReserveBytes { get; init; }
    public long PagingBytes { get; init; }
    public long GrowthBytes { get; init; }

    /// <summary>
    /// 纯“按需计算”的建议保留值（= 上面各项之和，未受本机可收缩能力限制）。
    /// 用于向用户解释“为什么需要这么多空间”。
    /// </summary>
    public long RawRecommendedKeepBytes { get; init; }

    /// <summary>
    /// 建议保留值 = max(<see cref="RawRecommendedKeepBytes"/>, 硬下限)。
    /// **不受“本机最多能分出多少”影响**——可收缩能力不改变“需要多少”，只影响“能不能分出来”。
    /// </summary>
    public long RecommendedKeepBytes { get; init; }

    /// <summary>绝对下限：低于此值本工具拒绝执行。</summary>
    public long FloorKeepBytes { get; init; }

    /// <summary>滑块允许的最小保留值（= max(硬下限, 总容量 − 最多可分出的空间)）。</summary>
    public long SliderMinKeepBytes { get; init; }

    /// <summary>滑块允许的最大保留值（= 总容量 − 最小可用分出量）。</summary>
    public long SliderMaxKeepBytes { get; init; }

    /// <summary>新分区的“最小建议容量”（小于此值会提示偏小，但不阻断）。</summary>
    public long NewPartMinBytes { get; init; }

    /// <summary>最多可分出的空间（GetSupportedSize 未分析时为 null）。</summary>
    public ulong? MaxShrinkableBytes { get; init; }

    /// <summary>是否具备分盘条件（不满足时 <see cref="BlockReason"/> 给出原因）。</summary>
    public bool CanSplit { get; init; }

    public string? BlockReason { get; init; }

    /// <summary>
    /// 本机可收缩能力导致的额外说明（仅为提示）：当“最少必须保留”大于“按需计算的建议值”时给出，
    /// 例如“Windows 实测最多只能分出 300 GB，因此 C 盘至少要保留 212 GB（高于按需计算的 198 GB）”。
    /// </summary>
    public string? CapabilityNote { get; init; }

    /// <summary>默认滑块位置（保留值）：建议值再按本机可收缩能力夹一次。</summary>
    public long DefaultKeepBytes { get; init; }

    /// <summary>默认滑块位置对应的分出量。</summary>
    public long DefaultSplitBytes { get; init; }

    /// <summary>构成明细（UI 逐行展示）。</summary>
    public IReadOnlyList<RecommendLine> Lines { get; init; } = Array.Empty<RecommendLine>();

    /// <summary>白话解释当前“保留值”是否合适（拖动滑块时实时调用）。</summary>
    public string Explain(long keepBytes)
    {
        if (!CanSplit) return BlockReason ?? "当前不具备分盘条件。";

        long split = TotalCapacityBytes - keepBytes;

        if (keepBytes < SliderMinKeepBytes)
            return $"保留过少：Windows 实测本卷最多只能分出 {SizeFormatter.Format(TotalCapacityBytes - SliderMinKeepBytes)}，" +
                   $"因此最少要保留 {SizeFormatter.Format(SliderMinKeepBytes)}。" +
                   (CapabilityNote is null ? string.Empty : " " + CapabilityNote);
        if (keepBytes > SliderMaxKeepBytes)
            return $"保留过多，分出的新盘只有 {SizeFormatter.Format(split)}，不足以正常存放文件。";
        if (split < HardMinSplitBytes)
            return $"分出量不足 {SizeFormatter.Format(HardMinSplitBytes)}，没有实际意义。";
        if (keepBytes < FloorKeepBytes)
            return $"低于绝对下限 {SizeFormatter.Format(FloorKeepBytes)}：Windows 更新与临时文件可能没有足够空间，本工具会拒绝执行。";
        if (keepBytes < RecommendedKeepBytes)
            return $"低于建议值 {SizeFormatter.Format(RecommendedKeepBytes)}：短期可用，但系统更新与软件缓存增长后可能再次吃紧。";
        if (keepBytes > RecommendedKeepBytes * 1.5)
            return $"高于建议值 {SizeFormatter.Format(RecommendedKeepBytes)}：分出的新盘是 {SizeFormatter.Format(split)}，够用；本卷会更宽裕。";
        return $"在建议范围内（建议保留 ≥ {SizeFormatter.Format(RecommendedKeepBytes)}，本次分出 {SizeFormatter.Format(split)}）。" +
               (CapabilityNote is null ? string.Empty : " " + CapabilityNote);
    }

    /// <summary>本次分盘总容量（保留值 + 分出量）。</summary>
    public long TotalCapacityBytes { get; init; }

    // ---- 常量（集中在此，便于自检与调参） ----

    /// <summary>Windows 11 安装的最低系统要求（64 GB）；保留值不得低于此值。</summary>
    public const long WindowsMinInstallBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>系统更新/组件保留/系统还原的增长缓冲。</summary>
    public const long SysReserveBytesConst = 20L * 1024 * 1024 * 1024;

    /// <summary>增长余量比例（按已用空间）。</summary>
    public const double GrowthRatio = 0.15;

    /// <summary>增长余量的下限。</summary>
    public const long GrowthFloorBytes = 30L * 1024 * 1024 * 1024;

    /// <summary>硬下限中的额外余量比例。</summary>
    public const double FloorExtraRatio = 0.10;

    /// <summary>硬下限中的额外余量下限。</summary>
    public const long FloorExtraFloorBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>新分区最小建议值比例（按物理磁盘容量）。</summary>
    public const double NewPartMinRatio = 0.08;

    /// <summary>新分区最小建议值下限。</summary>
    public const long NewPartMinFloorBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>低于此分出量视为没有实际意义（硬性拒绝）。</summary>
    public const long HardMinSplitBytes = 1L * 1024 * 1024 * 1024;
}

/// <summary>
/// 分盘推荐值引擎：按**本机实测**给出「建议保留 ≥ X GB」与「绝对下限」，并逐项展示构成。
/// 纯函数、无 IO，可直接单元断言（见 tools/UiSmokeTest）。
/// </summary>
public static class PartitionRecommender
{
    private const long Gb = 1024L * 1024 * 1024;

    public static RecommendResult Recommend(RecommendInput input)
    {
        long total = ClampToLong(input.VolumeTotalBytes);
        long free = ClampToLong(input.VolumeFreeBytes);
        if (free > total) free = total;                 // 口径异常时按“全空”处理
        long diskTotal = input.DiskTotalBytes > 0 ? ClampToLong(input.DiskTotalBytes) : total;
        long paging = Math.Max(0, input.PagingFileBytes);

        long used = total - free;
        long sysReserve = RecommendResult.SysReserveBytesConst;
        long growth = Math.Max((long)(used * RecommendResult.GrowthRatio), RecommendResult.GrowthFloorBytes);

        // 按需计算的建议值（未受“本机最多能分出多少”限制）
        long rawRecommended = used + sysReserve + paging + growth;

        long floor = Math.Max(
            RecommendResult.WindowsMinInstallBytes,
            used + Math.Max((long)(used * RecommendResult.FloorExtraRatio), RecommendResult.FloorExtraFloorBytes));
        if (floor > total) floor = total;

        long newPartMin = Math.Max((long)(diskTotal * RecommendResult.NewPartMinRatio), RecommendResult.NewPartMinFloorBytes);
        if (newPartMin > total) newPartMin = Math.Max(total / 4, RecommendResult.HardMinSplitBytes);

        // 滑块上界：至少留出 HardMinSplitBytes 可分
        long sliderMaxKeep = Math.Max(0, total - RecommendResult.HardMinSplitBytes);

        // 滑块下界：硬下限，以及“受 SizeMin 限制最多只能分出这么多”推出的下限
        long sliderMinKeep = floor;
        if (input.MaxShrinkableBytes is { } maxShrink)
        {
            long minKeepByMaxShrink = Math.Max(0, total - ClampToLong(maxShrink));
            if (minKeepByMaxShrink > sliderMinKeep) sliderMinKeep = minKeepByMaxShrink;
        }

        // 分盘可行性：先看“本机实测能分出多少”（不可移动文件钉死时给出的原因最有用），再看滑块区间
        bool canSplit;
        string? blockReason = null;
        ulong? shrinkCap = input.MaxShrinkableBytes;
        if (shrinkCap is { } cap && cap < (ulong)RecommendResult.HardMinSplitBytes)
        {
            canSplit = false;
            blockReason =
                $"当前分不出空间：Windows 实测本卷最多只能收缩出 {SizeFormatter.Format((long)cap)}" +
                $"（卷容量 {SizeFormatter.Format(total)}）。这通常是因为页面文件、卷影副本（系统还原）或" +
                "文件系统元数据等不可移动文件被钉在了分区靠后的位置，导致分区无法再往后缩。" +
                "请按「为什么分不出空间」的补救清单逐条处理后，重新点「分析可压缩空间」。";
        }
        else if (sliderMinKeep >= sliderMaxKeep)
        {
            canSplit = false;
            blockReason =
                $"当前不具备分盘条件：该卷总容量 {SizeFormatter.Format(total)}，但必须保留 " +
                $"{SizeFormatter.Format(sliderMinKeep)}（受 Windows 最低要求或可收缩下限限制），" +
                $"已没有可分出 {SizeFormatter.Format(RecommendResult.HardMinSplitBytes)} 以上的空间。";
        }
        else
        {
            canSplit = true;
        }

        // 建议值 = 按需计算值与硬下限取大；不被“本机最多能分出多少”抬高
        long recommended = Math.Max(rawRecommended, floor);
        if (recommended > total) recommended = total;

        // 滑块默认位置：建议值再按可收缩能力夹一次（用户拖不出不可行的方案）
        long defaultKeep = canSplit ? Clamp(recommended, sliderMinKeep, sliderMaxKeep) : total;
        long defaultSplit = Math.Max(0, total - defaultKeep);

        string? capabilityNote = canSplit && sliderMinKeep > recommended
            ? $"受本机可收缩能力限制：Windows 实测最多只能分出 {SizeFormatter.Format(total - sliderMinKeep)}，" +
              $"因此该卷最少要保留 {SizeFormatter.Format(sliderMinKeep)}（高于按需计算的 {SizeFormatter.Format(recommended)}）。"
            : null;

        var lines = new List<RecommendLine>
        {
            new() { Label = "当前卷已用", Value = SizeFormatter.Format(used),
                    Note = $"总容量 {SizeFormatter.Format(total)}，剩余 {SizeFormatter.Format(free)}" },
            new() { Label = "Windows 更新与系统保留", Value = "+" + SizeFormatter.Format(sysReserve),
                    Note = "组件存储（WinSxS）、更新下载、系统还原增长缓冲" },
            new() { Label = "页面文件 + 休眠文件 + 交换文件", Value = "+" + SizeFormatter.Format(paging),
                    Note = paging > 0 ? "已按本卷实测大小计入（这些文件默认不能移出本卷）" : "本卷根目录未实测到这些文件" },
            new() { Label = $"增长余量（已用的 {RecommendResult.GrowthRatio * 100:0}%）", Value = "+" + SizeFormatter.Format(growth),
                    Note = $"下限 {SizeFormatter.Format(RecommendResult.GrowthFloorBytes)}；软件缓存与临时文件会持续增长" },
        };

        return new RecommendResult
        {
            UsedBytes = used,
            SysReserveBytes = sysReserve,
            PagingBytes = paging,
            GrowthBytes = growth,
            RawRecommendedKeepBytes = rawRecommended,
            RecommendedKeepBytes = recommended,
            FloorKeepBytes = floor,
            SliderMinKeepBytes = sliderMinKeep,
            SliderMaxKeepBytes = sliderMaxKeep,
            NewPartMinBytes = newPartMin,
            MaxShrinkableBytes = input.MaxShrinkableBytes,
            CanSplit = canSplit,
            BlockReason = blockReason,
            CapabilityNote = capabilityNote,
            DefaultKeepBytes = defaultKeep,
            DefaultSplitBytes = defaultSplit,
            TotalCapacityBytes = total,
            Lines = lines,
        };
    }

    /// <summary>把 UI 上用户选择的“保留值”换算为合法的分盘方案；不合法时返回 null 并给出原因。</summary>
    public static bool TryBuildSplit(RecommendResult r, long keepBytes, out long splitBytes, out string? error)
    {
        splitBytes = 0;
        error = null;

        if (!r.CanSplit)
        {
            error = r.BlockReason ?? "当前不具备分盘条件。";
            return false;
        }
        if (keepBytes < r.FloorKeepBytes)
        {
            error = $"保留值低于绝对下限 {SizeFormatter.Format(r.FloorKeepBytes)}，可能影响 Windows 更新与系统运行。";
            return false;
        }
        if (keepBytes > r.SliderMaxKeepBytes)
        {
            error = $"保留值过大：滑块上限为 {SizeFormatter.Format(r.SliderMaxKeepBytes)}。";
            return false;
        }

        splitBytes = r.TotalCapacityBytes - keepBytes;
        if (splitBytes < RecommendResult.HardMinSplitBytes)
        {
            error = $"分出量不足 {SizeFormatter.Format(RecommendResult.HardMinSplitBytes)}，没有实际意义。";
            return false;
        }
        if (r.MaxShrinkableBytes is { } max && (ulong)splitBytes > max)
        {
            error = $"分出量超过 Windows 实测上限：最多 {SizeFormatter.Format((long)max)}（受不可移动文件位置限制）。";
            return false;
        }
        return true;
    }

    /// <summary>默认推荐方案（UI 打开时滑块的位置）。</summary>
    public static (long KeepBytes, long SplitBytes) DefaultOf(RecommendResult r) =>
        (r.DefaultKeepBytes, r.DefaultSplitBytes);

    // ---------- 内部 ----------

    private static long ClampToLong(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;

    private static long Clamp(long v, long min, long max) => v < min ? min : (v > max ? max : v);
}
