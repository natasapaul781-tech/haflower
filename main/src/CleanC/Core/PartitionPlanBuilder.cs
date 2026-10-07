using System.Text;

namespace CleanC.Core;

/// <summary>
/// 分区计划构建器：把「源分区 + 目标容量」变成一份**可读、可打印、可照做**的分步计划，
/// 每一步都带等价的 PowerShell 命令（含执行前的身份/尺寸复验语句）。
///
/// 本类纯计算，不执行任何操作。
/// </summary>
public static class PartitionPlanBuilder
{

    /// <summary>默认卷标（用户可在 UI 中修改）。</summary>
    public const string DefaultNewVolumeLabel = "新加卷";

    /// <summary>「未分配空间 → 新建分区」页的默认卷标。</summary>
    public const string DefaultCreateVolumeLabel = "数据盘";

    // ---------- 分盘 ----------

    public static PartitionPlan BuildSplit(DiskProbeResult probe, PartitionInfo source, SupportedSize supported,
        long keepBytes, char newLetter, string newVolumeLabel, bool dryRun)
    {
        var layout = probe.Layout;
        var disk = layout.DiskByNumber(source.DiskNumber);
        long sourceBefore = (long)source.SizeBytes;
        long splitBytes = sourceBefore - keepBytes;
        var identity = PartitionIdentity.Of(source);

        // 收缩之后能真正建出多大的分区：不能直接用"算术差值"。
        // Windows 建分区要求起点与终点都落在 1 MB 边界上（否则报 Not enough available capacity），
        // 而且磁盘末尾的 GPT 备份分区表所在区域不能用。这里按几何算出实际可用量，
        // 执行时还会在收缩完成后**再实测一次**（以实测为准）。
        var (freedStart, freedEnd) = FreedRegionAfterShrink(layout, source, keepBytes);
        long creatable = CreatableSize(freedStart, freedEnd);
        long planned = Math.Min(splitBytes, creatable);
        planned = (long)DiskLayoutSnapshot.AlignDown((ulong)Math.Max(0, planned));
        bool shortfall = planned < splitBytes;

        string locate =
            $"$p = Get-Partition -DiskNumber {source.DiskNumber} -PartitionNumber {source.PartitionNumber} -ErrorAction Stop";

        var steps = new List<PartitionStep>
        {
            new()
            {
                Index = 1,
                Title = $"收缩 {source.LetterText} 到 {SizeFormatter.Format(keepBytes)}",
                Action = $"把 {source.LetterText} 从 {SizeFormatter.Format(sourceBefore)} 缩小到 {SizeFormatter.Format(keepBytes)}，" +
                         $"释放出 {SizeFormatter.Format(splitBytes)} 变成「未分配」空间。" +
                         "系统会自动把可移动的文件挪到分区前部，**普通文件不会丢失**。",
                Expected = $"完成后 {source.LetterText} 显示为 {SizeFormatter.Format(keepBytes)}，" +
                           "磁盘管理里能看到紧随其后的「未分配」区域。",
                OnFailure = "若提示「可压缩空间不足」：数据不会丢，只是没能缩小。回到工具按「为什么分不出空间」的补救清单处理后重新分析。" +
                            "若提示 chkdsk 相关错误：先运行 chkdsk 修复卷错误再试。",
                PowerShell =
                    $"{locate}\n" +
                    $"if ([uint64]$p.Offset -ne [uint64]{source.OffsetBytes}) {{ throw '分区位置已变化，请重新分析' }}\n" +
                    $"if ([uint64]$p.Size -ne [uint64]{source.SizeBytes}) {{ throw '分区大小已变化，请重新分析' }}\n" +
                    $"Resize-Partition -DiskNumber {source.DiskNumber} -PartitionNumber {source.PartitionNumber} -Size {keepBytes}",
                IsDestructive = false,
                IsInterruptible = false,
            },
            new()
            {
                Index = 2,
                Title = $"在未分配空间新建 {newLetter}:（{SizeFormatter.Format(planned)}）",
                Action = $"在刚释放出的未分配空间上创建一个 {SizeFormatter.Format(planned)} 的新分区，并分配盘符 {newLetter}:。" +
                         (shortfall
                             ? $"（可建尺寸按 1 MB 对齐计算：理论释放量 {SizeFormatter.Format(splitBytes)}，" +
                               $"实际可建 {SizeFormatter.Format(planned)}，差 {SizeFormatter.Format(splitBytes - planned)}）"
                             : string.Empty),
                Expected = $"命令返回新分区的 Guid / Offset / Size / PartitionNumber，请记下它们（下一步要用）。",
                OnFailure = "若提示空间不足：说明释放量小于预期，重新执行第 1 步并放宽收缩量。" +
                            "若提示盘符被占用：换一个盘符后重做第 2、3 步（第 1 步不要重复做）。",
                // 尺寸必须是 1 MB 的整数倍，否则 Windows 对齐补足后会报 Not enough available capacity
                PowerShell =
                    $"$np = New-Partition -DiskNumber {source.DiskNumber} -Size {planned} -DriveLetter {newLetter}\n" +
                    "$np | Select-Object Guid, Offset, Size, PartitionNumber, DriveLetter | Format-List",
                IsDestructive = false,
                IsInterruptible = true,
            },
            new()
            {
                Index = 3,
                Title = $"把 {newLetter}: 格式化为 NTFS",
                Action = $"把**刚创建的那个**分区格式化为 NTFS，卷标「{newVolumeLabel}」。全新分区格式化不会影响任何已有数据。",
                Expected = $"{newLetter}: 出现在资源管理器中，可用空间约为 {SizeFormatter.Format(planned)}。",
                OnFailure = "格式化只会作用于 Guid 与第 2 步返回值一致的分区；若身份不符脚本会主动中止——此时**不要**手工换盘符继续，" +
                            "先核对磁盘管理里的分区布局。",
                // 关键安全点：只格式化 Guid 与新建结果一致的分区，绝不按盘符操作。
                // 注意：这里不能写 <第2步返回的…> 这种尖括号占位符——「<」在 PowerShell 里是保留的重定向运算符，
                // 直接粘贴会报语法错误。改为"先赋值两个变量再校验"，整段是合法脚本，用户只需改两行。
                PowerShell =
                    "# 先把第 2 步返回的两个值填到下面两行（其余不用改）\n" +
                    "$newPartitionNumber = 5                     # ← 第 2 步返回的 PartitionNumber\n" +
                    "$newGuid             = '{第2步返回的 Guid}'   # ← 第 2 步返回的 Guid\n" +
                    $"$np = Get-Partition -DiskNumber {source.DiskNumber} -PartitionNumber $newPartitionNumber -ErrorAction Stop\n" +
                    "if ([string]$np.Guid -ne $newGuid) { throw '分区身份不符，拒绝格式化' }\n" +
                    $"Format-Volume -Partition $np -FileSystem NTFS -NewFileSystemLabel '{SanitizeLabel(newVolumeLabel)}' -Confirm:$false",
                IsDestructive = false,
                IsInterruptible = true,
            },
        };

        return new PartitionPlan
        {
            Kind = PartitionOpKind.Split,
            Title = $"分盘：把 {source.LetterText} 分出 {SizeFormatter.Format(planned)} 作为新的 {newLetter}:",
            DiskNumber = source.DiskNumber,
            DiskStyle = disk?.Style ?? PartitionStyle.Unknown,
            DiskName = disk?.DisplayName ?? $"磁盘 {source.DiskNumber}",
            Source = identity,
            Target = null,
            NewDriveLetter = newLetter,
            NewVolumeLabel = newVolumeLabel,
            SourceSizeBeforeBytes = sourceBefore,
            SourceSizeAfterBytes = keepBytes,
            FreedBytes = planned,
            Note = shortfall
                ? $"释放出的空间是 {SizeFormatter.Format(splitBytes)}，但分区起点与尺寸都要按 1 MB 对齐，" +
                  $"实际可建 {SizeFormatter.Format(planned)}（少 {SizeFormatter.Format(splitBytes - planned)}）——不影响使用。"
                : string.Empty,
            IsDryRun = dryRun,
            Steps = steps,
        };
    }

