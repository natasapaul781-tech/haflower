using System.Text;

namespace CleanC.Core;

/// <summary>一条补救措施。</summary>
public sealed class ShrinkRemedy
{
    public required int Index { get; init; }
    public required string Title { get; init; }

    /// <summary>白话说明：这一步在做什么、为什么有用。</summary>
    public required string Detail { get; init; }

    /// <summary>需要执行的命令（可为空，表示在图形界面里点几下即可）。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>是否可逆（“可逆 / 不可逆”，写给用户看）。</summary>
    public string Reversibility { get; init; } = "可逆";

    /// <summary>是否带有不可忽视的副作用。</summary>
    public bool HasSideEffect { get; init; }

    /// <summary>本机是否可以执行（如“未启用休眠”时这一条就没意义）。</summary>
    public bool Applicable { get; init; } = true;

    /// <summary>本机的实测收益描述（如“约 12.7 GB”）。</summary>
    public string Benefit { get; init; } = string.Empty;
}

/// <summary>
/// 「为什么分不出空间」补救清单生成器。
///
/// 背景（Windows 的硬事实）：收缩分区时系统会把**可移动**文件挪到分区前部，
/// 但页面文件、卷影副本存储、文件系统/存储驱动元数据、坏簇重映射等属于**不可移动文件**，
/// 它们挡在哪里，分区就最多只能缩到哪里（见本工具 README 的引用来源）。
/// 本类把“还能做什么”按**收益/风险**排好序，并带上本机实测数字。
/// </summary>
public static class ShrinkBlockerAdvisor
{
    /// <summary>生成本机相关的补救清单（只读；不执行任何命令）。</summary>
    public static IReadOnlyList<ShrinkRemedy> Build(DiskProbeResult probe, PartitionInfo source, SupportedSize? supported)
    {
        var env = probe.Environment;
        var layout = probe.Layout;
        var vol = source.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
        string letter = source.DriveLetter is { } dl ? dl.ToString() : "C";
        long paging = source.DriveLetter is { } pl ? DiskLayoutService.MeasurePagingFiles(pl) : 0;
        long hiber = env.HiberFileBytes;
        long canSplit = supported is { Ok: true } s ? (long)s.MaxShrinkableBytes(source.SizeBytes) : -1;
        long free = vol is null ? 0 : (long)vol.FreeBytes;

        var list = new List<ShrinkRemedy>();

        list.Add(new ShrinkRemedy
        {
            Index = 1,
            Title = $"关闭休眠（省下约 {SizeFormatter.Format(hiber)}）",
            Detail = "休眠会在系统盘根目录生成一个与内存大小相当的 hiberfil.sys，它是不可移动文件。" +
                     "关闭休眠后该文件会被删除，通常能立刻让可压缩空间明显变大。建议操作后**重启一次**再重新分析。",
            Command = "powercfg /h off",
            Reversibility = "可逆（powercfg /h on 可恢复）",
            HasSideEffect = true,
            Applicable = env.HibernateEnabled,
            Benefit = hiber > 0 ? $"实测可释放约 {SizeFormatter.Format(hiber)}" : "未实测到 hiberfil.sys",
        });

        list.Add(new ShrinkRemedy
        {
            Index = 2,
            Title = $"把页面文件移到别的盘或调小（当前占 {SizeFormatter.Format(paging)}）",
            Detail = "页面文件（pagefile.sys）也是不可移动文件。把它移到其它盘、或改成较小的固定大小，" +
                     "本卷的可压缩空间会变大。操作路径：此电脑右键 → 属性 → 高级系统设置 → 高级 → 性能「设置」" +
                     " → 高级 → 虚拟内存「更改」。内存较小的机器不建议完全关闭页面文件。",
            Command = string.Empty,
            Reversibility = "可逆（改回“由系统管理”即可）",
            HasSideEffect = true,
            Applicable = paging > 0,
            Benefit = paging > 0 ? $"本卷根目录实测 {SizeFormatter.Format(paging)}" : "未实测到页面文件",
        });

        list.Add(new ShrinkRemedy
        {
            Index = 3,
            Title = "整理碎片 / 合并自由空间",
            Detail = "让可移动文件集中到分区前部，从而把分区分区尾部的空闲空间最大化。" +
                     "SSD 上这一步是安全的（只搬文件、不做机械寻道优化），但可能耗时较长。" +
                     "**效果因机而异，不保证一定变大**；这是最安全的一条，建议先试它。",
            Command = $"defrag {letter}: /X /V",
            Reversibility = "完全无副作用",
            HasSideEffect = false,
            Applicable = true,
            Benefit = "合并自由空间后通常能多分出一些",
        });

        list.Add(new ShrinkRemedy
        {
            Index = 4,
            Title = "先清理本卷再分盘",
            Detail = "本工具主界面已有清理功能：清理临时文件、各类开发缓存（npm/pip/NuGet/Conda/HuggingFace…）、" +
                     "浏览器与软件缓存等。已用空间变小后，建议保留值也随之变小，能分出的比例更合理。" +
                     "另外「占用分析」可以逐层下钻找出到底是谁占了空间。",
            Command = string.Empty,
            Reversibility = "会删除缓存（下次使用会重新生成/下载）",
            HasSideEffect = true,
            Applicable = true,
            Benefit = free < (long)(vol?.SizeBytes ?? 0) / 5
                ? $"本卷当前仅剩 {SizeFormatter.Format(free)}，建议先清理"
                : string.Empty,
        });

        list.Add(new ShrinkRemedy
        {
            Index = 5,
            Title = "关闭系统保护并删除还原点（风险最高，放最后）",
            Detail = "卷影副本（系统还原点、文件历史）的存储区同样不可移动，往往就是“可用压缩空间为 0”的真凶。" +
                     "删除后**还原点会永久消失**，无法恢复，因此请先确认你不需要用它回滚系统。",
            Command = $"vssadmin delete shadows /for={letter}: /all",
            Reversibility = "不可逆（还原点会丢失）",
            HasSideEffect = true,
            Applicable = env.SystemRestoreEnabled || (env.VssKnown && env.VssUsedBytes > 0),
            Benefit = env.VssKnown && env.VssUsedBytes > 0 ? $"实测卷影副本占用约 {SizeFormatter.Format(env.VssUsedBytes)}" : "本机未实测到卷影副本占用",
        });

        list.Add(new ShrinkRemedy
        {
            Index = 6,
            Title = "仍然分不出空间时（确认被“真正的不可移动文件”挡住）",
            Detail = "如果上面几条都做完、可分出空间仍是 0 或极小，说明挡住的是文件系统元数据" +
                     "（主文件表 $MFT、$LogFile）或坏簇重映射表——这些**无法搬动**，任何工具都缩不动。" +
                     "此时可行的路是：① 先备份数据、把整个分区重建后再恢复（风险高，需完整备份）；" +
                     "② 不缩 C 盘，改用「合盘」把别的分区并进来；" +
                     "③ 交给支持“移动分区”的第三方无损分区工具处理（本工具不做，以免数据风险）。",
            Command = "fsutil volume querycluster <盘符> <簇号>   # 可定位到底是哪个文件挡住了（需先有收缩失败日志）",
            Reversibility = "仅诊断，不改动任何东西",
            HasSideEffect = false,
            Applicable = canSplit >= 0 && canSplit < 1024L * 1024 * 1024,
            Benefit = canSplit >= 0 ? $"当前实测最多只能分出 {SizeFormatter.Format(canSplit)}" : string.Empty,
        });

        return list;
    }

