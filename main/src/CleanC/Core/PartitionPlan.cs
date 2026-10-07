namespace CleanC.Core;

/// <summary>分区计划类型。</summary>
public enum PartitionOpKind
{
    None,

    /// <summary>分盘：收缩源分区 → 在释放出的未分配空间新建分区。</summary>
    Split,

    /// <summary>合盘：把源分区（数据先搬到暂存处）删除 → 扩容目标分区。</summary>
    Merge,

    /// <summary>
    /// 新建：在磁盘上**已有的未分配空间**里创建分区并格式化。
    /// 不动任何现有分区，因此它既是常规操作，也是「上一个操作只做了一半」的修复路径。
    /// </summary>
    Create,
}

/// <summary>计划中的一步（用于 UI 预览与说明书，四段式：目的/操作/预期/失败怎么办）。</summary>
public sealed class PartitionStep
{
    public required int Index { get; init; }
    public required string Title { get; init; }

    /// <summary>这一步会做什么（白话）。</summary>
    public required string Action { get; init; }

    /// <summary>预期看到的结果。</summary>
    public required string Expected { get; init; }

    /// <summary>失败/异常时的处理动作。</summary>
    public required string OnFailure { get; init; }

    /// <summary>等价的 PowerShell 命令（说明书里可复制；空表示无命令，如人工步骤）。</summary>
    public string PowerShell { get; init; } = string.Empty;

    /// <summary>是否破坏性（不可逆）。</summary>
    public bool IsDestructive { get; init; }

    /// <summary>执行中是否可安全中止（false = 一旦发出不可打断，必须等本步完成）。</summary>
    public bool IsInterruptible { get; init; } = true;
}

/// <summary>校验结果条目（阻断原因或提示；UI 据此禁用执行按钮并逐条展示）。</summary>
public sealed class BlockReason
{
    public required string Title { get; init; }
    public required string Detail { get; init; }

    /// <summary>可行动的补救动作（可为空）。</summary>
    public string Remedy { get; init; } = string.Empty;

    /// <summary>
    /// 仅提示、**不阻断**执行（例如“删除源分区前无法预先实测可扩展范围”属于正常现象）。
    /// 只有 <see cref="IsWarning"/> 为 false 的条目才会被 UI 与执行引擎当作拦截条件。
    /// </summary>
    public bool IsWarning { get; init; }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Remedy) ? $"{Title}：{Detail}" : $"{Title}：{Detail}（补救：{Remedy}）";
}

/// <summary>校验结果的便捷视图。</summary>
public static class BlockReasonExtensions
{
    /// <summary>真正会阻断执行的条目（排除仅提示项）。</summary>
    public static IReadOnlyList<BlockReason> Blocking(this IReadOnlyList<BlockReason> reasons) =>
        reasons.Where(r => !r.IsWarning).ToArray();

    public static bool HasBlocking(this IReadOnlyList<BlockReason> reasons) =>
        reasons.Any(r => !r.IsWarning);
}

/// <summary>分盘计划。</summary>
public sealed class PartitionPlan
{
    public required PartitionOpKind Kind { get; init; }
    public required string Title { get; init; }
    public required int DiskNumber { get; init; }
    public PartitionStyle DiskStyle { get; init; } = PartitionStyle.Unknown;
    public string DiskName { get; init; } = string.Empty;

    /// <summary>被收缩/被删除的分区身份（执行前逐字段复验）。</summary>
    public PartitionIdentity Source { get; init; }

    /// <summary>被扩容的分区身份（分盘时 NewPartition 为新建；合盘时为目标分区）。</summary>
    public PartitionIdentity? Target { get; init; }

