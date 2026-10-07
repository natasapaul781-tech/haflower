using CleanC.Core;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 「校验 / 阻断原因」弹窗：把分区计划的全部阻断项与提示项列成表。
///
/// 主窗口只留一行「✔ 全部通过」或「⛔ 阻断 N 项」，点开本窗口看逐条原因与补救动作。
/// 只读：不做任何改动。
/// </summary>
public sealed class BlockersForm : Form
{
    private readonly IReadOnlyList<BlockReason> _blocks;
    private readonly string _title;

    public BlockersForm(string title, IReadOnlyList<BlockReason> blocks, string? subtitle = null)
    {
        _title = title;
        _blocks = blocks ?? Array.Empty<BlockReason>();

        Text = title;
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(980, 520), new Size(760, 380));

        InitializeUi(subtitle);
    }

    private void InitializeUi(string? subtitle)
    {
        int blocking = _blocks.Count(b => !b.IsWarning);
        int warnings = _blocks.Count(b => b.IsWarning);

        // ---- 顶部结论 ----
        var banner = UiStyle.CardPanel(DockStyle.Top);
        banner.Height = string.IsNullOrWhiteSpace(subtitle) ? 62 : 84;

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(16, 12),
            Text = _title,
            Font = UiStyle.Font(13f, bold: true),
            ForeColor = blocking > 0 ? UiStyle.Danger : UiStyle.Success,
        };
        var conclusion = new Label
        {
            AutoSize = true,
            Location = new Point(16, 38),
            Text = blocking > 0
                ? $"⛔ 有 {blocking} 项必须解决的问题，解决前「开始执行」不可用。" +
                  (warnings > 0 ? $"（另有 {warnings} 项提示）" : string.Empty)
                : warnings > 0
                    ? $"✔ 没有阻断项；有 {warnings} 项提示（不影响执行，但建议看一下）。"
                    : "✔ 全部通过，可以执行。",
            Font = UiStyle.Font(9f),
            ForeColor = blocking > 0 ? UiStyle.Danger : UiStyle.SubText,
        };
        banner.Controls.AddRange(new Control[] { title, conclusion });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            banner.Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(16, 60),
                Text = subtitle,
                Font = UiStyle.Font(8.5f),
                ForeColor = UiStyle.SubText,
            });
        }

        // ---- 明细表 ----
        var list = new ListView { Dock = DockStyle.Fill };
        UiStyle.StyleListView(list);
        list.View = View.Details;
        list.Columns.Add("级别", 56);
        list.Columns.Add("项目", 180);
        list.Columns.Add("说明", 420);
        list.Columns.Add("补救动作", 300);
        list.Resize += (_, _) =>
        {
            int last = list.Columns.Count - 1;
            int fixedSum = 0;
            for (int i = 0; i < last; i++) fixedSum += list.Columns[i].Width;
            list.Columns[last].Width = Math.Max(160, list.ClientSize.Width - fixedSum - 28);
        };

        foreach (var b in _blocks)
        {
            var lvi = new ListViewItem(b.IsWarning ? "提示" : "阻断")
            {
                ForeColor = b.IsWarning ? UiStyle.SubText : UiStyle.Danger,
                UseItemStyleForSubItems = false,
            };
            // ListView 单元格与 ToolTip 都是纯文本，必须去掉 **粗体** 标记，否则会露出星号
            lvi.SubItems.Add(RichTextRenderer.StripMarkdown(b.Title));
            lvi.SubItems.Add(RichTextRenderer.StripMarkdown(b.Detail));
            var fix = lvi.SubItems.Add(string.IsNullOrWhiteSpace(b.Remedy) ? "—" : RichTextRenderer.StripMarkdown(b.Remedy));
            fix.ForeColor = UiStyle.Primary;
            lvi.ToolTipText = RichTextRenderer.StripMarkdown(b.ToString());
            list.Items.Add(lvi);
        }

        if (_blocks.Count == 0)
        {
            var lvi = new ListViewItem("通过") { ForeColor = UiStyle.Success };
            lvi.SubItems.Add("没有需要处理的问题");
            lvi.SubItems.Add("当前选择与设置全部通过校验。");
            lvi.SubItems.Add("—");
            list.Items.Add(lvi);
        }

        // ---- 底部 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = UiStyle.Card };
        var btnCopy = UiStyle.MakeButton("复制全部", ButtonVariant.Secondary, 96, CopyAll);
        var btnClose = UiStyle.MakeButton("关闭", ButtonVariant.Primary, 88, () => Close());
        btnCopy.Location = new Point(16, 11);
        btnClose.Location = new Point(0, 11);
        bottom.Controls.AddRange(new Control[] { btnCopy, btnClose });
        bottom.Resize += (_, _) => btnClose.Left = bottom.ClientSize.Width - btnClose.Width - 16;

        Controls.Add(list);
        Controls.Add(bottom);
        Controls.Add(banner);
    }

    private void CopyAll()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {_title} ===");
            foreach (var b in _blocks)
                sb.AppendLine($"[{(b.IsWarning ? "提示" : "阻断")}] {b}");
            Clipboard.SetText(sb.ToString());
            MessageBox.Show(this, "已复制到剪贴板。", "已复制", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>主窗口一行摘要用的短文本。</summary>
    public static string Summarize(IReadOnlyList<BlockReason> blocks)
    {
        int blocking = blocks.Count(b => !b.IsWarning);
        int warnings = blocks.Count(b => b.IsWarning);
        if (blocking == 0 && warnings == 0) return "✔ 校验全部通过";
        if (blocking == 0) return $"✔ 无阻断项（{warnings} 项提示）";
        return $"⛔ 阻断 {blocking} 项" + (warnings > 0 ? $"，另有 {warnings} 项提示" : string.Empty);
    }
}
