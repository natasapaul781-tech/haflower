using System.Text;

namespace CleanC.Core;

/// <summary>
/// 《操作说明书》生成器：把探测结果、推荐值与计划渲染成**可打印、可照做**的纯文本，
/// 落盘到桌面（UTF-8 带 BOM，记事本中文不乱码）。
///
/// 本类只写文本文件，不执行任何分区与文件操作。
/// </summary>
public static class PartitionScriptWriter
{
    private const string ToolName = "C盘清理工具（CleanC）";

    /// <summary>
    /// 桌面上的说明书文件路径（同名文件按时间戳区分，不覆盖历史）。
    /// 桌面不可用时依次退到「文档」与程序所在目录——**绝不**返回相对路径（那会落到当前工作目录）。
    /// </summary>
    public static string DefaultManualPath()
    {
        string? dir = UserContext.WritableDirectory(UserContext.Desktop)
                      ?? UserContext.WritableDirectory(UserContext.Documents)
                      ?? UserContext.WritableDirectory(UserContext.AppDirectory());
        if (dir is null)
            throw new InvalidOperationException("找不到可用于保存说明书的目录（桌面/文档/程序目录都不可写）。");

        return Path.Combine(dir, $"CleanC-分区说明-{TimeText.CompactNow()}.txt");
    }

    /// <summary>把说明书写入文件；成功返回完整路径，失败抛出异常由调用方提示。</summary>
    public static string WriteManual(string content, string? path = null)
    {
        string target = string.IsNullOrWhiteSpace(path) ? DefaultManualPath() : path;
        string? dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // .txt 无格式，必须去掉 **粗体** 标记，否则用户看到一堆星号
        string plain = RichTextRenderer.StripMarkdown(content);
        // UTF-8 带 BOM：确保记事本等工具正确识别中文
        File.WriteAllText(target, plain, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return target;
    }

    // ---------- 说明书正文 ----------

    public static string BuildManual(DiskProbeResult probe, PartitionPlan plan, RecommendResult? recommend,
        IReadOnlyList<BlockReason> blocks)
    {
        var sb = new StringBuilder();

        sb.AppendLine("================================================================================");
        sb.AppendLine($"  {ToolName} — 磁盘分区操作说明书");
        sb.AppendLine($"  生成时间：{TimeText.StampNow()}");
        sb.AppendLine($"  日志文件：{OperationLog.CurrentPath}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("【重要】分区操作有风险：改动分区表属于不可逆操作，一旦断电或强行中断可能导致");
        sb.AppendLine("        数据丢失甚至无法开机。请先完成：①重要文件另有备份 ②关闭正在运行的");
        sb.AppendLine("        程序与杀毒扫描 ③笔记本接上电源（电量 > 50%）④确认不会断电。");
        sb.AppendLine("        建议先在一台虚拟机或备用机上试一遍。");
        sb.AppendLine();

        AppendCurrentLayout(sb, probe);

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("二、本次方案与推荐值");
        sb.AppendLine("--------------------------------------------------------------------------------");
        AppendPlanSummary(sb, plan, recommend);

        if (plan.Kind == PartitionOpKind.Merge)
            AppendMergePreconditions(sb, plan);

        if (blocks.Count > 0)
        {
            sb.AppendLine("  校验结果：");
            foreach (var b in blocks)
                sb.AppendLine($"    {(b.IsWarning ? "[提示]" : "[阻断]")} {b}");
            sb.AppendLine();
        }

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("三、分步操作（每步：做什么 / 预期结果 / 出错了怎么办）");
        sb.AppendLine("--------------------------------------------------------------------------------");
        int n = 1;
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"  第 {n++} 步：{step.Title}{(step.IsDestructive ? "  ★ 不可逆" : string.Empty)}" +
                          $"{(step.IsInterruptible ? string.Empty : "  （执行中不可中断）")}");
            sb.AppendLine($"    做什么 ：{step.Action}");
            sb.AppendLine($"    预期结果：{step.Expected}");
            sb.AppendLine($"    出错了 ：{step.OnFailure}");
            if (!string.IsNullOrWhiteSpace(step.PowerShell))
            {
                sb.AppendLine("    等价命令（可在「管理员 PowerShell」中逐条粘贴执行）：");
                foreach (var line in step.PowerShell.Split('\n'))
                    sb.AppendLine("      " + line.TrimEnd('\r'));
            }
            sb.AppendLine();
        }

        AppendRecoveryGuide(sb);
        AppendGlossary(sb);

        sb.AppendLine("================================================================================");
        sb.AppendLine("  说明书结束。本文件仅描述操作，不会自行执行任何改动。");
        sb.AppendLine("================================================================================");

        return sb.ToString();
    }