    /// <summary>新建分区（分盘）的规划参数；合盘时为 null。</summary>
    public char? NewDriveLetter { get; init; }
    public string NewVolumeLabel { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="PartitionOpKind.Create"/> 计划里新分区的起始偏移（已按 1 MB 对齐）。
    /// 显式指定偏移是为了**精确落在已核验的那段未分配空间上**——不指定偏移时由 Windows 自己挑，
    /// 磁盘上有多段空闲时就可能挑到别处。
    /// </summary>
    public long NewPartitionStartBytes { get; init; }

    /// <summary>
    /// 计划的口径说明（例如"实际可建尺寸比理论值少 0.6 MB，因为要按 1 MB 对齐"）。
    /// 计划与执行结果之间出现不可避免的小差异时，必须写在这里，不能让用户以为工具算错了。
    /// </summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// 合盘：删除前对源分区内容的**只读盘点**结果（文件数/体积/主要目录）。
    /// 工具不替用户备份，因此这个数字必须如实记录进说明书与操作日志——删完之后它就是唯一的线索。
    /// </summary>
    public PartitionImpact? Impact { get; init; }

    // ---- 容量变化（字节；用 long 便于直接展示与断言） ----
    public long SourceSizeBeforeBytes { get; init; }
    public long SourceSizeAfterBytes { get; init; }
    public long FreedBytes { get; init; }
    public long TargetSizeBeforeBytes { get; init; }
    public long TargetSizeAfterBytes { get; init; }

    /// <summary>本次为**演练**（只生成说明书与命令，不执行任何破坏性动作）。</summary>
    public bool IsDryRun { get; init; } = true;

    public IReadOnlyList<PartitionStep> Steps { get; init; } = Array.Empty<PartitionStep>();
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

// ---------- 校验 ----------

/// <summary>
/// 分区计划校验器：把「能不能做」判定为可读的阻断清单。
/// 所有守卫集中在此，UI 与执行引擎共用同一套判定（UI 灰显 + 引擎二次拒绝）。
/// </summary>
public static class PartitionPlanValidator
{
    private const long Gb = 1024L * 1024 * 1024;

    // ---------- 分盘 ----------

    public static IReadOnlyList<BlockReason> ValidateSplit(
        DiskProbeResult probe, PartitionInfo source, SupportedSize supported, long keepBytes, char? newLetter)
    {
        var blocks = new List<BlockReason>();
        var layout = probe.Layout;

        CheckCommon(probe, source, layout, blocks, "收缩");

        if (!supported.Ok)
        {
            blocks.Add(new BlockReason
            {
                Title = "无法分析可收缩范围",
                Detail = DiskLayoutService.ExplainReturnCode(supported.ReturnValue) +
                         (string.IsNullOrWhiteSpace(supported.ExtendedStatus) ? string.Empty : $"（{supported.ExtendedStatus}）"),
                Remedy = "确认以管理员身份运行；若返回 42008（卷有错误）请先运行 chkdsk。",
            });
        }

        // 滑块/推荐值口径校验（复用推荐器，保证 UI 与校验同一套数学）
        long total = ClampToLong(source.SizeBytes);
        long free = source.DriveLetter is { } sl ? ClampToLong(layout.VolumeByDriveLetter(sl)?.FreeBytes ?? 0) : 0;
        long diskTotal = ClampToLong(layout.DiskByNumber(source.DiskNumber)?.SizeBytes ?? source.SizeBytes);

        var rec = PartitionRecommender.Recommend(new RecommendInput
        {
            VolumeTotalBytes = source.SizeBytes,
            VolumeFreeBytes = (ulong)Math.Max(0, free),
            PagingFileBytes = source.DriveLetter is { } pl ? DiskLayoutService.MeasurePagingFiles(pl) : 0,
            DiskTotalBytes = (ulong)Math.Max(0, diskTotal),
            MaxShrinkableBytes = supported.Ok ? supported.MaxShrinkableBytes(source.SizeBytes) : null,
        });

        if (!rec.CanSplit)
        {
            blocks.Add(new BlockReason
            {
                Title = "当前不具备分盘条件",
                Detail = rec.BlockReason ?? "推荐值引擎判定不可分盘。",
                Remedy = "按下面「为什么分不出空间」中的补救清单逐条处理后，重新点「分析可压缩空间」。",
            });
        }
        else if (!PartitionRecommender.TryBuildSplit(rec, keepBytes, out long split, out string? err))
        {
            blocks.Add(new BlockReason
            {
                Title = "保留值与实测范围不符",
                Detail = err ?? "无法生成合法方案。",
                Remedy = $"把「C 盘保留」调到 {SizeFormatter.Format(rec.SliderMinKeepBytes)} ~ " +
                         $"{SizeFormatter.Format(rec.SliderMaxKeepBytes)} 之间。",
            });
        }

        // 新分区盘符
        if (newLetter is not { } letter)
        {
            blocks.Add(new BlockReason { Title = "未选择新分区盘符", Detail = "新建分区需要一个未被占用的盘符。", Remedy = "在下拉框中选择一个盘符。" });
        }
        else
        {
            if (layout.UsedDriveLetters.Contains(letter))
                blocks.Add(new BlockReason
                {
                    Title = "盘符已被占用",
                    Detail = $"{letter}: 已被本机某个卷、网络映射或 subst 占用。",
                    Remedy = "换一个盘符。",
                });
            if (letter is 'A' or 'B')
                blocks.Add(new BlockReason { Title = "盘符不可用", Detail = "A: 与 B: 传统上保留给软驱。", Remedy = "从 D: 起选择。" });
        }

        // MBR 主分区槽位 + 2 TB 上限（分盘这一步要建的尺寸 = 源分区当前大小 - 保留值）
        AddMbrSlotCheck(layout, source.DiskNumber, blocks, Math.Max(0, ClampToLong(source.SizeBytes) - keepBytes));

        return blocks;
    }

    // ---------- 新建分区（用已有的未分配空间） ----------

    /// <summary>
    /// 「用未分配空间新建分区」的校验。
    ///
    /// 与分盘/合盘的根本区别：它**不动任何现有分区**，只在一段不属于任何分区的空间里建卷，
    /// 因此不需要白名单、相邻性、页面文件那一整套判断；但仍有几条底线：
    /// 磁盘必须可写且健康、起点必须能对齐、大小不能超过这段空间、盘符不能已被占用、
    /// MBR 磁盘主分区槽位要够。任何一条不满足都直接阻断。
    /// </summary>
    /// <param name="extent">选中的未分配空间（来自探测结果）。</param>
    /// <param name="sizeBytes">打算新建的分区大小（已对齐后的可用上限由 <see cref="FreeExtent.AlignedSizeBytes"/> 给出）。</param>
    public static IReadOnlyList<BlockReason> ValidateCreate(
        DiskProbeResult probe, FreeExtent? extent, long sizeBytes, char? newLetter, string volumeLabel)
    {
        var blocks = new List<BlockReason>();
        var layout = probe.Layout;

        if (!layout.IsElevated)
        {
            blocks.Add(new BlockReason
            {
                Title = "需要管理员权限",
                Detail = "磁盘与分区信息受系统 ACL 保护，非管理员无法读取或修改。",
                Remedy = "以管理员身份运行本程序。",
            });
            return blocks;
        }

        if (extent is null)
        {
            blocks.Add(new BlockReason
            {
                Title = "未选择未分配空间",
                Detail = "该磁盘上没有 ≥ 1 MB 的未分配空间，或尚未选择。",
                Remedy = "换一块磁盘；或先用「分盘」「合盘」腾出空间。",
            });
            return blocks;
        }

        var disk = layout.DiskByNumber(extent.DiskNumber);
        if (disk is null)
        {
            blocks.Add(new BlockReason
            {
                Title = "找不到所属磁盘",
                Detail = $"磁盘 {extent.DiskNumber} 未在探测结果中。",
                Remedy = "点「重新检测」刷新磁盘状态。",
            });
            return blocks;
        }

        AddDiskStateChecks(disk, blocks);

        if ((long)extent.StartBytes < FreeExtent.ReservedHeadBytes)
            blocks.Add(new BlockReason
            {
                Title = "这段空间位于磁盘开头的保留区",
                Detail = $"起点 {SizeFormatter.Format((long)extent.StartBytes)} 落在磁盘开头的 " +
                         $"{SizeFormatter.Format(FreeExtent.ReservedHeadBytes)} 保留区内" +
                         "（GPT 主分区表与对齐填充就在这里），用它建卷会破坏分区表。",
                Remedy = "选择更靠后的未分配空间。",
            });

        // 对齐后的可用量：Windows 要求分区起点按 1 MB 对齐
        long usable = (long)extent.AlignedSizeBytes;
        if (usable < FreeExtent.MinUsefulBytes)
        {
            blocks.Add(new BlockReason
            {
                Title = "这段未分配空间太小",
                Detail = $"{extent.DisplayName}；扣掉 1 MB 对齐后只剩 {SizeFormatter.Format(usable)}。" +
                         "Windows 无法在这里建立可用卷。",
                Remedy = "选择更大的未分配空间。",
            });
            return blocks;
        }

        if (sizeBytes <= 0)
        {
            blocks.Add(new BlockReason
            {
                Title = "未指定新分区大小",
                Detail = "需要给出要创建的分区大小。",
                Remedy = "拖动滑块或点「用满整段」。",
            });
        }
        else if (sizeBytes > usable)
        {
            blocks.Add(new BlockReason
            {
                Title = "分区大小超过可用空间",
                Detail = $"计划新建 {SizeFormatter.Format(sizeBytes)}，但这段空间对齐后只有 {SizeFormatter.Format(usable)} 可用。",
                Remedy = "把大小调到可用量以内（或点「用满整段」）。",
            });
        }
        else if (sizeBytes < 1024L * 1024)
        {
            blocks.Add(new BlockReason
            {
                Title = "分区太小",
                Detail = $"{SizeFormatter.Format(sizeBytes)} 不足 1 MB，Windows 无法建立可用卷。",
                Remedy = "至少给 1 GB。",
            });
        }
        else if (sizeBytes < Gb)
        {
            blocks.Add(new BlockReason
            {
                IsWarning = true,
                Title = "分区小于 1 GB",
                Detail = $"{SizeFormatter.Format(sizeBytes)} 的分区实际用处很小（文件系统本身要占一部分）。",
                Remedy = "除非确有此需求，建议至少 1 GB。",
            });
        }

        // 盘符
        if (newLetter is not { } letter)
        {
            blocks.Add(new BlockReason { Title = "未选择盘符", Detail = "新建分区需要一个未被占用的盘符。", Remedy = "在下拉框中选择一个盘符。" });
        }
        else
        {
            if (layout.UsedDriveLetters.Contains(letter))
                blocks.Add(new BlockReason
                {
                    Title = "盘符已被占用",
                    Detail = $"{letter}: 已被本机某个卷、网络映射或 subst 占用。",
                    Remedy = "换一个盘符。",
                });
            if (letter is 'A' or 'B')
                blocks.Add(new BlockReason { Title = "盘符不可用", Detail = "A: 与 B: 传统上保留给软驱。", Remedy = "从 D: 起选择。" });
        }

        // MBR 主分区槽位 + 2 TB 上限
        AddMbrSlotCheck(layout, extent.DiskNumber, blocks, sizeBytes);

        // 卷标：非法字符会被清洗，但要让用户知道
        string clean = PartitionPlanBuilder.SanitizeLabel(volumeLabel);
        if (volumeLabel.Trim().Length > 0 && !string.Equals(clean, volumeLabel.Trim(), StringComparison.Ordinal))
            blocks.Add(new BlockReason
            {
                IsWarning = true,
                Title = "卷标含非法字符",
                Detail = $"「{volumeLabel}」中的 \\ / : * ? \" < > | 会被去掉，实际卷标为「{clean}」。",
                Remedy = "如需特定卷标，改成只含字母数字与中文。",
            });

        // 提示（不阻断）
        if (disk.IsBoot || disk.IsSystem)
            blocks.Add(new BlockReason
            {
                IsWarning = true,
                Title = "这是系统盘",
                Detail = $"磁盘 {disk.Number}（{disk.DisplayName}）是启动/系统盘。本操作**只使用未分配空间**，" +
                         "不会改动 C: 与 EFI/恢复分区；但仍请确认你确实要在这块盘上新建分区。",
                Remedy = "无需处理，确认即可。",
            });

        if (((ulong)sizeBytes) < extent.SizeBytes)
            blocks.Add(new BlockReason
            {
                IsWarning = true,
                Title = "不会用满整段未分配空间",
                Detail = $"这段空间共 {extent.SizeText}，本次只用 {SizeFormatter.Format(sizeBytes)}，" +
                         $"建完后还会剩下约 {SizeFormatter.Format((long)extent.SizeBytes - sizeBytes)} 未分配。",
                Remedy = "点「用满整段」可以一次用完（推荐）。",
            });

        return blocks;
    }

    // ---------- 合盘 ----------

    /// <summary>
    /// 合盘可行性校验。
    ///
    /// 策略说明：合盘**不再由工具搬文件**——用户自行在工具外备份，工具只负责把后果说清楚并多要两次确认。
    /// 因此这里不做任何暂存/备份相关检查，但**保留全部"别把系统或自己弄坏"的底线**：
    /// 白名单、不许删系统盘、源分区不得含页面文件/休眠文件、源分区不得包含正在运行的本程序、
    /// 相邻性（目标在左、源在右、中间无其它分区）等。这些校验与用户确认是**并列**的必要条件。
    /// </summary>
    public static IReadOnlyList<BlockReason> ValidateMerge(
        DiskProbeResult probe, PartitionInfo source, PartitionInfo target, SupportedSize targetSupported)
    {
        var blocks = new List<BlockReason>();
        var layout = probe.Layout;

        CheckCommon(probe, source, layout, blocks, "删除", isSourceBeingDeleted: true);
        CheckCommon(probe, target, layout, blocks, "扩容");

        // 源分区绝不能是系统盘/启动盘或当前盘符
        if (source.IsBoot || source.IsSystem || source.DriveLetter == layout.SystemDriveLetter)
            blocks.Add(new BlockReason
            {
                Title = "不能删除系统所在分区",
                Detail = $"{(source.DriveLetter is { } c ? c + ":" : "该分区")} 是 Windows 所在分区，删除后系统将无法启动。",
                Remedy = "把「要合并掉的分区」改成数据分区（例如 D:），把「要变大的分区」选为 C:。",
            });

        // 源分区若装着正在运行的本程序，删除它会把程序自己删掉（执行到一半进程消失）
        AddSelfContainedCheck(source, layout, blocks);

        // 源分区若承载页面文件/休眠文件，删除会直接破坏系统
        AddPagingFileCheck(source, blocks);

        if (source.DiskNumber != target.DiskNumber)
        {
            blocks.Add(new BlockReason
            {
                Title = "两个分区不在同一块磁盘",
                Detail = "Windows 只能在同一块磁盘上把空间并入相邻分区，不能跨磁盘合并。",
                Remedy = "选择同一块磁盘上的分区，或分别调整。",
            });
        }
        else
        {
            // 关键几何条件：目标必须在源**左侧**，且两者之间没有任何其它分区。
            // 删除源之后，[目标.End, 源.End) 整段才成为"紧邻目标右侧的连续空闲空间"。
            if (source.OffsetBytes < target.EndBytes)
            {
                blocks.Add(new BlockReason
                {
                    Title = "分区顺序不支持直接合并",
                    Detail = $"要删除的分区（起始 {SizeFormatter.Format((long)source.OffsetBytes)}）位于目标分区" +
                             $"（{SizeFormatter.Format((long)target.OffsetBytes)}~{SizeFormatter.Format((long)target.EndBytes)}）的左侧。" +
                             "Windows 只能把紧邻分区右侧的空闲空间并入该分区，左侧的空闲空间无法并入。",
                    Remedy = "改选目标分区右侧的分区作为「要合并掉的分区」；若确实需要反向合并，" +
                             "只能使用支持移动分区的第三方工具（本工具不做，以免数据风险）。",
                });
            }
            else
            {
                var between = layout.PartitionsOfDisk(source.DiskNumber)
                    .Where(p => p.PartitionNumber != source.PartitionNumber &&
                                p.PartitionNumber != target.PartitionNumber &&
                                p.OffsetBytes >= target.EndBytes &&
                                p.EndBytes <= source.EndBytes)
                    .ToArray();
                if (between.Length > 0)
                {
                    blocks.Add(new BlockReason
                    {
                        Title = "中间还有其它分区挡路",
                        Detail = "目标分区与要删除的分区之间存在：" +
                                 string.Join("、", between.Select(p => $"{p.KindText}（{SizeFormatter.Format((long)p.SizeBytes)}）")) +
                                 "。删除后释放的空间不与目标分区相邻，无法并入。",
                        Remedy = "改选紧邻目标分区右侧的那个分区作为「要合并掉的分区」。",
                    });
                }
            }
        }

        if (!targetSupported.Ok)
        {
            // 仅提示、不阻断：删除源分区**之前**，目标分区右侧还没有空闲空间，
            // 因此 GetSupportedSize 的 SizeMax 必然等于当前大小——这不代表不能扩容。
            // 真正的扩容能力在删除源分区后由执行引擎重新实测确认。
            blocks.Add(new BlockReason
            {
                IsWarning = true,
                Title = "可扩展范围将在删除源分区后复核",
                Detail = DiskLayoutService.ExplainReturnCode(targetSupported.ReturnValue) +
                         " 删除源分区之前，目标分区右侧没有空闲空间，因此这一步的实测结果不具参考性。",
                Remedy = "无需处理：执行引擎会在删除源分区后重新实测，只有确认能扩容才会继续。",
            });
        }

        // 数据会永久消失这件事必须出现为"提示"，让用户在校验清单里也能看到（真正拦人的是确认弹窗）
        blocks.Add(new BlockReason
        {
            IsWarning = true,
            Title = "该分区内的全部文件将被永久删除",
            Detail = $"删除 {(source.DriveLetter is { } sl ? sl + ":" : "源分区")} 会移除它的分区表条目，" +
                     "其中的文件将无法访问（比格式化更彻底）。本工具不会替你备份任何文件。",
            Remedy = "点「删除并扩容…」时会先盘点并列出将被删除的文件数与主要目录，" +
                     "并再要求你勾选确认；请务必在此之前自行把还需要的东西复制到别处。",
        });

        return blocks;
    }

    /// <summary>源分区是否包含正在运行的本程序（删掉它等于把程序自己删了）。</summary>
    private static void AddSelfContainedCheck(PartitionInfo source, DiskLayoutSnapshot layout, List<BlockReason> blocks)
    {
        char? processLetter = DriveLetterOfProcess();
        if (processLetter is not { } pl || source.DriveLetter != pl) return;

        blocks.Add(new BlockReason
        {
            Title = "要删除的分区里正放着本程序",
            Detail = $"本程序当前从 {pl}: 运行，而它就是本次要删除的分区。" +
                     "删除后程序会在操作中途消失，后续步骤无法完成，磁盘可能停在中间状态。",
            Remedy = $"先把本程序（CleanC.exe）复制到别的分区（例如 {layout.SystemDriveLetter ?? 'C'}:）再从那里运行，然后重试。",
        });
    }

    /// <summary>源分区是否承载页面文件/休眠文件（删除会直接破坏系统）。</summary>
    private static void AddPagingFileCheck(PartitionInfo source, List<BlockReason> blocks)
    {
        if (source.DriveLetter is not { } letter) return;
        long paging = DiskLayoutService.MeasurePagingFiles(letter);
        if (paging <= 0) return;

        blocks.Add(new BlockReason
        {
            Title = "该分区承载页面文件或休眠文件",
            Detail = $"{letter}: 根目录下有 pagefile.sys / hiberfil.sys / swapfile.sys（合计 " +
                     $"{SizeFormatter.Format(paging)}）。这些是 Windows 运行所必需的文件，删除该分区会破坏系统。",
            Remedy = "先把页面文件移到别的盘（系统属性 → 高级 → 性能 → 虚拟内存），" +
                     "并关闭休眠（管理员执行 powercfg /h off），重启后再重试。",
        });
    }

    /// <summary>当前进程所在卷的盘符（判定失败返回 null）。internal 以便自检/断言复用同一判定。</summary>
    internal static char? DriveLetterOfProcess()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            string root = Path.GetPathRoot(exe) ?? string.Empty;
            if (root.Length >= 1 && char.IsLetter(root[0])) return char.ToUpperInvariant(root[0]);
        }
        catch (Exception) { /* 忽略 */ }
        return null;
    }

