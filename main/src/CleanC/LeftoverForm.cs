using CleanC.Core;
using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 残留清理对话框：列出扫描到的卸载残留候选（默认全部不勾选），
/// 勾选候选后点击「删除选中项」，删除前还会弹出一次确认框（在调用方）。
/// 危险样式：红色警示条 + 明确后果文案；无官方卸载程序的应用额外提示“这是强制删除路径”。
/// </summary>
public sealed class LeftoverForm : Form
{
    private const int AutoColIndex = 1; // “目标”列自动填满

    private readonly InstalledApp _app;
    private readonly IReadOnlyList<LeftoverCandidate> _candidates;

    private ListView _listView = null!;
    private RoundedButton _btnSelectAll = null!;
    private RoundedButton _btnSelectNone = null!;
    private RoundedButton _btnDelete = null!;
    private RoundedButton _btnSkip = null!;
    private Label _lblCount = null!;

    /// <summary>用户勾选的候选（DialogResult.OK 时有效）。</summary>
    public List<LeftoverCandidate> Selected { get; } = new();

    public LeftoverForm(InstalledApp app, IReadOnlyList<LeftoverCandidate> candidates)
    {
        _app = app;
        _candidates = candidates;

        Text = "残留清理";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(960, 560), new Size(880, 480));

        InitializeUi();
    }

    private void InitializeUi()
    {
        // ================= 顶部横幅 =================
        var banner = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = UiStyle.Card };
        var lblTitle = new Label
        {
            AutoSize = true,
            Location = new Point(18, 10),
            Text = $"清理残留 — {_app.DisplayName}",
            Font = UiStyle.Font(13f, bold: true),
            ForeColor = UiStyle.Danger
        };
        var lblHint = new Label
        {
            AutoSize = true,
            Location = new Point(18, 40),
            Text = "以下为官方卸载后仍存在的确定性匹配候选（目录/注册表/快捷方式）；默认不勾选，仅处理你逐项核对过的内容。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _lblCount = new Label
        {
            AutoSize = true,
            Location = new Point(18, 62),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.Primary
        };
        banner.Controls.AddRange(new Control[] { lblTitle, lblHint, _lblCount });

        // ================= 危险警示条 =================
        var warnStrip = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = UiStyle.WarningBg
        };
        var lblWarn = new Label
        {
            Dock = DockStyle.Fill,
            Text = "删除不可撤销，且不会扫描未列出的引用；请核对每条「风险提示」。"
                 + (_app.IsDeep && !_app.IsUwp ? " 本应用没有官方卸载程序：下方删除即为强制删除路径。" : string.Empty),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.WarningText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18, 0, 18, 0)
        };
        warnStrip.Controls.Add(lblWarn);

        // ================= 列表 =================
        _listView = new ListView();
        UiStyle.StyleListView(_listView);
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.CheckBoxes = true;
        _listView.Columns.Add("类别", 78);
        _listView.Columns.Add("目标", 330);
        _listView.Columns.Add("占用", 88, HorizontalAlignment.Right);
        _listView.Columns.Add("匹配规则", 150);
        _listView.Columns.Add("风险提示", 200);
        foreach (var c in _candidates)
        {
            var lvi = new ListViewItem(c.KindText) { Tag = c, Checked = false };
            lvi.SubItems.Add(c.Target);
            lvi.SubItems.Add(c.SizeText);
            lvi.SubItems.Add(c.MatchedRule);
            lvi.SubItems.Add(c.Risk);
            _listView.Items.Add(lvi);
        }
        _lblCount.Text = $"共 {_candidates.Count} 项候选（已勾选 0 项）";
        _listView.ItemChecked += (_, _) => UpdateState();
        _listView.DoubleClick += (_, _) =>
        {
            if (_listView.SelectedItems.Count > 0)
            {
                var lvi = _listView.SelectedItems[0];
                lvi.Checked = !lvi.Checked;
            }
        };
        _listView.Resize += (_, _) => AutoFitColumns();

        // ================= 底部 =================
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(10, 8, 10, 8), BackColor = UiStyle.Card };
        var lblGate = new Label
        {
            AutoSize = true,
            Location = new Point(4, 8),
            Text = "勾选后点击「删除选中项」（默认不勾选，逐项核对过的才勾选）；删除前还会弹出一次确认框。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _btnDelete = UiStyle.MakeButton("删除选中项", ButtonVariant.Danger, 116, () =>
        {
            Selected.Clear();
            foreach (ListViewItem lvi in _listView.CheckedItems)
                if (lvi.Tag is LeftoverCandidate c)
                    Selected.Add(c);
            DialogResult = DialogResult.OK;
            Close();
        });
        _btnDelete.Enabled = false;
        _btnSkip = UiStyle.MakeButton("跳过", ButtonVariant.Quiet, 80, () =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        });
        _btnSelectAll = UiStyle.MakeSmallButton("全选", ButtonVariant.Secondary, 70, () => SetAllChecked(true));
        _btnSelectNone = UiStyle.MakeSmallButton("全不选", ButtonVariant.Secondary, 70, () => SetAllChecked(false));
        _btnDelete.Location = new Point(560, 20);
        _btnSkip.Location = new Point(684, 20);
        _btnSelectAll.Location = new Point(4, 40);
        _btnSelectNone.Location = new Point(80, 40);
        bottom.Controls.AddRange(new Control[] { lblGate, _btnDelete, _btnSkip, _btnSelectAll, _btnSelectNone });

        Controls.Add(_listView);
        Controls.Add(bottom);
        Controls.Add(warnStrip);
        Controls.Add(banner);

        AutoFitColumns();
        UpdateState();
    }

    private void AutoFitColumns()
    {
        if (_listView.Columns.Count == 0) return;
        int reserved = 26;
        int fixedSum = 0;
        for (int i = 0; i < _listView.Columns.Count; i++)
            if (i != AutoColIndex)
                fixedSum += _listView.Columns[i].Width;
        int auto = Math.Max(240, _listView.ClientSize.Width - fixedSum - reserved);
        _listView.Columns[AutoColIndex].Width = auto;
    }

    private void SetAllChecked(bool check)
    {
        foreach (ListViewItem lvi in _listView.Items)
            lvi.Checked = check;
    }

    private void UpdateState()
    {
        int n = 0;
        long total = 0;
        foreach (ListViewItem lvi in _listView.CheckedItems)
        {
            n++;
            if (lvi.Tag is LeftoverCandidate { SizeBytes: { } size }) total += size;
        }
        _lblCount.Text = $"共 {_candidates.Count} 项候选（已勾选 {n} 项" +
                         (n > 0 ? $"，预计释放约 {SizeFormatter.Format(total)}" : string.Empty) + "）";
        _btnDelete.Enabled = n > 0;
    }
}