    // ---------- 第一节：本机磁盘现状 ----------

    private static void AppendCurrentLayout(StringBuilder sb, DiskProbeResult probe)
    {
        var layout = probe.Layout;

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("一、本机磁盘现状");
        sb.AppendLine("--------------------------------------------------------------------------------");

        sb.AppendLine($"  系统盘：{(layout.SystemDriveLetter is { } s ? s + ":" : "无法判定")}" +
                      $"　检测用时 {layout.ElapsedMs} ms　检测时间 {TimeText.Format(layout.CapturedAt, "HH:mm:ss")}");
        sb.AppendLine();

        foreach (var d in layout.Disks)
        {
            sb.AppendLine($"  ◆ {d.DisplayName}");
            sb.AppendLine($"      容量 {SizeFormatter.Format((long)d.SizeBytes)}　分区表 {d.StyleText}" +
                          $"　接口 {d.BusType}　健康 {d.HealthText}" +
                          (d.IsBoot ? "　[启动盘]" : string.Empty) +
                          (d.IsSystem ? "　[系统盘]" : string.Empty) +
                          (d.HasLdmPartitions ? "　[动态磁盘：本工具不操作]" : string.Empty));
            sb.AppendLine("      分区布局（← 左 → 右，按起始位置排序）：");
            sb.AppendLine("      " + BuildStrip(layout, d.Number));

            foreach (var p in layout.PartitionsOfDisk(d.Number))
            {
                var vol = p.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
                string size = SizeFormatter.Format((long)p.SizeBytes);
                string detail = vol is null
                    ? "（无卷信息）"
                    : $"{vol.FileSystem}　已用 {SizeFormatter.Format((long)vol.UsedBytes)}　" +
                      $"可用 {SizeFormatter.Format((long)vol.FreeBytes)}";
                sb.AppendLine($"        · {p.LetterText} {p.KindText}　{size}　{detail}" +
                              $"{(p.IsOperable ? string.Empty : "　← 本工具不会操作此分区")}");
            }
            sb.AppendLine();
        }

        var env = probe.Environment;
        sb.AppendLine("  环境检查：");
        if (env.BitLocker.Count == 0)
        {
            sb.AppendLine("    · BitLocker：未检测到加密卷");
        }
        else
        {
            foreach (var kv in env.BitLocker.OrderBy(k => k.Key))
                sb.AppendLine($"    · BitLocker {kv.Key}:　{kv.Value.Text}");
        }
        sb.AppendLine($"    · 页面文件：{(env.PageFilePaths.Count == 0 ? "未检测到" : string.Join("、", env.PageFilePaths))}");
        sb.AppendLine($"    · 休眠：{(env.HibernateEnabled ? "已启用" : "未启用")}" +
                      $"（hiberfil.sys 合计 {SizeFormatter.Format(env.HiberFileBytes)}）");
        sb.AppendLine($"    · 卷影副本（系统还原/备份）占用：{(env.VssKnown ? SizeFormatter.Format(env.VssUsedBytes) : "无法判定")}");
        sb.AppendLine($"    · 系统保护：{(env.SystemRestoreEnabled ? "已启用" : "未启用")}" +
                      (env.SystemRestoreDiskPercent > 0 ? $"（上限 {env.SystemRestoreDiskPercent}%）" : string.Empty));
        sb.AppendLine();

        if (layout.Problems.Count > 0)
        {
            sb.AppendLine("  探测过程中的提示：");
            foreach (var p in layout.Problems) sb.AppendLine("    ~ " + p);
            sb.AppendLine();
        }
    }