    // ---------- 新建分区（用已有的未分配空间） ----------

    /// <summary>
    /// 在磁盘上已有的未分配空间里新建一个分区并格式化。
    ///
    /// 与分盘的区别：**不收缩任何分区**——用的就是磁盘上已经空着的那段空间。
    /// 因此它也是「收缩成功、新建失败」这类半完成状态的修复路径。
    /// </summary>
    /// <param name="extent">选中的未分配空间（起点会按 1 MB 向上对齐，Windows 不接受未对齐的偏移）。</param>
    public static PartitionPlan BuildCreate(DiskProbeResult probe, FreeExtent extent, long sizeBytes,
        char newLetter, string newVolumeLabel, bool dryRun)
    {
        var layout = probe.Layout;
        var disk = layout.DiskByNumber(extent.DiskNumber);
        long start = (long)extent.AlignedStartBytes;

        // 尺寸必须落在 1 MB 边界上、且不超过这段空间能真正建出来的量
        // （否则 Windows 对齐补足后会报 Not enough available capacity）。
        long maxCreatable = (long)extent.AlignedSizeBytes;
        if (sizeBytes <= 0 || sizeBytes > maxCreatable) sizeBytes = maxCreatable;
        sizeBytes = (long)DiskLayoutSnapshot.AlignDown((ulong)Math.Max(0, sizeBytes));

        long tailLeft = (long)extent.SizeBytes - sizeBytes;   // 建完之后这段空间还会剩多少

        var steps = new List<PartitionStep>
        {
            new()
            {
                Index = 1,
                Title = $"在未分配空间新建 {newLetter}:（{SizeFormatter.Format(sizeBytes)}）",
                Action = $"在磁盘 {extent.DiskNumber} 的未分配空间上创建 {SizeFormatter.Format(sizeBytes)} 的分区，" +
                         $"起始位置 {SizeFormatter.Format(start)}（与计划核对过的位置一致），并分配盘符 {newLetter}:。" +
                         "**不触碰任何现有分区**。",
                Expected = $"命令返回新分区的 Guid / Offset / Size / PartitionNumber；Offset 应等于 {start}。",
                OnFailure = "若提示位置/大小非法：说明这段空间已被其它程序改动，重新点「重新检测」。",
                PowerShell =
                    $"$np = New-Partition -DiskNumber {extent.DiskNumber} -Offset {start} -Size {sizeBytes} -DriveLetter {newLetter}\n" +
                    "$np | Select-Object Guid, Offset, Size, PartitionNumber, DriveLetter | Format-List",
                IsDestructive = false,
                IsInterruptible = false,   // 改分区表，发出后不打断
            },
            new()
            {
                Index = 2,
                Title = $"把 {newLetter}: 格式化为 NTFS",
                Action = $"把**刚创建的那个**分区格式化为 NTFS，卷标「{newVolumeLabel}」。全新分区里没有数据，格式化不影响任何已有文件。",
                Expected = $"{newLetter}: 出现在资源管理器中，可用空间约为 {SizeFormatter.Format(sizeBytes)}。",
                OnFailure = "格式化只作用于 Guid 与第 1 步返回值一致的分区；身份不符脚本会主动中止——" +
                            "此时不要手工换盘符继续，先核对磁盘管理里的布局。",
                PowerShell =
                    "# 先把第 1 步返回的两个值填到下面两行（其余不用改）\n" +
                    "$newPartitionNumber = 5                     # ← 第 1 步返回的 PartitionNumber\n" +
                    "$newGuid             = '{第1步返回的 Guid}'   # ← 第 1 步返回的 Guid\n" +
                    $"$np = Get-Partition -DiskNumber {extent.DiskNumber} -PartitionNumber $newPartitionNumber -ErrorAction Stop\n" +
                    "if ([string]$np.Guid -ne $newGuid) { throw '分区身份不符，拒绝格式化' }\n" +
                    $"Format-Volume -Partition $np -FileSystem NTFS -NewFileSystemLabel '{SanitizeLabel(newVolumeLabel)}' -Confirm:$false",
                IsDestructive = false,
                IsInterruptible = true,
            },
        };

        return new PartitionPlan
        {
            Kind = PartitionOpKind.Create,
            Title = $"新建分区：用磁盘 {extent.DiskNumber} 的未分配空间创建 {newLetter}:（{SizeFormatter.Format(sizeBytes)}）",
            DiskNumber = extent.DiskNumber,
            DiskStyle = disk?.Style ?? PartitionStyle.Unknown,
            DiskName = disk?.DisplayName ?? $"磁盘 {extent.DiskNumber}",
            Source = default,
            Target = null,
            NewDriveLetter = newLetter,
            NewVolumeLabel = newVolumeLabel,
            NewPartitionStartBytes = start,
            FreedBytes = sizeBytes,
            Note = tailLeft > 0
                ? $"这段空间共 {SizeFormatter.Format((long)extent.SizeBytes)}，本次用 {SizeFormatter.Format(sizeBytes)}，" +
                  $"建完后还会剩下约 {SizeFormatter.Format(tailLeft)} 未分配（含 1 MB 对齐与磁盘末尾保留区）。"
                : string.Empty,
            IsDryRun = dryRun,
            Steps = steps,
        };
    }

