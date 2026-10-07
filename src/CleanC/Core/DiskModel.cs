namespace CleanC.Core;

/// <summary>磁盘分区表样式。</summary>
public enum PartitionStyle
{
    Unknown,
    Mbr,
    Gpt,
    Raw,
}

/// <summary>
/// 分区类别。分区操作采用**白名单**：只有 <see cref="BasicData"/> 允许参与收缩/扩展/删除，
/// 其余一律拒绝（UI 不列出可选项 + 引擎二次拒绝）。
///
/// 判定依据是 Windows 自己的 <c>MSFT_Partition.Type</c> 字符串（Storage 模块官方映射），
/// 归一化后的 GPT 类型 GUID 与 MBR 类型作为回退——详见 <see cref="DiskLayoutService.Classify"/>。
/// </summary>
public enum PartitionKind
{
    /// <summary>基本数据分区（GPT Basic / MBR IFS）——唯一允许操作的类型。</summary>
    BasicData,

    /// <summary>EFI 系统分区（含引导文件，删除后系统无法启动）。</summary>
    EfiSystem,

    /// <summary>Microsoft 保留分区（MSR）。</summary>
    MicrosoftReserved,

    /// <summary>Microsoft 恢复分区（WinRE）。</summary>
    Recovery,

    /// <summary>LDM 元数据分区（动态磁盘）。</summary>
    LdmMetadata,

    /// <summary>LDM 数据分区（动态磁盘）。</summary>
    LdmData,

    /// <summary>Storage Spaces 保护分区（存储池成员，不属可调整范围）。</summary>
    StorageSpaceProtective,

    /// <summary>卷影副本分区。</summary>
    ShadowCopy,

    /// <summary>其它未知类型（保守对待，同样不可操作）。</summary>
    Other,
}

/// <summary>
/// 健康状态。
///
/// 注意：<c>MSFT_Disk.HealthStatus</c> / <c>MSFT_Volume.HealthStatus</c> 都是
/// <c>Storage.types.ps1xml</c> 里定义的 **ScriptProperty，返回英文字符串**
/// （"Healthy" / "Warning" / "Unhealthy"，无法判定时 "Unknown"）——
/// 不是数字。早先按数字转换会静默失败并一律显示为健康，从而放过坏卷。
/// </summary>
public enum HealthState
{
    Healthy,
    Warning,
    Unhealthy,
    Unknown,
}

/// <summary>物理磁盘（来源：Get-Disk → MSFT_Disk）。</summary>
public sealed class DiskInfo
{
    public int Number { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
    public ulong SizeBytes { get; init; }
    public PartitionStyle Style { get; init; } = PartitionStyle.Unknown;
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public string BusType { get; init; } = string.Empty;
    public string OperationalStatus { get; init; } = string.Empty;
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>健康状态（由 <c>HealthStatus</c> 字符串解析）。</summary>
    public HealthState Health { get; init; } = HealthState.Unknown;

    /// <summary>Windows 返回的 HealthStatus 原文（诊断用）。</summary>
    public string HealthRaw { get; init; } = string.Empty;

    public string HealthText => HealthStateText.Of(Health);

    public string StyleText => Style switch
    {
        PartitionStyle.Gpt => "GPT",
        PartitionStyle.Mbr => "MBR",
        PartitionStyle.Raw => "RAW（未初始化）",
        _ => "未知",
    };

    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? $"磁盘 {Number}" : $"磁盘 {Number}：{FriendlyName}";

    /// <summary>动态磁盘（LDM）——本工具不操作（Windows 11 已弃用动态磁盘）。</summary>
    public bool HasLdmPartitions { get; init; }

    /// <summary>下拉框用的选择项文本。</summary>
    public string ChoiceText =>
        $"{DisplayName}　{SizeFormatter.Format((long)SizeBytes)}　{StyleText}" +
        (IsBoot ? "　[启动盘]" : string.Empty) +
        (HasLdmPartitions ? "　[动态磁盘：不操作]" : string.Empty) +
        (Health != HealthState.Healthy && Health != HealthState.Unknown ? $"　[{HealthText}]" : string.Empty);
}

/// <summary>分区（来源：Get-Partition → MSFT_Partition）。</summary>
public sealed class PartitionInfo
{
    public int DiskNumber { get; init; }
    public int PartitionNumber { get; init; }
    public char? DriveLetter { get; init; }
    public ulong OffsetBytes { get; init; }
    public ulong SizeBytes { get; init; }