    /// <summary>纯文本条带图：用字符粗略表示各分区在磁盘上的相对位置与大小。</summary>
    private static string BuildStrip(DiskLayoutSnapshot layout, int diskNumber)
    {
        const int width = 68;
        var disk = layout.DiskByNumber(diskNumber);
        if (disk is null || disk.SizeBytes == 0) return "（无法绘制）";

        var cells = new char[width];
        for (int i = 0; i < width; i++) cells[i] = '·';   // · = 未分配

        foreach (var p in layout.PartitionsOfDisk(diskNumber))
        {
            int from = (int)Math.Max(0, (long)(p.OffsetBytes * (ulong)width / disk.SizeBytes));
            int to = (int)Math.Min(width - 1, (long)((p.EndBytes - 1) * (ulong)width / disk.SizeBytes));
            char ch = p.DriveLetter is { } c ? char.ToUpperInvariant(c)
                : p.Kind == PartitionKind.Recovery ? 'R'
                : p.Kind == PartitionKind.EfiSystem ? 'E'
                : p.Kind == PartitionKind.MicrosoftReserved ? 'M'
                : '?';
            for (int i = from; i <= to && i < width; i++) cells[i] = ch;
        }

        string strip = new(cells);
        return "|" + strip + "|　（· = 未分配，E = EFI，M = MSR，R = 恢复分区，字母 = 盘符）";
    }

    // ---------- 第二节：方案摘要 ----------

    private static void AppendPlanSummary(StringBuilder sb, PartitionPlan plan, RecommendResult? recommend)
    {
        sb.AppendLine($"  方案：{plan.Title}（{(plan.IsDryRun ? "演练：只生成说明，不执行" : "已确认执行")}）");
        sb.AppendLine();

        if (recommend is not null && plan.Kind == PartitionOpKind.Split)
        {
            sb.AppendLine("  推荐值构成（全部按本机实测计算）：");
            foreach (var line in recommend.Lines)
                sb.AppendLine($"    {line.Label,-28}{line.Value,14}    {line.Note}");
            sb.AppendLine("    " + new string('-', 76));
            sb.AppendLine($"    {"建议保留",-28}{SizeFormatter.Format(recommend.RecommendedKeepBytes),14}    ★ 推荐值");
            sb.AppendLine($"    {"绝对下限（不允许低于）",-28}{SizeFormatter.Format(recommend.FloorKeepBytes),14}    低于此值本工具会拒绝执行");
            if (recommend.MaxShrinkableBytes is { } ms)
                sb.AppendLine($"    {"本机最多可分出的空间",-28}{SizeFormatter.Format((long)ms),14}    受不可移动文件位置限制");
            sb.AppendLine($"    {"新分区最小建议容量",-28}{SizeFormatter.Format(recommend.NewPartMinBytes),14}    低于此值会提示偏小");
            sb.AppendLine();
        }

        sb.AppendLine("  容量变化：");
        switch (plan.Kind)
        {
            case PartitionOpKind.Split:
                sb.AppendLine($"    源分区：{SizeFormatter.Format(plan.SourceSizeBeforeBytes)} → {SizeFormatter.Format(plan.SourceSizeAfterBytes)}" +
                              $"（分出 {SizeFormatter.Format(plan.FreedBytes)}）");
                sb.AppendLine($"    新分区：{SizeFormatter.Format(plan.FreedBytes)}　盘符 {plan.NewDriveLetter}:" +
                              $"　卷标「{plan.NewVolumeLabel}」　文件系统 NTFS");
                break;
            case PartitionOpKind.Merge:
                sb.AppendLine($"    目标分区：{SizeFormatter.Format(plan.TargetSizeBeforeBytes)} → {SizeFormatter.Format(plan.TargetSizeAfterBytes)}" +
                              $"（增加 {SizeFormatter.Format(plan.FreedBytes)}）");
                sb.AppendLine($"    被删除分区：{SizeFormatter.Format(plan.SourceSizeBeforeBytes)}" +
                              (plan.Impact is null
                                  ? "（删除前未盘点内容）"
                                  : $"（其中 {plan.Impact.ScaleText}）"));
                if (plan.Impact is { TopLevel.Count: > 0 } imp)
                {
                    sb.AppendLine("    将被永久删除的主要内容：");
                    foreach (var (name, bytes) in imp.TopLevel)
                        sb.AppendLine($"        {name,-32}{SizeFormatter.Format(bytes),12}");
                }
                break;
            case PartitionOpKind.Create:
                sb.AppendLine($"    新分区：{SizeFormatter.Format(plan.FreedBytes)}　盘符 {plan.NewDriveLetter}:" +
                              $"　卷标「{plan.NewVolumeLabel}」　文件系统 NTFS");
                sb.AppendLine($"    起始位置：{SizeFormatter.Format(plan.NewPartitionStartBytes)}（已按 1 MB 对齐）");
                sb.AppendLine("    现有分区：不做任何改动（本方案只使用未分配空间）");
                break;
        }
        sb.AppendLine();
        sb.AppendLine($"  请注意容量单位：硬盘厂商标称容量按 1000 换算，Windows 按 1024 显示，");
        sb.AppendLine($"  因此标称 1 TB 的硬盘在系统里显示约 931 GB，这是正常现象，不是少了空间。");
        sb.AppendLine();
    }

