using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 通用只读文本查看对话框：用于展示较长的说明文字（补救清单、风险说明、恢复指引等）。
/// 提供「复制全文」与「关闭」。不修改任何内容。
/// </summary>
public sealed class TextViewForm : Form
{
    private readonly string _text;

    public TextViewForm(string title, string text, string? subtitle = null)
    {
        _text = text ?? string.Empty;

        Text = title;
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(920, 720), new Size(640, 400), margin: 120);

        InitializeUi(subtitle);
    }

    private void InitializeUi(string? subtitle)
    {
        int bannerHeight = string.IsNullOrWhiteSpace(subtitle) ? 48 : 70;
        var banner = UiStyle.CardPanel(DockStyle.Top);
        banner.Height = bannerHeight;

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(16, 14),
            Text = Text,
            Font = UiStyle.Font(12.5f, bold: true),
            ForeColor = UiStyle.Text,
        };
        banner.Controls.Add(title);

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var sub = new Label
            {
                AutoSize = true,
                Location = new Point(16, 42),
                Text = subtitle,
                Font = UiStyle.Font(9f),
                ForeColor = UiStyle.SubText,
            };
            banner.Controls.Add(sub);
        }

        // 只读正文：自动换行（长段落不会再横向溢出），并用 RTF 渲染 **粗体** 标记
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            DetectUrls = false,
            BorderStyle = BorderStyle.None,
            BackColor = UiStyle.LogBg,
            ForeColor = UiStyle.Text,
            Font = UiStyle.MonoFont(9.5f),
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        ApplyText(box, _text);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiStyle.Card };
        var btnCopy = UiStyle.MakeButton("复制全文", ButtonVariant.Secondary, 96, () => CopyAll());
        var btnClose = UiStyle.MakeButton("关闭", ButtonVariant.Primary, 88, () => Close());
        var lblHint = new Label
        {
            AutoSize = true,
            Location = new Point(16, 18),
            Text = "本窗口仅展示说明文字，不会执行任何命令。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
        };
        btnCopy.Location = new Point(0, 10);
        btnClose.Location = new Point(0, 10);
        bottom.Controls.AddRange(new Control[] { lblHint, btnCopy, btnClose });
        bottom.Resize += (_, _) =>
        {
            btnClose.Left = bottom.ClientSize.Width - btnClose.Width - 16;
            btnCopy.Left = btnClose.Left - btnCopy.Width - 8;
        };

        Controls.Add(box);
        Controls.Add(bottom);
        Controls.Add(banner);
    }

    /// <summary>
    /// 填入正文：优先用 RTF（把 **粗体** 渲染成真正的粗体）；RTF 失败时退回纯文本（并去掉标记），
    /// 保证任何情况下都不会把星号露给用户。
    /// </summary>
    private static void ApplyText(RichTextBox box, string text)
    {
        try
        {
            box.Rtf = Core.RichTextRenderer.BuildRtf(text);
        }
        catch (Exception)
        {
            box.Text = Core.RichTextRenderer.StripMarkdown(text);
        }
    }

    private void CopyAll()
    {
        try
        {
            // 剪贴板给纯文本：去掉粗体标记，避免粘贴出去出现一堆星号
            Clipboard.SetText(Core.RichTextRenderer.StripMarkdown(_text));
            MessageBox.Show(this, "全文已复制到剪贴板。", "已复制", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