    // ---------- 几何：收缩后能真正建出多大的分区 ----------

    /// <summary>
    /// 收缩到 <paramref name="keepBytes"/> 之后，释放出来的空间在磁盘上的范围 [start, end)。
    ///
    /// end 取"源分区之后第一个分区的起点"；若源分区之后没有别的分区，则取**磁盘末尾减去末尾保留区**
    /// （末尾 1 MB 是 GPT 备份分区表所在，不能用）。
    /// </summary>
    public static (long Start, long End) FreedRegionAfterShrink(
        DiskLayoutSnapshot layout, PartitionInfo source, long keepBytes)
    {
        long start = (long)source.OffsetBytes + keepBytes;

        var next = layout.PartitionsOfDisk(source.DiskNumber)
            .Where(p => p.PartitionNumber != source.PartitionNumber && p.OffsetBytes >= source.EndBytes)
            .OrderBy(p => p.OffsetBytes)
            .FirstOrDefault();

        long end;
        if (next is not null)
        {
            end = (long)next.OffsetBytes;
        }
        else
        {
            var disk = layout.DiskByNumber(source.DiskNumber);
            end = disk is null ? start : (long)disk.SizeBytes - FreeExtent.ReservedTailBytes;
        }

        return (start, end);
    }

    /// <summary>
    /// 这段空间**能真正建出来**的最大分区尺寸：
    /// 起点向上取整到 1 MB、尺寸向下取整到 1 MB（Windows 会对齐补足，给"除不尽"的尺寸会直接报
    /// Not enough available capacity）。小于 1 MB 时返回 0。
    /// </summary>
    public static long CreatableSize(long regionStart, long regionEnd)
    {
        if (regionEnd <= regionStart) return 0;
        long start = (long)DiskLayoutSnapshot.AlignUp((ulong)regionStart);
        if (regionEnd <= start) return 0;
        return (long)DiskLayoutSnapshot.AlignDown((ulong)(regionEnd - start));
    }