    // ---------- 第四节：恢复指引 ----------

    private static void AppendRecoveryGuide(StringBuilder sb)
    {
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("四、万一出问题怎么办（请先不要反复重启，按顺序排查）");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("  1) 分区做完但盘符没出现 / 新盘看不到：");
        sb.AppendLine("     打开「磁盘管理」(Win+X → 磁盘管理)，找到显示为「未分配」或没有盘符的分区，");
        sb.AppendLine("     右键 →「更改驱动器号和路径」→「添加」分配一个盘符即可。");
        sb.AppendLine();
        sb.AppendLine("  2) 操作在“收缩”这一步失败，提示可压缩空间不足：");
        sb.AppendLine("     数据不会丢，只是没能缩小。回到工具，按「为什么分不出空间」里的补救清单处理，");
        sb.AppendLine("     然后重新点「分析可压缩空间」。");
        sb.AppendLine();
        sb.AppendLine("  3) 删除分区后断电 / 中断，只剩「未分配」空间：");
        sb.AppendLine("     目标分区（例如 C:）的数据完好，可正常使用；未被并回去的空间仍然是未分配的。");
        sb.AppendLine("     重新运行本工具或磁盘管理，把「未分配」空间扩展进目标分区即可（右键目标分区 →「扩展卷」）。");
        sb.AppendLine("     注意：被删除那个分区里的数据**已经不在**，请从暂存位置找回。");
        sb.AppendLine();
        sb.AppendLine("  4) 系统无法启动（最严重）：");
        sb.AppendLine("     · 不要反复重启造成二次写入；");
        sb.AppendLine("     · 用 Windows 安装 U 盘启动，选择「修复计算机」→「启动修复」；");
        sb.AppendLine("     · 若提示找不到引导，在安装 U 盘的「命令提示符」中执行（X: 为系统盘符，通常是 C:）：");
        sb.AppendLine("         diskpart");
        sb.AppendLine("           list disk");
        sb.AppendLine("           list volume       ← 确认系统盘与 EFI 分区仍在");
        sb.AppendLine("           exit");
        sb.AppendLine("         bcdboot X:\\Windows /s S: /f UEFI     ← S: 为 EFI 系统分区（需先给它分配盘符）");
        sb.AppendLine("         reagentc /info                        ← 检查 WinRE 状态");
        sb.AppendLine();
        sb.AppendLine("  5) 数据误删：立即停止对该分区的任何写入（不要再装软件、不要下载），");
        sb.AppendLine("     用数据恢复软件扫描该磁盘。写入越少，恢复成功率越高。");
        sb.AppendLine();
        sb.AppendLine("  6) 不确定时：把所有疑问连同上方的磁盘布局截图，交给专业维修人员处理。");
        sb.AppendLine();
    }