    // ---------- 通用检查 ----------

    /// <param name="isSourceBeingDeleted">
    /// 该分区是否是"将被删除的源分区"。为 true 时 BitLocker 只提示不阻断——
    /// 目标分区要被扩容，加密会干扰；源分区反正是删除，加密不影响。
    /// </param>
    private static void CheckCommon(DiskProbeResult probe, PartitionInfo p, DiskLayoutSnapshot layout,
        List<BlockReason> blocks, string verb, bool isSourceBeingDeleted = false)
    {
        if (!layout.IsElevated)
        {
            blocks.Add(new BlockReason
            {
                Title = "需要管理员权限",
                Detail = "磁盘与分区信息受系统 ACL 保护，非管理员无法读取或修改。",
                Remedy = "以管理员身份运行本程序（在磁盘分区窗口里点「以管理员身份重新启动」）。",
            });
            return;   // 无布局数据，后续检查无意义
        }

        if (!p.IsOperable)
        {
            blocks.Add(new BlockReason
            {
                Title = $"该分区类型不能{verb}",
                Detail = $"磁盘 {p.DiskNumber} 分区 {p.PartitionNumber} 是「{p.KindText}」，不属于基本数据分区。" +
                         "系统分区、保留分区与恢复分区一旦被动会直接影响开机。",
                Remedy = "改选类型为「基本数据」的分区。",
            });
            return;
        }

        var disk = layout.DiskByNumber(p.DiskNumber);
        if (disk is null)
        {
            blocks.Add(new BlockReason { Title = "找不到所属磁盘", Detail = $"磁盘 {p.DiskNumber} 未在探测结果中。", Remedy = "点「重新检测」刷新磁盘状态。" });
            return;
        }

        AddDiskStateChecks(disk, blocks);

        var vol = p.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
        if (vol is null)
        {
            blocks.Add(new BlockReason
            {
                Title = "该分区没有可识别的卷",
                Detail = "调整分区需要卷信息（文件系统与容量）。无盘符或未格式化的分区无法调整。",
                Remedy = "先在磁盘管理中为该分区分配盘符并确认文件系统为 NTFS。",
            });
            return;
        }

        if (!vol.IsNtfs)
            blocks.Add(new BlockReason
            {
                Title = "文件系统不受支持",
                Detail = $"Windows 只能调整 NTFS（或未格式化）分区，当前是 {vol.FileSystem}。",
                Remedy = "exFAT/FAT32/ReFS 需要先备份数据再重新格式化为 NTFS。",
            });

        if (vol.Health is HealthState.Warning or HealthState.Unhealthy)
            blocks.Add(new BlockReason
            {
                Title = "卷存在错误，无法调整",
                Detail = $"卷 {vol.DisplayName} 健康状态：{vol.HealthText}" +
                         (vol.HealthRaw.Length > 0 && vol.HealthRaw != vol.HealthText ? $"（Windows 返回 {vol.HealthRaw}）" : string.Empty) +
                         "。这正是系统返回码 42008「卷存在错误，无法收缩」的来源；带错调整可能损坏文件系统。",
                Remedy = $"以管理员身份运行 chkdsk {p.DriveLetter}: /scan 修复后再试（如需修复实际错误用 /f）。",
            });

        if (p.DriveLetter is { } bl)
        {
            var blState = probe.Environment.BitLockerOf(bl);
            if (blState?.Protection == BitLockerProtection.On)
            {
                if (isSourceBeingDeleted)
                {
                    // 要被删除的分区加密与否不影响删除（反正是整体移除），只提示
                    blocks.Add(new BlockReason
                    {
                        IsWarning = true,
                        Title = "该分区已启用 BitLocker",
                        Detail = $"{bl}: 已加密且保护开启。删除分区不受加密影响，" +
                                 "但其中的文件删除后**即使有恢复密钥也无法恢复**——备份时请确认能正常解密打开。",
                        Remedy = "无需处理；只需确保你复制出去的副本可以正常打开。",
                    });
                }
                else
                {
                    blocks.Add(new BlockReason
                    {
                        Title = "BitLocker 保护处于开启状态",
                        Detail = $"{bl}: 已加密且保护开启。调整分区前后需要挂起/恢复保护，否则可能失败。",
                        Remedy = $"先执行 Suspend-BitLocker -MountPoint \"{bl}:\" -RebootCount 1 挂起保护，操作完成后再 Resume-BitLocker。",
                    });
                }
            }
        }
    }