    /// <summary>Windows 返回的 <c>Type</c> 原文（如 "Basic" / "System" / "IFS" / "Recovery"）——非本地化。</summary>
    public string TypeText { get; init; } = string.Empty;

    /// <summary>GPT 类型 GUID 原文（**带大括号**，如 <c>{ebd0a0a2-…}</c>）；MBR 磁盘上为空。</summary>
    public string GptType { get; init; } = string.Empty;

    public string Guid { get; init; } = string.Empty;
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsHidden { get; init; }
    public bool IsActive { get; init; }
    public bool NoDefaultDriveLetter { get; init; }

    /// <summary>MBR 类型（如 7 = IFS/NTFS）；GPT 磁盘上为 0。</summary>
    public int MbrType { get; init; }

    /// <summary>按 Windows Type / GPT GUID / MBR 类型判定的类别（决定是否允许操作）。</summary>
    public PartitionKind Kind { get; init; } = PartitionKind.Other;

    /// <summary>判定理由（中文一句话，用于「磁盘明细」弹窗与日志，避免"看不出为什么不可操作"）。</summary>
    public string ClassificationReason { get; init; } = string.Empty;

    public ulong EndBytes => OffsetBytes + SizeBytes;

    public string LetterText => DriveLetter is { } c ? $"{c}:" : "（无盘符）";

    public string DisplayName => $"{(DriveLetter is { } c ? $"{c}: " : string.Empty)}磁盘 {DiskNumber} 分区 {PartitionNumber}（{SizeFormatter.Format((long)SizeBytes)}）";

    public string KindText => PartitionKindText.Of(Kind, TypeText);

    /// <summary>只有基本数据分区可参与分区操作。</summary>
    public bool IsOperable => Kind == PartitionKind.BasicData;
}

/// <summary>类别与健康状态的中文文案（UI、说明书、日志共用一处，避免各处口径不一）。</summary>
public static class PartitionKindText
{
    public static string Of(PartitionKind kind, string? typeText = null) => kind switch
    {
        PartitionKind.BasicData => "基本数据",
        PartitionKind.EfiSystem => "EFI 系统分区",
        PartitionKind.MicrosoftReserved => "MSR 保留分区",
        PartitionKind.Recovery => "恢复分区（WinRE）",
        PartitionKind.LdmMetadata => "动态磁盘元数据",
        PartitionKind.LdmData => "动态磁盘数据",
        PartitionKind.StorageSpaceProtective => "存储池保护分区",
        PartitionKind.ShadowCopy => "卷影副本",
        _ => string.IsNullOrWhiteSpace(typeText) ? "未知类型" : $"其它（{typeText}）",
    };
}

/// <summary>健康状态的中文文案。</summary>
public static class HealthStateText
{
    public static string Of(HealthState state) => state switch
    {
        HealthState.Healthy => "健康",
        HealthState.Warning => "警告",
        HealthState.Unhealthy => "不健康",
        _ => "未知",
    };
}

/// <summary>卷（来源：Get-Volume → MSFT_Volume）。</summary>
public sealed class VolumeInfo
{
    public char? DriveLetter { get; init; }
    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public ulong SizeBytes { get; init; }
    public ulong FreeBytes { get; init; }
    public string DriveType { get; init; } = string.Empty;

    /// <summary>健康状态（由 <c>HealthStatus</c> 字符串解析，而非数字）。</summary>
    public HealthState Health { get; init; } = HealthState.Unknown;

    /// <summary>Windows 返回的 HealthStatus 原文（诊断用）。</summary>
    public string HealthRaw { get; init; } = string.Empty;

    public string HealthText => HealthStateText.Of(Health);

    public ulong UsedBytes => SizeBytes > FreeBytes ? SizeBytes - FreeBytes : 0;

    /// <summary>Resize-Partition 只支持 NTFS / RAW。</summary>
    public bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public string DisplayName
    {
        get
        {
            string letter = DriveLetter is { } c ? $"{c}:" : "（无盘符）";
            string label = string.IsNullOrWhiteSpace(Label) ? string.Empty : $"（{Label}）";
            return $"{letter}{label}";
        }
    }
}

/// <summary>一次 GetSupportedSize 调用的结果（MSFT_Partition.GetSupportedSize 方法）。</summary>
public sealed class SupportedSize
{
    /// <summary>WMI 返回码（0 成功；4097 尺寸不支持；42008 卷有错误；42009 未知文件系统；40001 拒绝访问）。</summary>
    public int ReturnValue { get; init; }