    // ---------- 第五节：名词解释 ----------

    private static void AppendGlossary(StringBuilder sb)
    {
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("五、名词解释（给不熟悉分区的用户）");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("  · 分区 / 分盘：把一块物理硬盘切成几块，每块分配一个盘符（C:、D:…）。");
        sb.AppendLine("  · 收缩（缩小卷）：把一个分区从「尾部」缩小，释放出的空间变成「未分配」。");
        sb.AppendLine("    普通文件会被系统自动挪到分区前部，**不会丢数据**。");
        sb.AppendLine("  · 扩展（扩展卷）：把紧邻分区**右侧**的「未分配」空间并进这个分区。");
        sb.AppendLine("    只能在右侧、必须连续——这是 Windows 的硬性限制，不是工具的限制。");
        sb.AppendLine("  · 不可移动文件：页面文件、卷影副本（系统还原）、文件系统元数据等，");
        sb.AppendLine("    运行中的 Windows 无法搬动它们，它们挡在哪里，分区就最多只能缩到哪里。");
        sb.AppendLine("  · NTFS：Windows 的分区格式。只有 NTFS 可以无损调整大小。");
        sb.AppendLine("  · 未分配空间：不属于任何分区的空闲区域，不显示盘符，需要新建分区或并入相邻分区才能使用。");
        sb.AppendLine("  · EFI / MSR / 恢复分区：系统启动与恢复用的隐藏分区，**绝对不能删除**，删了会开不了机。");
        sb.AppendLine();
    }

    // ---------- 弹窗用的明细文本 ----------