    /// <summary>
    /// 磁盘级别的"能不能动"检查（动态磁盘 / 离线 / 只读 / 健康 / 未初始化）。
    /// 分盘、合盘、新建分区三条路径**共用**这一段，避免各写一份而慢慢走样。
    /// </summary>
    private static void AddDiskStateChecks(DiskInfo disk, List<BlockReason> blocks)
    {
        if (disk.HasLdmPartitions)
            blocks.Add(new BlockReason
            {
                Title = "动态磁盘不受支持",
                Detail = "该磁盘上存在 LDM 分区（动态磁盘）。动态磁盘的扩容规则与基本磁盘不同，Windows 11 已弃用该特性。",
                Remedy = "本工具不做动态磁盘转换；如需处理请在磁盘管理中手动操作或使用专业工具。",
            });

        if (disk.IsOffline)
            blocks.Add(new BlockReason { Title = "磁盘处于离线状态", Detail = $"磁盘 {disk.Number} 当前离线。", Remedy = "先在磁盘管理中把该磁盘设为「联机」。" });

        if (disk.IsReadOnly)
            blocks.Add(new BlockReason { Title = "磁盘为只读", Detail = $"磁盘 {disk.Number} 被标记为只读。", Remedy = "检查写保护开关/策略后重试。" });

        if (disk.Health is HealthState.Warning or HealthState.Unhealthy)
            blocks.Add(new BlockReason
            {
                Title = "磁盘健康状态异常",
                Detail = $"磁盘 {disk.Number} 健康状态：{disk.HealthText}" +
                         (disk.HealthRaw.Length > 0 && disk.HealthRaw != disk.HealthText ? $"（Windows 返回 {disk.HealthRaw}）" : string.Empty) +
                         "。健康异常的磁盘上调整分区，可能直接造成数据损坏。",
                Remedy = "先备份数据并检查磁盘硬件（可用厂商工具或 CrystalDiskInfo 看 SMART），确认健康后再操作。",
            });

        if (disk.Style == PartitionStyle.Raw)
            blocks.Add(new BlockReason { Title = "磁盘未初始化", Detail = "RAW 磁盘没有分区表。", Remedy = "先用磁盘管理初始化磁盘（会清除数据）。" });
    }