    /// <summary>可收缩到的最小字节数（由磁盘碎片整理程序按**不可移动文件**的位置算出）。</summary>
    public ulong MinBytes { get; init; }

    /// <summary>可扩展到的最大字节数 = 当前大小 + **紧随其后的**空闲 extent 之和。</summary>
    public ulong MaxBytes { get; init; }

    public string ExtendedStatus { get; init; } = string.Empty;

    public bool Ok => ReturnValue == 0;

    /// <summary>理论上最多能收缩出多少空间（当前大小 − SizeMin）。</summary>
    public ulong MaxShrinkableBytes(ulong currentSize) =>
        currentSize > MinBytes ? currentSize - MinBytes : 0;

    /// <summary>紧随其后的连续空闲空间（SizeMax − 当前大小）；为 0 即“无法扩展”。</summary>
    public ulong AdjacentFreeBytes(ulong currentSize) =>
        MaxBytes > currentSize ? MaxBytes - currentSize : 0;
}

/// <summary>一次磁盘布局探测的完整快照（**只读**，不含任何改动入口）。</summary>
public sealed class DiskLayoutSnapshot
{
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
    public required IReadOnlyList<PartitionInfo> Partitions { get; init; }
    public required IReadOnlyList<VolumeInfo> Volumes { get; init; }

    /// <summary>探测过程中的非致命问题（逐条中文，用于 UI 提示与日志）。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public int ElapsedMs { get; init; }

    /// <summary>
    /// 本次探测时进程是否具备管理员权限。
    /// 磁盘/分区信息（MSFT_Disk/MSFT_Partition/MSFT_Volume）受 WMI 命名空间 ACL 保护，
    /// **非管理员查询一律“拒绝访问”**，因此本字段为 false 时布局数据必然为空，UI 需据此硬性拦截。
    /// </summary>
    public bool IsElevated { get; init; }

    /// <summary>系统盘（Windows 目录所在卷）的盘符，如 'C'；判定失败返回 null。</summary>
    public char? SystemDriveLetter { get; init; }

    public PartitionInfo? PartitionByDriveLetter(char letter) =>
        Partitions.FirstOrDefault(p => p.DriveLetter is { } c && char.ToUpperInvariant(c) == char.ToUpperInvariant(letter));

    public DiskInfo? DiskByNumber(int number) => Disks.FirstOrDefault(d => d.Number == number);

    public VolumeInfo? VolumeByDriveLetter(char letter) =>
        Volumes.FirstOrDefault(v => v.DriveLetter is { } c && char.ToUpperInvariant(c) == char.ToUpperInvariant(letter));

    /// <summary>某磁盘上按起始偏移排序的分区（用于绘制条带图与判定相邻性）。</summary>
    public IReadOnlyList<PartitionInfo> PartitionsOfDisk(int diskNumber) =>
        Partitions.Where(p => p.DiskNumber == diskNumber).OrderBy(p => p.OffsetBytes).ToArray();

    /// <summary>
    /// 某磁盘上的**可用来新建分区**的未分配空间，按起始偏移升序。
    ///
    /// 只做几何计算，不查询系统：把该磁盘上所有分区按偏移排好，逐段取"缝隙"，
    /// 最后一段从最后一个分区的末尾一直算到磁盘末尾。三处收窄，缺一不可：
    /// <list type="bullet">
    ///   <item>小于 1 MB 的缝隙没有使用价值（Windows 也不会在那里建卷），略过；</item>
    ///   <item>**开头 1 MB 保留**（GPT 主分区表 + 对齐填充）——实测本机磁盘 0 前面的 1 MB 就属于这段，
    ///         它看起来像"未分配"，但那是分区表所在，用它建卷轻则被拒、重则破坏分区表；</item>
    ///   <item>**末尾 1 MB 保留**（GPT 备份分区表）——用满到最后一字节，系统会报"请求的大小不受支持"。</item>
    /// </list>
    /// 返回的区间因此是"可以直接拿去建卷"的区间，不再需要调用方再扣一遍。
    /// </summary>
    public IReadOnlyList<FreeExtent> FreeExtentsOfDisk(int diskNumber)
    {
        var disk = DiskByNumber(diskNumber);
        if (disk is null) return Array.Empty<FreeExtent>();

        ulong usableStart = (ulong)FreeExtent.ReservedHeadBytes;
        ulong usableEnd = disk.SizeBytes > (ulong)FreeExtent.ReservedTailBytes
            ? disk.SizeBytes - (ulong)FreeExtent.ReservedTailBytes
            : 0;
        if (usableEnd <= usableStart) return Array.Empty<FreeExtent>();

        var list = new List<FreeExtent>();
        ulong cursor = 0;
        foreach (var p in PartitionsOfDisk(diskNumber))
        {
            AddGap(list, diskNumber, cursor, p.OffsetBytes, usableStart, usableEnd);

            ulong end = p.EndBytes;
            if (end > cursor) cursor = end;
        }
        AddGap(list, diskNumber, cursor, disk.SizeBytes, usableStart, usableEnd);

        return list;
    }