    /// <summary>「推荐明细」弹窗正文：把推荐值的来龙去脉讲清楚（含本机实测边界）。</summary>
    public static string BuildRecommendDetail(RecommendResult rec, PartitionInfo source, VolumeInfo? volume)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【{(source.DriveLetter is { } c ? c + ":" : "该卷")} 分盘推荐值明细】");
        sb.AppendLine();
        sb.AppendLine($"卷容量 {SizeFormatter.Format((long)source.SizeBytes)}" +
                      (volume is null
                          ? string.Empty
                          : $"　已用 {SizeFormatter.Format((long)volume.UsedBytes)}　剩余 {SizeFormatter.Format((long)volume.FreeBytes)}" +
                            $"　文件系统 {volume.FileSystem}　健康 {volume.HealthText}"));
        sb.AppendLine();
        sb.AppendLine("① 按需计算（这台机器需要多少空间才能稳定运行）：");
        foreach (var line in rec.Lines)
        {
            sb.AppendLine($"   {line.Label,-30}{line.Value,14}");
            if (!string.IsNullOrWhiteSpace(line.Note)) sb.AppendLine($"   {"",-30}{"",14}  {line.Note}");
        }
        sb.AppendLine("   " + new string('-', 74));
        sb.AppendLine($"   {"按需建议值",-30}{SizeFormatter.Format(rec.RawRecommendedKeepBytes),14}");
        sb.AppendLine($"   {"硬下限（不允许低于）",-30}{SizeFormatter.Format(rec.FloorKeepBytes),14}");
        sb.AppendLine($"   {"建议保留 ≥",-30}{SizeFormatter.Format(rec.RecommendedKeepBytes),14}   ★ 界面默认显示的就是它");
        sb.AppendLine();
        sb.AppendLine("② 本机实测的可操作范围（由 Windows 按不可移动文件位置算出）：");
        if (rec.MaxShrinkableBytes is { } max)
        {
            sb.AppendLine($"   本机最多可分出　　　　{SizeFormatter.Format((long)max),14}");
            sb.AppendLine($"   滑块下界（最少保留）　{SizeFormatter.Format(rec.SliderMinKeepBytes),14}");
            sb.AppendLine($"   滑块上界（最多保留）　{SizeFormatter.Format(rec.SliderMaxKeepBytes),14}");
        }
        else
        {
            sb.AppendLine("   尚未分析：点主窗口的「分析可压缩空间」后这里会显示本机实测上限。");
        }
        sb.AppendLine($"   新分区最小建议容量　　{SizeFormatter.Format(rec.NewPartMinBytes),14}");
        sb.AppendLine();
        if (rec.CapabilityNote is { } note)
        {
            sb.AppendLine("③ 说明：");
            sb.AppendLine("   " + note);
            sb.AppendLine();
        }
        sb.AppendLine("口径说明：");
        sb.AppendLine("   · 全部数字都来自本机实测，不是写死的固定值（不同机器差异很大）。");
        sb.AppendLine("   · 「硬下限」= max(64 GB, 已用 + max(已用的 10%, 20 GB))；64 GB 是 Windows 11 的安装最低要求。");
        sb.AppendLine("   · 「本机最多可分出」来自 GetSupportedSize 的 SizeMin：系统会把可移动文件挪到分区前部，");
        sb.AppendLine("     但页面文件、卷影副本、文件系统元数据等不可移动文件挡在哪里，就最多只能缩到哪里。");
        sb.AppendLine("   · 页面文件/休眠文件默认留在本卷，所以它们的实测大小被计入「必须保留」。");
        return sb.ToString();
    }

    /// <summary>「环境检查」弹窗正文：影响分区操作的环境事实与结论。</summary>
    public static string BuildEnvironmentCheck(DiskProbeResult probe)
    {
        var env = probe.Environment;
        var layout = probe.Layout;
        var sb = new StringBuilder();
        sb.AppendLine("【环境检查（只读）】");
        sb.AppendLine();
        sb.AppendLine($"管理员权限：{(layout.IsElevated ? "是" : "否（磁盘信息不可读）")}");
        sb.AppendLine($"系统盘：{(layout.SystemDriveLetter is { } s ? s + ":" : "无法判定")}");
        sb.AppendLine();

        sb.AppendLine("BitLocker 加密：");
        if (env.BitLocker.Count == 0)
        {
            sb.AppendLine("  未检测到加密卷（或无法查询）。");
        }
        else
        {
            foreach (var kv in env.BitLocker.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {kv.Key}:　{kv.Value.Text}");
                if (kv.Value.Protection == BitLockerProtection.On)
                    sb.AppendLine($"      ⚠ 调整该盘分区前应挂起保护：Suspend-BitLocker -MountPoint \"{kv.Key}:\" -RebootCount 1");
            }
        }
        sb.AppendLine();

        sb.AppendLine("页面文件（不可移动文件，必须留在所在卷）：");
        if (env.PageFilePaths.Count == 0)
            sb.AppendLine("  未检测到（可能需要管理员权限，或确实没有配置页面文件）。");
        else
            foreach (var p in env.PageFilePaths) sb.AppendLine("  " + p);
        sb.AppendLine();

        sb.AppendLine("休眠（hiberfil.sys 同样不可移动）：");
        sb.AppendLine($"  {(env.HibernateEnabled ? "已启用" : "未启用")}　实测 hiberfil.sys 合计 {SizeFormatter.Format(env.HiberFileBytes)}");
        if (env.HibernateEnabled && env.HiberFileBytes > 0)
            sb.AppendLine("  提示：关闭休眠（powercfg /h off）通常能立刻让可压缩空间变大，且是可逆的。");
        sb.AppendLine();

        sb.AppendLine("卷影副本（系统还原点 / 文件历史）：");
        sb.AppendLine($"  占用 {(env.VssKnown ? SizeFormatter.Format(env.VssUsedBytes) : "无法判定（需要管理员权限）")}");
        sb.AppendLine($"  系统保护：{(env.SystemRestoreEnabled ? "已启用" : "未启用")}" +
                      (env.SystemRestoreDiskPercent > 0 ? $"（上限 {env.SystemRestoreDiskPercent}%）" : string.Empty));
        sb.AppendLine("  提示：卷影副本存储区也是不可移动文件，常常是「可用压缩空间为 0」的真凶；");
        sb.AppendLine("        删除还原点不可逆，请确认不需要用它回滚系统后再操作。");
        sb.AppendLine();

        if (layout.Problems.Count > 0)
        {
            sb.AppendLine("探测过程中的提示：");
            foreach (var p in layout.Problems) sb.AppendLine("  ~ " + p);
        }
        return sb.ToString();
    }

    // ---------- 第二节补充：合盘的前置条件（必须用户自己完成） ----------

    /// <summary>
    /// 合盘的前置条件。本工具**不替用户备份**，因此说明书必须把"该你自己做什么"写清楚，
    /// 并把将被删除的具体规模如实列出（这是删除之后唯一的可追溯线索）。
    /// </summary>
    private static void AppendMergePreconditions(StringBuilder sb, PartitionPlan plan)
    {
        string src = plan.Source.DiskNumber >= 0 && plan.Impact is not null
            ? plan.Impact.Root.TrimEnd('\\')
            : "源分区";

        sb.AppendLine("  前置条件（必须你自己完成，本工具不会替你备份任何文件）：");
        sb.AppendLine();
        sb.AppendLine("    ▸ 本方案会**永久删除**一个分区，其中的文件将无法访问。工具只负责把后果讲清楚，");
        sb.AppendLine("      不会复制、不会搬到暂存位置、不会做任何备份动作。");
        sb.AppendLine();

        if (plan.Impact is { } imp)
        {
            sb.AppendLine($"    ▸ 该分区里将被删除的内容：{imp.ScaleText}" +
                          (imp.Truncated ? "（统计已截断，实际更多）" : string.Empty) +
                          (imp.Denied > 0 ? $"；另有 {imp.Denied} 个目录因权限不足未统计" : string.Empty));
            if (imp.TopLevel.Count > 0)
            {
                sb.AppendLine("      主要内容：");
                foreach (var (name, bytes) in imp.TopLevel)
                    sb.AppendLine($"        {name,-32}{SizeFormatter.Format(bytes),12}");
            }
        }
        else
        {
            sb.AppendLine($"    ▸ 本次未盘点 {src} 的内容规模（可在工具里点「删除并扩容…」触发只读盘点）。");
        }
        sb.AppendLine();

        sb.AppendLine("    ▸ 删除前请务必完成：");
        sb.AppendLine("        1. 把还需要的东西复制到其它磁盘、移动硬盘或网盘；复制完**随手打开几个文件确认能正常打开**，");
        sb.AppendLine("           只看文件数量相同是不够的；");
        sb.AppendLine("        2. 关掉正在使用该分区文件的程序（下载工具、虚拟机、云同步、杀毒扫描等）；");
        sb.AppendLine("        3. 笔记本接上电源，确认过程中不会断电、不会强制关机。");
        sb.AppendLine();
        sb.AppendLine("    ▸ 如果不备份就执行：删除分区不可逆，Windows 没有撤销；");
        sb.AppendLine("      数据恢复软件只有在「删除后没有向这块磁盘写入任何新数据」时才可能成功，且成功率不保证；");
        sb.AppendLine("      一旦把空间并入目标分区并开始写入，恢复可能性会大幅下降甚至归零。");
        sb.AppendLine();
    }

    // ---------- UI 预览用的简短计划文本 ----------

    /// <summary>计划预览（UI 上灰色文字展示，不做说明书的完整排版）。</summary>
    public static string BuildPreview(PartitionPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{plan.Title}");
        sb.AppendLine($"磁盘：{plan.DiskName}（{plan.DiskStyle}）");
        foreach (var step in plan.Steps)
            sb.AppendLine($"  {step.Index}. {step.Title}{(step.IsDestructive ? "　★不可逆" : string.Empty)}" +
                          $"{(step.IsInterruptible ? string.Empty : "　（不可中断）")}");
        return sb.ToString().TrimEnd();
    }
}
