using CleanC.Core;
using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 操作日志查看器：展示 %LOCALAPPDATA%\CleanC\operations.log 最近记录，
/// 支持刷新、导出副本（另存为）、清空（需确认）。
/// </summary>
public sealed class LogViewerForm : Form
{
    private RichTextBox _log = null!;
    private Label _lblPath = null!;
    private RoundedButton _btnRefresh = null!;
    private RoundedButton _btnExport = null!;
    private RoundedButton _btnClear = null!;
    private RoundedButton _btnClose = null!;

    public LogViewerForm()
    {
        Text = "操作日志";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(920, 600), new Size(760, 480));

        InitializeUi();
        Reload();
    }

    private void InitializeUi()
    {
        // ---- 顶部 ----
        var banner = UiStyle.CardPanel(DockStyle.Top);
        banner.Height = 92;
        var lblTitle = new Label
        {
            AutoSize = true,
            Location = new Point(18, 12),
            Text = "操作日志",
            Font = UiStyle.Font(14f, bold: true),
            ForeColor = UiStyle.Text
        };
        _lblPath = new Label
        {
            AutoSize = true,
            Location = new Point(18, 44),
            Text = "记录文件：" + OperationLog.CurrentPath,
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText
        };
        _btnRefresh = UiStyle.MakeButton("刷新", ButtonVariant.Secondary, 84, Reload);
        _btnRefresh.Location = new Point(18, 70);
        _btnExport = UiStyle.MakeButton("导出副本…", ButtonVariant.Secondary, 104, OnExport);
        _btnExport.Location = new Point(110, 70);
        _btnClear = UiStyle.MakeButton("清空日志", ButtonVariant.Danger, 96, OnClear);
        _btnClear.Location = new Point(222, 70);
        _btnClose = UiStyle.MakeButton("关闭", ButtonVariant.Quiet, 80, () => Close());
        _btnClose.Location = new Point(0, 12);
        banner.Controls.AddRange(new Control[] { lblTitle, _lblPath, _btnRefresh, _btnExport, _btnClear, _btnClose });
        banner.Resize += (_, _) =>
        {
            _btnClose.Left = banner.ClientSize.Width - _btnClose.Width - 18;
            _btnClose.Top = 12;
        };

        // ---- 内容 ----
        _log = new RichTextBox();
        UiStyle.StyleLog(_log);
        _log.Dock = DockStyle.Fill;
        _log.WordWrap = false;

        Controls.Add(_log);
        Controls.Add(banner);
    }

    private void Reload()
    {
        string text = OperationLog.ReadTail();
        _log.Text = string.IsNullOrEmpty(text) ? "（暂无操作记录）" : text;
        _log.SelectionStart = 0;
        _log.ScrollToCaret();
        _lblPath.Text = "记录文件：" + OperationLog.CurrentPath;
    }

    private void OnExport()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出操作日志副本",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"CleanC-操作日志-{TimeText.CompactNow()}.txt",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // 优先直接复制日志文件（含全部历史）；不可读时回退为当前视图内容
            if (System.IO.File.Exists(OperationLog.CurrentPath))
            {
                System.IO.File.Copy(OperationLog.CurrentPath, dlg.FileName, overwrite: true);
            }
            else
            {
                // 带 BOM 的 UTF-8：与说明书/CSV/JSON 导出口径一致，
                // 在默认 GBK 的记事本/编辑器里打开中文不乱码。
                System.IO.File.WriteAllText(dlg.FileName, _log.Text,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            MessageBox.Show(this, $"已导出：{dlg.FileName}", "导出完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnClear()
    {
        var r = MessageBox.Show(this,
            "清空后日志不可恢复，将清除所有操作记录。\n确定清空？",
            "清空操作日志", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        if (OperationLog.Clear())
        {
            Reload();
            MessageBox.Show(this, "操作日志已清空。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, "清空失败（文件可能被占用或无权限）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