    /// <summary>把补救清单渲染成可读文本（UI 对话框与说明书共用）。</summary>
    public static string BuildText(DiskProbeResult probe, PartitionInfo source, SupportedSize? supported)
    {
        var sb = new StringBuilder();
        var layout = probe.Layout;
        var vol = source.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
        var remedies = Build(probe, source, supported);

        sb.AppendLine($"【为什么 {source.LetterText} 分不出空间？】");
        sb.AppendLine();
        sb.AppendLine("Windows 收缩分区时，会把**可移动的文件**自动挪到分区前部，但下面这些属于**不可移动文件**，");
        sb.AppendLine("系统在运行中无法搬动它们——它们挡在哪里，分区就最多只能缩到哪里：");
        sb.AppendLine("  · 页面文件 pagefile.sys、休眠文件 hiberfil.sys");
        sb.AppendLine("  · 卷影副本存储区（系统还原点、文件历史）");
        sb.AppendLine("  · 文件系统元数据（主文件表 $MFT、$LogFile）与坏簇重映射表");
        sb.AppendLine("  · 分区尾部的驱动程序/文件系统保留区");
        sb.AppendLine();
        sb.AppendLine($"本机实测：");
        sb.AppendLine($"  · 卷容量 {SizeFormatter.Format((long)source.SizeBytes)}，" +
                      $"已用 {(vol is null ? "未知" : SizeFormatter.Format((long)vol.UsedBytes))}，" +
                      $"剩余 {(vol is null ? "未知" : SizeFormatter.Format((long)vol.FreeBytes))}");
        sb.AppendLine($"  · Windows 实测最少可收缩到 {(supported is { Ok: true } s ? SizeFormatter.Format((long)s.MinBytes) : "（尚未分析成功）")}，" +
                      $"即最多能分出 {(supported is { Ok: true } s2 ? SizeFormatter.Format((long)s2.MaxShrinkableBytes(source.SizeBytes)) : "（未知）")}");
        sb.AppendLine($"  · 休眠：{(probe.Environment.HibernateEnabled ? "已启用" : "未启用")}" +
                      $"（hiberfil.sys 合计 {SizeFormatter.Format(probe.Environment.HiberFileBytes)}）");
        sb.AppendLine($"  · 页面文件：{(probe.Environment.PageFilePaths.Count == 0 ? "未检测到（可能需要管理员权限）" : string.Join("、", probe.Environment.PageFilePaths))}");
        sb.AppendLine($"  · 卷影副本占用：{(probe.Environment.VssKnown ? SizeFormatter.Format(probe.Environment.VssUsedBytes) : "无法判定（需要管理员权限）")}");
        sb.AppendLine($"  · 系统保护：{(probe.Environment.SystemRestoreEnabled ? "已启用" : "未启用")}");
        sb.AppendLine();
        sb.AppendLine("补救措施（按「收益 / 风险」从优到劣排序，建议从上往下逐条试，每做一条就重新点一次「分析可压缩空间」）：");
        sb.AppendLine();

        foreach (var r in remedies)
        {
            sb.AppendLine($"  {r.Index}) {r.Title}{(r.Applicable ? string.Empty : "　（本机当前不适用）")}");
            sb.AppendLine($"     {r.Detail}");
            if (!string.IsNullOrWhiteSpace(r.Benefit)) sb.AppendLine($"     本机情况：{r.Benefit}");
            sb.AppendLine($"     可逆性：{r.Reversibility}{(r.HasSideEffect ? "　⚠ 请注意副作用" : string.Empty)}");
            if (!string.IsNullOrWhiteSpace(r.Command))
            {
                sb.AppendLine("     命令（管理员 PowerShell / 命令提示符）：");
                sb.AppendLine("       " + r.Command);
            }
            sb.AppendLine();
        }

        sb.AppendLine("小提示：收缩失败的原因会被 Windows 记到「事件查看器 → Windows 日志 → 应用程序」里的");
        sb.AppendLine("        事件 ID 259，里面会直接写明是哪个文件挡住了压缩。");
        sb.AppendLine();
        sb.AppendLine("安全提醒：以上第 1~4 条都是可恢复或低风险的；第 5 条会永久删除还原点，请确认后再执行。");
        sb.AppendLine("          任何一条执行后，请回到本窗口重新点「分析可压缩空间」以刷新实测结果。");

        return sb.ToString();
    }
}
