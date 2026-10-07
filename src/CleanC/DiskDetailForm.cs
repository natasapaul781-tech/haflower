using CleanC.Core;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 「磁盘明细」弹窗：逐磁盘、逐分区列出**判定所需的全部原始值**与判定结果。
///
/// 存在意义：分区类型白名单一旦判定失败（例如字段书写形式与预期不符），主窗口只会表现为
/// "下拉框是空的"，用户完全无从下手。本弹窗把 Type 原文 / GptType 原文 / MbrType / 判定结果 /
/// 判定理由 一次摊开，既能自查，也能一键导出 JSON 交给开发者定位——把"静默失败"变成"可解释失败"。
///
/// 只读：不修改任何分区与文件。
/// </summary>
public sealed class DiskDetailForm : Form
{
    private readonly DiskProbeResult _probe;
    private ListView _listView = null!;
    private Label _lblSummary = null!;

    public DiskDetailForm(DiskProbeResult probe)
    {
        _probe = probe;

        Text = "磁盘明细（只读）";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(1180, 720), new Size(820, 460), margin: 80);

        InitializeUi();
    }

    private void InitializeUi()
    {
        var layout = _probe.Layout;

        // ---- 顶部：结论 + 口径说明 ----
        var banner = UiStyle.CardPanel(DockStyle.Top);
        banner.Height = 74;
        var title = new Label
        {
            AutoSize = true,
            Location = new Point(16, 12),
            Text = "磁盘与分区明细",
            Font = UiStyle.Font(13f, bold: true),
            ForeColor = UiStyle.Text,
        };
        var hint = new Label
        {
            AutoSize = true,
            Location = new Point(16, 38),
            Text = "本工具只操作判定为「基本数据」的分区；其余分区只在这里展示，不会出现在可选列表里。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
        };
        _lblSummary = new Label
        {
            AutoSize = true,
            Location = new Point(16, 56),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.Primary,
        };
        banner.Controls.AddRange(new Control[] { title, hint, _lblSummary });

        // ---- 分区明细表 ----
        _listView = new ListView { Dock = DockStyle.Fill };
        UiStyle.StyleListView(_listView);
        _listView.View = View.Details;
        _listView.ShowGroups = true;
        _listView.Columns.Add("分区", 66);
        _listView.Columns.Add("盘符", 50);
        _listView.Columns.Add("可操作", 62);
        _listView.Columns.Add("类型判定", 108);
        _listView.Columns.Add("Type 原文", 92);
        _listView.Columns.Add("GptType 原文（带大括号）", 236);
        _listView.Columns.Add("MbrType", 62);
        _listView.Columns.Add("容量", 86);
        _listView.Columns.Add("文件系统", 72);
        _listView.Columns.Add("判定理由", 320);
        _listView.Resize += (_, _) => FitLastColumn();

        var groups = new Dictionary<int, ListViewGroup>();
        foreach (var d in layout.Disks.OrderBy(d => d.Number))
        {
            var g = new ListViewGroup($"磁盘 {d.Number}：{d.FriendlyName}　{SizeFormatter.Format((long)d.SizeBytes)}　{d.StyleText}" +
                                      $"　接口 {d.BusType}　健康 {d.HealthText}" +
                                      (d.IsBoot ? "　[启动盘]" : string.Empty) +
                                      (d.HasLdmPartitions ? "　[动态磁盘：不操作]" : string.Empty) +
                                      $"　—— 可操作分区 {layout.OperableCountOfDisk(d.Number)} 个");
            _listView.Groups.Add(g);
            groups[d.Number] = g;
        }

        int operable = 0;
        foreach (var p in layout.Partitions.OrderBy(p => p.DiskNumber).ThenBy(p => p.OffsetBytes))
        {
            if (p.IsOperable) operable++;
            var vol = p.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;

            var lvi = new ListViewItem($"{p.DiskNumber}-{p.PartitionNumber}")
            {
                ForeColor = p.IsOperable ? UiStyle.Text : UiStyle.SubText,
                UseItemStyleForSubItems = false,
            };
            lvi.SubItems.Add(p.DriveLetter is { } dl ? dl + ":" : "—");
            var opItem = lvi.SubItems.Add(p.IsOperable ? "✔ 是" : "✘ 否");
            opItem.ForeColor = p.IsOperable ? UiStyle.Success : UiStyle.Danger;
            lvi.SubItems.Add(p.KindText);
            lvi.SubItems.Add(string.IsNullOrWhiteSpace(p.TypeText) ? "（空）" : p.TypeText);
            lvi.SubItems.Add(string.IsNullOrWhiteSpace(p.GptType) ? "（空 / MBR）" : p.GptType);
            lvi.SubItems.Add(p.MbrType == 0 ? "0（GPT 盘）" : p.MbrType.ToString());
            lvi.SubItems.Add(SizeFormatter.Format((long)p.SizeBytes));
            lvi.SubItems.Add(vol is null ? "—" : vol.FileSystem);

            var reason = lvi.SubItems.Add(p.ClassificationReason);
            reason.ForeColor = p.IsOperable ? UiStyle.SubText : UiStyle.WarningText;
            lvi.ToolTipText = $"磁盘 {p.DiskNumber} 分区 {p.PartitionNumber}\n" +
                              $"类型判定：{p.KindText}\nType 原文：{(p.TypeText.Length == 0 ? "（空）" : p.TypeText)}\n" +
                              $"GptType：{(p.GptType.Length == 0 ? "（空）" : p.GptType)}\nMbrType：{p.MbrType}\n" +
                              $"GUID：{(p.Guid.Length == 0 ? "（空）" : p.Guid)}\n" +
                              $"偏移 {SizeFormatter.Format((long)p.OffsetBytes)}，容量 {SizeFormatter.Format((long)p.SizeBytes)}\n" +
                              $"判定理由：{p.ClassificationReason}" +
                              (p.IsOperable ? string.Empty : "\n（不可操作：不会出现在可选列表里）");

            if (groups.TryGetValue(p.DiskNumber, out var grp)) lvi.Group = grp;
            _listView.Items.Add(lvi);
        }

        _lblSummary.Text = layout.Disks.Count == 0
            ? (layout.IsElevated
                ? "未读取到任何磁盘（已提权，可能是 WMI 服务异常）"
                : "未读取到任何磁盘：需要管理员权限")
            : $"{layout.Disks.Count} 块磁盘 · {layout.Partitions.Count} 个分区 · " +
              $"判定为可操作（基本数据）的 {operable} 个" +
              (operable == 0 ? "　⚠ 没有可操作的分区，请把本窗口内容发给开发者定位" : string.Empty);

        // ---- 底部按钮 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = UiStyle.Card };
        var btnCopy = UiStyle.MakeButton("复制表格", ButtonVariant.Secondary, 96, CopyTable);
        var btnExport = UiStyle.MakeButton("导出诊断 JSON…", ButtonVariant.Secondary, 132, ExportJson);
        var btnClose = UiStyle.MakeButton("关闭", ButtonVariant.Primary, 88, () => Close());
        var lblNote = new Label
        {
            AutoSize = true,
            Location = new Point(250, 19),
            Text = "导出的是本次探测的原始报文 + 判定结果，便于定位字段差异（不含任何文件内容）。",
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText,
        };
        btnCopy.Location = new Point(16, 11);
        btnExport.Location = new Point(120, 11);
        btnClose.Location = new Point(0, 11);
        bottom.Controls.AddRange(new Control[] { btnCopy, btnExport, lblNote, btnClose });
        bottom.Resize += (_, _) => btnClose.Left = bottom.ClientSize.Width - btnClose.Width - 16;

        Controls.Add(_listView);
        Controls.Add(bottom);
        Controls.Add(banner);
        FitLastColumn();
    }

    private void FitLastColumn()
    {
        if (_listView.Columns.Count == 0) return;
        int last = _listView.Columns.Count - 1;
        int fixedSum = 0;
        for (int i = 0; i < last; i++) fixedSum += _listView.Columns[i].Width;
        _listView.Columns[last].Width = Math.Max(180, _listView.ClientSize.Width - fixedSum - 28);
    }

    private void CopyTable()
    {
        try
        {
            Clipboard.SetText(BuildTableText());
            MessageBox.Show(this, "已复制到剪贴板。", "已复制", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>把明细表渲染成纯文本（复制/日志用）。</summary>
    public string BuildTableText()
    {
        var layout = _probe.Layout;
        var sb = new StringBuilder();
        sb.AppendLine("=== 磁盘与分区明细（CleanC 只读探测） ===");
        sb.AppendLine($"探测时间 {TimeText.Stamp(layout.CapturedAt)}　用时 {layout.ElapsedMs} ms　" +
                      $"管理员权限 {(layout.IsElevated ? "是" : "否")}　系统盘 {(layout.SystemDriveLetter is { } s ? s + ":" : "未知")}");
        sb.AppendLine();
        foreach (var d in layout.Disks.OrderBy(x => x.Number))
        {
            sb.AppendLine($"◆ 磁盘 {d.Number}：{d.FriendlyName}　{SizeFormatter.Format((long)d.SizeBytes)}　{d.StyleText}" +
                          $"　接口 {d.BusType}　运行状态 {d.OperationalStatus}　健康 {d.HealthText}（原文 {d.HealthRaw}）" +
                          (d.IsBoot ? "　[启动盘]" : string.Empty) + (d.IsSystem ? "　[系统盘]" : string.Empty) +
                          (d.IsOffline ? "　[离线]" : string.Empty) + (d.IsReadOnly ? "　[只读]" : string.Empty) +
                          (d.HasLdmPartitions ? "　[动态磁盘]" : string.Empty));
            foreach (var p in layout.PartitionsOfDisk(d.Number))
            {
                var vol = p.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
                sb.AppendLine($"   分区 {p.DiskNumber}-{p.PartitionNumber}　{p.LetterText}" +
                              $"　可操作 {(p.IsOperable ? "是" : "否")}　判定 {p.KindText}" +
                              $"　Type原文 {(p.TypeText.Length == 0 ? "(空)" : p.TypeText)}" +
                              $"　GptType {(p.GptType.Length == 0 ? "(空)" : p.GptType)}" +
                              $"　MbrType {p.MbrType}　容量 {SizeFormatter.Format((long)p.SizeBytes)}" +
                              $"　文件系统 {(vol is null ? "—" : vol.FileSystem)}");
                sb.AppendLine($"       理由：{p.ClassificationReason}");
            }
        }
        if (layout.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("探测提示：");
            foreach (var p in layout.Problems) sb.AppendLine("  ~ " + p);
        }
        return sb.ToString();
    }

    private void ExportJson()
    {
        var layout = _probe.Layout;
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"tool\": \"CleanC 磁盘分区诊断\",");
        sb.AppendLine($"  \"capturedAt\": \"{TimeText.Stamp(layout.CapturedAt)}\",");
        sb.AppendLine($"  \"isElevated\": {(layout.IsElevated ? "true" : "false")},");
        sb.AppendLine($"  \"systemDriveLetter\": \"{(layout.SystemDriveLetter is { } s ? s.ToString() : string.Empty)}\",");
        sb.AppendLine("  \"classification\": [");
        int n = 0;
        foreach (var p in layout.Partitions.OrderBy(x => x.DiskNumber).ThenBy(x => x.OffsetBytes))
        {
            string sep = ++n < layout.Partitions.Count ? "," : string.Empty;
            sb.AppendLine("    { \"disk\": " + p.DiskNumber + ", \"partition\": " + p.PartitionNumber +
                          ", \"driveLetter\": \"" + (p.DriveLetter?.ToString() ?? string.Empty) + "\"" +
                          ", \"typeText\": " + JsonStr(p.TypeText) +
                          ", \"gptType\": " + JsonStr(p.GptType) +
                          ", \"mbrType\": " + p.MbrType +
                          ", \"kind\": \"" + p.Kind + "\"" +
                          ", \"operable\": " + (p.IsOperable ? "true" : "false") +
                          ", \"reason\": " + JsonStr(p.ClassificationReason) + " }" + sep);
        }
        sb.AppendLine("  ],");
        sb.AppendLine("  \"rawProbeJson\": " + JsonStr(layout.RawJson));
        sb.AppendLine("}");

        using var dlg = new SaveFileDialog
        {
            Title = "导出诊断 JSON",
            Filter = "JSON 文件 (*.json)|*.json|文本文件 (*.txt)|*.txt",
            FileName = $"disk-probe-{TimeText.CompactNow()}.json",
            InitialDirectory = ExistingVerifyDir(),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            OperationLog.Log("分区-诊断", "已导出磁盘明细 JSON：" + dlg.FileName);
            MessageBox.Show(this, "已导出：\n" + dlg.FileName, "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>优先落在仓库的 verify 目录下（从 exe 位置向上找），否则用当前目录。</summary>
    private static string ExistingVerifyDir()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "verify");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception) { /* 忽略 */ }
        return Environment.CurrentDirectory;
    }

    private static string JsonStr(string? value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