    /// <summary>把 [from, to) 这段缝隙按可用范围裁剪后加入列表（不足 1 MB 就丢掉）。</summary>
    private static void AddGap(List<FreeExtent> list, int diskNumber, ulong from, ulong to,
        ulong usableStart, ulong usableEnd)
    {
        ulong start = Math.Max(from, usableStart);
        ulong end = Math.Min(to, usableEnd);
        if (end <= start || end - start < (ulong)FreeExtent.MinUsefulBytes) return;

        list.Add(new FreeExtent { DiskNumber = diskNumber, StartBytes = start, SizeBytes = end - start });
    }

    /// <summary>
    /// 分区起始偏移必须按 1 MB 对齐，否则 <c>New-Partition -Offset</c> 会被系统直接拒绝
    /// （"The specified offset is not aligned"）。收缩过的分区末尾不一定对齐，
    /// 因此定位未分配空间时要把起点**向上取整**到 1 MB。
    /// </summary>
    public const long AlignmentBytes = 1024L * 1024;

    /// <summary>把偏移向上取整到 1 MB 边界。</summary>
    public static ulong AlignUp(ulong offset) =>
        offset % (ulong)AlignmentBytes == 0 ? offset : (offset / (ulong)AlignmentBytes + 1) * (ulong)AlignmentBytes;

    /// <summary>
    /// 把大小**向下取整**到 1 MB 边界。
    ///
    /// 为什么必须做：Windows 建分区时会按对齐补足尺寸，给一个"除不尽 1 MB"的大小，
    /// 它会要一个比可用量更大的整数 MB，于是直接报
    /// **Not enough available capacity**（真事故：收缩成功、新建失败，磁盘上留下 156.5 GB 未分配）。
    /// 分区起点和终点都落在 1 MB 边界上，才不会出现这种"差几百 KB 建不出来"的问题。
    /// </summary>
    public static ulong AlignDown(ulong size) => size - size % (ulong)AlignmentBytes;

    /// <summary>已占用的盘符（含网络映射与 subst，避免新建分区抢到已用字母）。</summary>
    public IReadOnlySet<char> UsedDriveLetters { get; init; } = new HashSet<char>();

    /// <summary>
    /// 本次探测的**原始 JSON 报文**（Get-Disk / Get-Partition / Get-Volume 的合并输出）。
    /// 用于「导出诊断信息」与回归测试夹具：字段名或取值一旦变化，可以用真实数据立刻复现。
    /// </summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>该磁盘上可操作（基本数据）的分区数。</summary>
    public int OperableCountOfDisk(int diskNumber) =>
        Partitions.Count(p => p.DiskNumber == diskNumber && p.IsOperable);
}

/// <summary>
/// 磁盘上的一段**未分配空间**（不属于任何分区，可直接用来新建分区）。
///
/// 它出现在两种情形：① 磁盘本来就没分满；② 上一个操作只完成了一半
/// （收缩成功但新建失败 / 删除分区成功但扩容失败）。后者正是「新建分区」页要修的状态。
/// </summary>
public sealed class FreeExtent
{
    public required int DiskNumber { get; init; }
    public required ulong StartBytes { get; init; }
    public required ulong SizeBytes { get; init; }

    public ulong EndBytes => StartBytes + SizeBytes;

    /// <summary>小于 1 MB 的缝隙没有使用价值，不列给用户（Windows 也不会在那里建卷）。</summary>
    public const long MinUsefulBytes = 1024L * 1024;

