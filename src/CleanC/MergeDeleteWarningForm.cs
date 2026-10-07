using CleanC.Core;
using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 合盘删除分区的**危险确认弹窗**。
///
/// 设计取舍（本工具不替用户备份，所以必须由界面把后果顶到用户眼前）：
/// ① 正文用**真实盘点数字**（将被删除的文件数、总体积、主要目录），不是一句空泛的"注意数据安全"；
/// ② 明确写出"本工具不会替你备份任何文件"，并逐条列出不备份的后果；
/// ③ 危险按钮**默认禁用**，只有勾选"已确认不再需要或已自行备份"之后才可点；
/// ④ **默认按钮是「取消」**，回车即取消；Esc 也取消；危险按钮不接收回车；
/// ⑤ 只有危险按钮能把结果置为 OK，直接关窗视为取消。
///
/// 本窗口**只读**：不移动、不复制、不删除任何文件，只负责把风险讲清楚并收一次确认。
/// </summary>
public sealed class MergeDeleteWarningForm : Form
{
    private readonly string _sourceText;
    private readonly string _targetText;

    private CheckBox _chkAcknowledge = null!;
    private RoundedButton _btnDelete = null!;

    /// <summary>用户是否在勾选后明确点了删除按钮。</summary>
    public bool Confirmed => DialogResult == DialogResult.OK;

    public MergeDeleteWarningForm(PartitionImpact impact, PartitionInfo source, PartitionInfo target, long extendableBytes)
    {
        _sourceText = source.DriveLetter is { } c ? c + ":" : "源分区";
        _targetText = target.DriveLetter is { } d ? d + ":" : "目标分区";

        Text = $"危险：即将永久删除分区 {_sourceText}";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(900, 760), new Size(660, 460), margin: 100);

        // 默认按钮 = 取消（回车即取消），危险按钮不参与回车
        InitializeUi(PartitionImpactScanner.BuildWarningText(impact, source, target, extendableBytes));
    }

    private void InitializeUi(string warningText)
    {
        // ---- 红色危险横幅 ----
        var banner = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiStyle.WarningBg };
        var title = new Label
        {
            AutoSize = false,
            Location = new Point(16, 10),
            Size = new Size(800, 22),
            Text = $"⚠ 危险：即将永久删除分区 {_sourceText} 的全部内容",
            Font = UiStyle.Font(13f, bold: true),
            ForeColor = UiStyle.WarningText,
            AutoEllipsis = true,
        };
        var sub = new Label
        {
            AutoSize = false,
            Location = new Point(16, 36),
            Size = new Size(800, 18),
            Text = "本工具不会替你备份任何文件。请先确认下面的数字与后果，再决定是否继续。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.WarningText,
            AutoEllipsis = true,
        };
        banner.Controls.AddRange(new Control[] { title, sub });
        banner.Resize += (_, _) =>
        {
            title.Width = Math.Max(200, banner.ClientSize.Width - 32);
            sub.Width = Math.Max(200, banner.ClientSize.Width - 32);
        };

        // ---- 正文（只读富文本：自动折行 + **粗体** 渲染）----
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            DetectUrls = false,
            BorderStyle = BorderStyle.None,
            BackColor = UiStyle.Card,
            ForeColor = UiStyle.Text,
            Font = UiStyle.MonoFont(9.5f),
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        try
        {
            box.Rtf = RichTextRenderer.BuildRtf(warningText);
        }
        catch (Exception)
        {
            box.Text = RichTextRenderer.StripMarkdown(warningText);
        }

        // ---- 勾选 + 按钮 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = UiStyle.Card };
        _chkAcknowledge = new CheckBox
        {
            AutoSize = false,
            Location = new Point(16, 8),
            Size = new Size(840, 22),
            Text = $"我已确认 {_sourceText} 里的文件不再需要，或已自行备份到其它位置并能正常打开",
            Font = UiStyle.Font(9.5f, bold: true),
            ForeColor = UiStyle.Text,
        };
        _chkAcknowledge.CheckedChanged += (_, _) => UpdateButtons();

        // 取消放在左侧且设为默认按钮：手比脑快时，回车会取消而不是删除
        var btnCancel = UiStyle.MakeButton("取消（我要先备份）", ButtonVariant.Primary, 168, () =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        });
        _btnDelete = UiStyle.MakeButton($"删除 {_sourceText} 并扩容 {_targetText}", ButtonVariant.Danger, 236, () =>
        {
            if (!_chkAcknowledge.Checked) return;   // 双保险：未勾选时按钮点不动
            DialogResult = DialogResult.OK;
            Close();
        });
        _btnDelete.Enabled = false;

        var lblHint = new Label
        {
            AutoSize = false,
            Location = new Point(16, 66),
            Size = new Size(840, 18),
            Text = "提示：删除分区不可逆。若还想保留其中任何文件，请点「取消」，复制出去之后再回来。",
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };

        bottom.Controls.AddRange(new Control[] { _chkAcknowledge, btnCancel, _btnDelete, lblHint });
        bottom.Resize += (_, _) =>
        {
            btnCancel.Location = new Point(16, 32);
            _btnDelete.Location = new Point(bottom.ClientSize.Width - _btnDelete.Width - 16, 32);
            _chkAcknowledge.Width = Math.Max(240, bottom.ClientSize.Width - 32);
            lblHint.Width = Math.Max(240, bottom.ClientSize.Width - 32);
        };

        AcceptButton = btnCancel;      // 回车 = 取消
        CancelButton = btnCancel;      // Esc = 取消

        Controls.Add(box);
        Controls.Add(bottom);
        Controls.Add(banner);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _btnDelete.Enabled = _chkAcknowledge.Checked;
        _btnDelete.Text = _chkAcknowledge.Checked
            ? $"删除 {_sourceText} 并扩容 {_targetText}"
            : $"请先勾选上面的确认";
    }
}