    /// <summary>
    /// MBR 磁盘的两条硬限制：① 最多 4 个主分区；② 单个分区不能超过 2 TB
    /// （MBR 用 32 位扇区数寻址）。不同电脑上这两条都会真的撞上，所以在这里一并阻断。
    /// </summary>
    private static void AddMbrSlotCheck(DiskLayoutSnapshot layout, int diskNumber, List<BlockReason> blocks,
        long newPartitionBytes = 0)
    {
        var disk = layout.DiskByNumber(diskNumber);
        if (disk?.Style != PartitionStyle.Mbr) return;

        var parts = layout.PartitionsOfDisk(diskNumber);
        // MBR 扩展分区（0x05 / 0x0F）不占用主分区槽位
        int primary = parts.Count(p => p.MbrType is not (5 or 0x0F));
        bool hasExtended = parts.Any(p => p.MbrType is 5 or 0x0F);
        if (primary >= 4 && !hasExtended)
            blocks.Add(new BlockReason
            {
                Title = "MBR 磁盘主分区数量已满",
                Detail = $"这是 MBR 分区表磁盘，最多只能有 4 个主分区，当前已有 {primary} 个，无法再新建分区。",
                Remedy = "需要先把磁盘转换为 GPT（有数据风险，本工具不做），或删除一个不再使用的分区后再分盘。",
            });

        const long mbrMaxBytes = 2L * 1024 * 1024 * 1024 * 1024;   // 2 TB
        if (newPartitionBytes > mbrMaxBytes)
            blocks.Add(new BlockReason
            {
                Title = "MBR 磁盘上的分区不能超过 2 TB",
                Detail = $"计划新建 {SizeFormatter.Format(newPartitionBytes)}，而 MBR 分区表用 32 位扇区数寻址，" +
                         "单个分区最大只能是 2 TB（Windows 会直接报『参数错误 / 容量不足』）。",
                Remedy = "把要新建的分区调到 2 TB 以内，或先把磁盘转换为 GPT（有数据风险，本工具不做）。",
            });
    }

    // ---------- 工具 ----------

    /// <summary>取路径所在盘符（如 "D:\\stage" → 'D'）；非盘符路径返回 null。</summary>
    public static char? DriveLetterOf(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            if (root.Length >= 1 && char.IsLetter(root[0])) return char.ToUpperInvariant(root[0]);
        }
        catch (Exception) { /* 路径非法 */ }
        return null;
    }

    /// <summary>取路径所在卷的可用空间；无法判定返回 -1。</summary>
    public static long AvailableBytesAt(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // 目录可能尚不存在：逐级向上找到第一个存在的祖先
            while (!string.IsNullOrEmpty(full) && !Directory.Exists(full))
            {
                string? parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent) || parent == full) break;
                full = parent;
            }
            string root = Path.GetPathRoot(full) ?? string.Empty;
            if (root.Length == 0) return -1;
            var di = new DriveInfo(root);
            return di.IsReady ? di.AvailableFreeSpace : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static long ClampToLong(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;
}