    // ---------- 合盘 ----------
    /// <param name="extendableBytes">
    /// 删除源分区后可并入目标分区的字节数 = 源分区结束位置 − 目标分区结束位置
    /// （由 <see cref="PartitionPlanValidator.ValidateMerge"/> 保证两者之间没有其它分区）。
    /// </param>
    /// <param name="impact">
    /// 删除前对源分区内容的**只读**盘点结果（可为 null：未盘点时说明书会写"未盘点"）。
    /// 工具不替用户备份，因此这个数字必须留档——删完之后它就是唯一的线索。
    /// </param>
    public static PartitionPlan BuildMerge(DiskProbeResult probe, PartitionInfo source, PartitionInfo target,
        long extendableBytes, bool dryRun, PartitionImpact? impact = null)
    {
        var layout = probe.Layout;
        var disk = layout.DiskByNumber(source.DiskNumber);
        long targetBefore = (long)target.SizeBytes;
        long sourceSize = (long)source.SizeBytes;
        long targetAfter = targetBefore + extendableBytes;
        string src = source.LetterText;
        string dst = target.LetterText;
        var identity = PartitionIdentity.Of(source);
        var targetIdentity = PartitionIdentity.Of(target);

        string locate =
            $"$p = Get-Partition -DiskNumber {source.DiskNumber} -PartitionNumber {source.PartitionNumber} -ErrorAction Stop";

        var steps = new List<PartitionStep>
        {
            new()
            {
                Index = 1,
                Title = $"删除分区 {src}（{SizeFormatter.Format(sourceSize)}）",
                Action = $"永久删除 {src} 这个分区" +
                         (impact is null
                             ? "。"
                             : $"——其中的 {impact.ScaleText} 将无法访问（分区表条目被移除，比格式化更彻底）。") +
                         "本工具不会替你备份任何文件；删除后 Windows 没有撤销，数据恢复软件也不保证成功。",
                Expected = $"{src} 从资源管理器消失，该位置变成 {SizeFormatter.Format(extendableBytes)} 的「未分配」空间。",
                OnFailure = "若提示分区被占用：关掉正在使用该分区文件的程序（下载工具、虚拟机、云同步、杀毒扫描）后重试。" +
                            "若中途断电：目标分区数据完好，磁盘上只剩一段「未分配」空间，重新执行第 2 步即可。",
                PowerShell =
                    $"{locate}\n" +
                    $"if ([string]$p.Guid -ne '{source.Guid}') {{ throw '源分区身份不符，已中止' }}\n" +
                    $"if ([uint64]$p.Offset -ne [uint64]{source.OffsetBytes} -or [uint64]$p.Size -ne [uint64]{source.SizeBytes}) {{ throw '源分区已变化，请重新分析' }}\n" +
                    $"Remove-Partition -DiskNumber {source.DiskNumber} -PartitionNumber {source.PartitionNumber} -Confirm:$false",
                IsDestructive = true,
                IsInterruptible = false,
            },
            new()
            {
                Index = 2,
                Title = $"把 {SizeFormatter.Format(extendableBytes)} 并入 {dst}",
                Action = $"先实测 {dst} 紧邻右侧的连续空闲空间，确认不少于 {SizeFormatter.Format(extendableBytes)} 之后，" +
                         "再把整段空间并入该分区。多测一步是为了避免出现「删了分区却扩不了容」的不可逆损失。",
                Expected = $"{dst} 变为约 {SizeFormatter.Format(targetAfter)}；资源管理器里可看到容量增加。",
                OnFailure = "若报「尺寸不受支持」：用下面注释里的实测最大值重试（把整段空闲空间全部并入）。" +
                            "若中途断电：目标分区数据完好，重新执行本步即可。",
                PowerShell =
                    $"$t = Get-Partition -DiskNumber {target.DiskNumber} -PartitionNumber {target.PartitionNumber} -ErrorAction Stop\n" +
                    $"if ([string]$t.Guid -ne '{target.Guid}' -or [uint64]$t.Offset -ne [uint64]{target.OffsetBytes}) {{ throw '目标分区身份不符，已中止（源分区已删除，请手工扩容）' }}\n" +
                    "$s = Invoke-CimMethod -InputObject $t -MethodName GetSupportedSize\n" +
                    "$free = [uint64]$s.SizeMax - [uint64]$t.Size\n" +
                    $"if ($free -lt [uint64]{extendableBytes}) {{ throw \"紧邻空闲空间只有 $free 字节，少于预期，已中止扩容\" }}\n" +
                    "Resize-Partition -DiskNumber " + target.DiskNumber + " -PartitionNumber " + target.PartitionNumber + " -Size ([uint64]$t.Size + $free)",
                IsDestructive = false,
                IsInterruptible = false,
            },
        };

        return new PartitionPlan
        {
            Kind = PartitionOpKind.Merge,
            Title = $"合盘：删除 {src}（{SizeFormatter.Format(sourceSize)}）并把空间并入 {dst}" +
                    $"（{SizeFormatter.Format(targetBefore)} → {SizeFormatter.Format(targetAfter)}）",
            DiskNumber = source.DiskNumber,
            DiskStyle = disk?.Style ?? PartitionStyle.Unknown,
            DiskName = disk?.DisplayName ?? $"磁盘 {source.DiskNumber}",
            Source = identity,
            Target = targetIdentity,
            Impact = impact,
            SourceSizeBeforeBytes = sourceSize,
            SourceSizeAfterBytes = 0,
            FreedBytes = extendableBytes,
            TargetSizeBeforeBytes = targetBefore,
            TargetSizeAfterBytes = targetAfter,
            IsDryRun = dryRun,
            Steps = steps,
        };
    }


    /// <summary>卷标清洗：去掉卷标不允许的字符与引号，避免生成无法执行的命令。</summary>
    public static string SanitizeLabel(string label)
    {
        var sb = new StringBuilder();
        foreach (char c in (label ?? string.Empty).Trim())
        {
            if (char.IsControl(c)) continue;
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\'') continue;
            sb.Append(c);
        }
        string result = sb.ToString().Trim();
        if (result.Length > 32) result = result[..32];
        return result.Length == 0 ? DefaultNewVolumeLabel : result;
    }
}