    /// <summary>
    /// 磁盘**开头**的保留区：GPT 主分区表 + 对齐填充。实测本机磁盘 0 的 EFI 分区从 1 MB 起，
    /// 前面正好空出 1 MB —— 那段空间看着像"未分配"，但**绝不能用它建卷**：
    /// 轻则系统拒绝，重则覆盖分区表。所以枚举未分配空间时直接从 1 MB 起算。
    /// </summary>
    public const long ReservedHeadBytes = 1024L * 1024;

    /// <summary>
    /// 磁盘**末尾**的保留区：GPT 备份分区表（末尾 33 个扇区，向上取整到 1 MB 留足余量）。
    /// 用满到磁盘最后一字节去建分区，系统会因"请求的大小不受支持"直接失败——
    /// 所以尾部未分配空间要提前扣掉这 1 MB。
    /// </summary>
    public const long ReservedTailBytes = 1024L * 1024;

    /// <summary>Windows 能真正利用的起始偏移：把原始起点按 1 MB 对齐（见 <see cref="DiskLayoutSnapshot.AlignUp"/>）。</summary>
    public ulong AlignedStartBytes => DiskLayoutSnapshot.AlignUp(StartBytes);

    /// <summary>
    /// **实际能建出来的最大尺寸**：起点对齐后再把尺寸向下取整到 1 MB。
    /// 界面的「用满整段」、执行前的复验、计划里的尺寸全都用这个值——
    /// 用它才不会撞上 Windows 的对齐补足（Not enough available capacity）。
    /// </summary>
    public ulong AlignedSizeBytes =>
        EndBytes > AlignedStartBytes
            ? DiskLayoutSnapshot.AlignDown(EndBytes - AlignedStartBytes)
            : 0;

    /// <summary>对齐过程一共"用不上"的字节数（起点前的缝隙 + 尾部取整掉的部分）。</summary>
    public long AlignmentWasteBytes => (long)(SizeBytes - AlignedSizeBytes);

    public string SizeText => SizeFormatter.Format((long)SizeBytes);

    /// <summary>下拉框里的一行：既能看出位置，也能看出多大。</summary>
    public string DisplayName =>
        $"起始 {SizeFormatter.Format((long)StartBytes)} · 可用 {SizeFormatter.Format((long)AlignedSizeBytes)}" +
        (AlignmentWasteBytes > 0 ? $"（共 {SizeText}，扣掉 1 MB 对齐后可用 {SizeFormatter.Format((long)AlignedSizeBytes)}）" : string.Empty);

    public static FreeExtent Of(int diskNumber, ulong start, ulong size) =>
        new() { DiskNumber = diskNumber, StartBytes = start, SizeBytes = size };

    /// <summary>
    /// 直接放进 ComboBox 的就是这个对象——**必须重写 ToString**，
    /// 否则下拉框里会显示成类型名「CleanC.Core.FreeExtent」（已经踩过一次）。
    /// </summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// 分区身份：破坏性操作前的“复验”凭据。优先使用分区 GUID（唯一且稳定）；
/// GUID 缺失时回退到「磁盘号 + 起始偏移 + 大小」三元组。任何一个字段与计划不符即中止执行。
/// </summary>
public readonly record struct PartitionIdentity(
    int DiskNumber, int PartitionNumber, string Guid, ulong OffsetBytes, ulong SizeBytes)
{
    public static PartitionIdentity Of(PartitionInfo p) =>
        new(p.DiskNumber, p.PartitionNumber, p.Guid, p.OffsetBytes, p.SizeBytes);

    /// <summary>与另一个身份是否一致（GUID 可用时以 GUID 为准，否则比对偏移与大小）。</summary>
    public bool Matches(PartitionInfo other)
    {
        if (!string.IsNullOrWhiteSpace(Guid) && !string.IsNullOrWhiteSpace(other.Guid))
            return Guid.Equals(other.Guid, StringComparison.OrdinalIgnoreCase);
        return DiskNumber == other.DiskNumber && PartitionNumber == other.PartitionNumber;
    }

    /// <summary>是否与另一个身份完全一致（含大小，用于确认分区未被改动过）。</summary>
    public bool MatchesExactly(PartitionInfo other) =>
        Matches(other) && OffsetBytes == other.OffsetBytes && SizeBytes == other.SizeBytes;

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Guid)
            ? $"磁盘{DiskNumber} 分区{PartitionNumber} 偏移{OffsetBytes} 大小{SizeBytes}"
            : $"GUID {Guid}（偏移 {OffsetBytes}，大小 {SizeBytes}）";
}
