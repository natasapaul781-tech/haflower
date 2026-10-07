using CleanC.Core;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 磁盘分区窗口（分盘 / 合盘）。
///
/// 界面原则（刻意保持精简）：**主窗口只放"做决定"需要的东西**——
/// 磁盘条带图、目标分区选择、一行推荐摘要、滑块、一行校验结论、计划步骤标题。
/// 其余明细（推荐值构成、校验/阻断原因、环境检查、磁盘与分区原始值、会话日志、执行报告）
/// 全部通过按钮打开弹窗查看，避免主窗口堆成一堵墙。
///
/// 安全约定：只读检测；默认只产出《操作说明书》；破坏性动作需显式勾选确认；
/// 只把「基本数据分区」列为可操作对象；缺少管理员权限时禁用全部操作并说明原因。
/// </summary>
public sealed class DiskForm : Form
{
    private const long Gb = 1024L * 1024 * 1024;
    private const int MaxSessionLogLines = 2000;

    // ---- 数据 ----
    private DiskProbeResult? _probe;
    private int _selectedDisk = -1;
    private SupportedSize? _splitSupported;
    private RecommendResult? _recommend;
    private SupportedSize? _mergeTargetSupported;

    private PartitionPlan? _splitPlan;
    private PartitionPlan? _mergePlan;
    private IReadOnlyList<BlockReason> _splitBlocks = Array.Empty<BlockReason>();
    private IReadOnlyList<BlockReason> _mergeBlocks = Array.Empty<BlockReason>();

    private PartitionImpact? _mergeImpact;
    private PartitionInfo? _impactForPartition;

    private string? _lastManualPath;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _syncingSlider;
    private readonly List<string> _sessionLog = new();

    /// <summary>处理中遮罩 + 危险操作期间的关闭闸门（统一由 UiBusyHost 管理）。</summary>
    private UiBusyHost _busyHost = null!;
    // ---- 顶部 / 警示 ----
    private Panel _banner = null!;
    private Label _lblSummary = null!;
    private RoundedButton _btnRefresh = null!;
    private RoundedButton _btnDiskDetail = null!;
    private RoundedButton _btnEnv = null!;
    private RoundedButton _btnBack = null!;
    private CheckBox _chkAck = null!;

    // ---- 磁盘行 / 条带图 / 标签页 ----
    private ComboBox _cmbDisk = null!;
    private Label _lblDiskInfo = null!;
    private RoundedButton _btnNewPartition = null!;
    private DiskStrip _strip = null!;
    private TabControl _tabs = null!;
    private TabPage _splitPage = null!;
    private TabPage _mergePage = null!;

    // ---- 分盘页 ----
    private ComboBox _cmbSplitSource = null!;
    private RoundedButton _btnSplitAnalyze = null!;
    private RoundedButton _btnWhyNot = null!;
    private Label _lblRecommend = null!;
    private RoundedButton _btnRecommend = null!;
    private TrackBar _slider = null!;
    private Label _lblKeepSplit = null!;
    private Label _lblExplain = null!;
    private ComboBox _cmbNewLetter = null!;
    private TextBox _txtNewLabel = null!;
    private Label _lblSplitBlocks = null!;
    private RoundedButton _btnSplitBlocks = null!;
    private ListView _lstSplitSteps = null!;

    // ---- 合盘页 ----
    private ComboBox _cmbMergeSource = null!;
    private ComboBox _cmbMergeTarget = null!;
    private RoundedButton _btnMergeAnalyze = null!;
    private Label _lblMergeBlocks = null!;
    private RoundedButton _btnMergeBlocks = null!;
    private Label _lblImpact = null!;
    private RoundedButton _btnImpactDetail = null!;
    private ListView _lstMergeSteps = null!;

    // ---- 底部 ----
    private RoundedButton _btnManual = null!;
    private RoundedButton _btnSessionLog = null!;
    private RoundedButton _btnElevate = null!;
    private CheckBox _chkConfirmRun = null!;

    /// <summary>
    /// 是否需要显示「以管理员身份重新启动」按钮。
    /// 用独立字段而不是读 <c>_btnElevate.Visible</c>：WinForms 的 Visible **getter** 还要求父链可见，
    /// 窗体尚未 Show 时一律返回 false，会让首次布局漏掉这个按钮（实测被布局检查抓到重叠）。
    /// </summary>
    private readonly bool _needElevate = !ElevationHelper.IsElevated();

    private RoundedButton _btnExecute = null!;
    private Label _lblGate = null!;
    private ProgressBar _progressBar = null!;
    private RoundedButton _btnCancel = null!;

    public DiskForm()
    {
        Text = "磁盘分区（分盘 / 合盘）";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(1120, 800), new Size(900, 620), margin: 60);

        InitializeUi();
        UpdateGate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutBanner();
        LayoutDiskRow();
        if (SuppressAutoProbe) return;   // 布局检查工具会注入合成数据，别再跑真实探测覆盖它
        _ = ReloadAsync();
    }

    // 关闭闸门（危险操作期间拒绝关闭）已由 UiBusyHost 统一挂载到本窗体的 FormClosing。

    /// <summary>
    /// 测试/布局检查专用：抑制构造后自动跑的真实探测。
    /// 布局检查需要在"已填充数据"的状态下测量控件位置，真实探测在本机非提权时只会得到空数据。
    /// </summary>
    internal bool SuppressAutoProbe { get; set; }

    /// <summary>测试/布局检查专用：直接注入一份探测结果（走与真实探测完全相同的应用路径）。</summary>
    internal void ApplyProbeForTest(DiskProbeResult probe)
    {
        _probe = probe;
        ApplyProbe(probe);
    }

    /// <summary>
    /// 测试/布局检查专用：注入"已分析"状态（走真实推荐值计算与滑块刷新路径）。
    /// 空状态的布局看不出问题——真正会挤在一起的往往是
    /// "建议保留 ≥ 198 GB（下限 148 GB）｜本机最多可分出 126 GB｜新分区最小建议 50 GB" 这类长文案。
    /// </summary>
    internal void SetSplitAnalyzedForTest(SupportedSize supported)
    {
        _splitSupported = supported;
        _recommend = null;
        RefreshSplitState();
    }

    /// <summary>测试/布局检查专用：注入"已盘点"状态，检查合盘页长文案下的布局。</summary>
    internal void SetMergeImpactForTest(PartitionImpact impact)
    {
        var source = SelectedPartition(_cmbMergeSource);
        _mergeImpact = impact;
        _impactForPartition = source;
        RefreshImpactLabel();
        RefreshMergeState();
    }

    /// <summary>测试/布局检查专用：直接读当前计划（供断言）。</summary>
    internal PartitionPlan? MergePlanForTest => _mergePlan;

    /// <summary>测试/布局检查专用：设置新分区盘符与卷标，让分盘计划真正生成（步骤列表被填充）。</summary>
    internal void SetNewPartitionForTest(char letter, string label)
    {
        for (int i = 0; i < _cmbNewLetter.Items.Count; i++)
        {
            if (_cmbNewLetter.Items[i] is string s && s.Length > 0 && s[0] == letter)
            {
                _cmbNewLetter.SelectedIndex = i;
                break;
            }
        }
        _txtNewLabel.Text = label;
        RebuildSplitPlan();
    }

    /// <summary>测试/布局检查专用：切换标签页并完成布局。</summary>
    internal void SelectTabForTest(int index)
    {
        if (_tabs is null || index < 0 || index >= _tabs.TabPages.Count) return;
        _tabs.SelectedIndex = index;
    }

    // ==================== UI 构建 ====================

    private void InitializeUi()
    {
        BuildBanner();
        var warnStrip = BuildWarnStrip();
        var diskRow = BuildDiskRow();
        BuildStrip();
        _tabs = BuildTabs();
        var bottom = BuildBottom();

        Controls.Add(_tabs);
        Controls.Add(bottom);
        Controls.Add(_strip);
        Controls.Add(diskRow);
        Controls.Add(warnStrip);
        Controls.Add(_banner);

        // 处理中遮罩 + 危险操作期间拒绝关闭（统一由 UiBusyHost 管理，见该类注释）
        _busyHost = new UiBusyHost(this);
    }

    private void BuildBanner()
    {
        _banner = UiStyle.CardPanel(DockStyle.Top);
        _banner.Height = 66;

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(14, 8),
            Text = "磁盘分区（分盘 / 合盘）",
            Font = UiStyle.Font(13.5f, bold: true),
            ForeColor = UiStyle.Text,
        };
        _lblSummary = new Label
        {
            AutoSize = false,
            Location = new Point(14, 34),
            Size = new Size(600, 20),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.Primary,
            Text = "正在检测…",
            AutoEllipsis = true,
        };

        _btnRefresh = UiStyle.MakeButton("重新检测", ButtonVariant.Secondary, 96, async () => await ReloadAsync());
        _btnDiskDetail = UiStyle.MakeButton("磁盘明细…", ButtonVariant.Secondary, 104, OnShowDiskDetail);
        _btnEnv = UiStyle.MakeButton("环境检查…", ButtonVariant.Secondary, 104, OnShowEnvironment);
        _btnBack = UiStyle.MakeButton("返回", ButtonVariant.Quiet, 76, () => Close());

        _banner.Controls.AddRange(new Control[]
        {
            title, _lblSummary, _btnRefresh, _btnDiskDetail, _btnEnv, _btnBack,
        });
        _banner.Resize += (_, _) => LayoutBanner();
    }

    private void LayoutBanner()
    {
        const int top = 16;
        int w = _banner.ClientSize.Width;
        _btnBack.Location = new Point(w - _btnBack.Width - 14, top);
        _btnEnv.Location = new Point(_btnBack.Left - _btnEnv.Width - 8, top);
        _btnDiskDetail.Location = new Point(_btnEnv.Left - _btnDiskDetail.Width - 8, top);
        _btnRefresh.Location = new Point(_btnDiskDetail.Left - _btnRefresh.Width - 8, top);
        // 摘要行不要压到按钮
        _lblSummary.Width = Math.Max(200, _btnRefresh.Left - _lblSummary.Left - 12);
    }

    private Panel BuildWarnStrip()
    {
        var strip = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = UiStyle.WarningBg };

        var lbl = new Label
        {
            AutoSize = false,
            Location = new Point(16, 4),
            Size = new Size(900, 17),
            Text = "分区操作不可逆：断电或强行中断可能导致数据丢失甚至无法开机。请先备份、关掉正在运行的程序与杀毒扫描、笔记本接电源。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.WarningText,
            AutoEllipsis = true,
        };
        _chkAck = new CheckBox
        {
            AutoSize = true,
            Location = new Point(16, 21),
            Text = "我已备份重要数据，并理解分区操作的风险（本工具不承担数据丢失责任）",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.WarningText,
            BackColor = Color.Transparent,
        };
        _chkAck.CheckedChanged += (_, _) => UpdateGate();

        strip.Controls.AddRange(new Control[] { lbl, _chkAck });
        strip.Resize += (_, _) => lbl.Width = Math.Max(300, strip.ClientSize.Width - 32);
        return strip;
    }

    private Panel BuildDiskRow()
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = UiStyle.Card };

        var lbl = new Label { AutoSize = true, Location = new Point(14, 8), Text = "磁盘：", ForeColor = UiStyle.Text };
        _cmbDisk = new ComboBox
        {
            Location = new Point(58, 4),
            Width = 430,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbDisk.SelectedIndexChanged += (_, _) => OnDiskChanged();
        _lblDiskInfo = new Label
        {
            AutoSize = false,
            Location = new Point(500, 8),
            Size = new Size(400, 18),
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };

        // 「用未分配空间新建分区」：既是常规操作，也是"上一个操作只做了一半"的修复入口。
        // 这一行高度只有 32，所以用 26 高的小按钮（普通按钮 32 高会超出这一行被裁切）；
        // 宽度按最长的文案「用未分配空间新建分区…（146.5 GB）」留足，避免被省略号截掉数字。
        _btnNewPartition = UiStyle.MakeButton("用未分配空间新建分区…", ButtonVariant.Secondary, 236,
            async () => await OnNewPartition(), height: 26);
        _btnNewPartition.Location = new Point(760, 3);
        _btnNewPartition.Enabled = false;

        row.Controls.AddRange(new Control[] { lbl, _cmbDisk, _lblDiskInfo, _btnNewPartition });
        row.Resize += (_, _) => LayoutDiskRow();
        return row;
    }

    private void LayoutDiskRow()
    {
        int w = _cmbDisk.Parent?.ClientSize.Width ?? 1000;
        _btnNewPartition.Left = Math.Max(590, w - _btnNewPartition.Width - 16);
        _btnNewPartition.Top = 3;                       // 行高 32、按钮高 26，必须显式摆正
        _cmbDisk.Width = Math.Max(220, Math.Min(430, _btnNewPartition.Left - 340));
        _lblDiskInfo.Left = _cmbDisk.Right + 12;
        _lblDiskInfo.Width = Math.Max(100, _btnNewPartition.Left - _lblDiskInfo.Left - 12);
    }
    private void BuildStrip()
    {
        _strip = new DiskStrip { Dock = DockStyle.Top, Height = 78, BackColor = UiStyle.Card };
        _strip.PartitionClicked += (_, p) => OnStripPartitionClicked(p);
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = UiStyle.Font(9f),
            Padding = new Point(14, 5),
        };
        tabs.TabPages.Add(BuildSplitPage());
        tabs.TabPages.Add(BuildMergePage());
        tabs.SelectedIndexChanged += (_, _) => OnTabChanged();
        return tabs;
    }

    // ---------- 分盘页（固定 Y，只有宽度自适应） ----------

    private TabPage BuildSplitPage()
    {
        _splitPage = new TabPage("分盘（把 C 盘分出 D 盘）")
        {
            BackColor = UiStyle.Bg,
            Padding = new Padding(10),
            AutoScroll = true,
            AutoScrollMinSize = new Size(0, 402),
        };
        var page = _splitPage;

        var lblSrc = new Label { AutoSize = true, Location = new Point(12, 16), Text = "要分盘的卷：", ForeColor = UiStyle.Text };
        _cmbSplitSource = new ComboBox
        {
            Location = new Point(100, 12),
            Width = 330,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbSplitSource.SelectedIndexChanged += (_, _) =>
        {
            _recommend = null;
            _splitSupported = null;
            _splitPlan = null;
            RefreshSplitState();
        };
        _btnSplitAnalyze = UiStyle.MakeButton("分析可压缩空间", ButtonVariant.Primary, 148, async () => await AnalyzeSplitAsync());
        _btnSplitAnalyze.Location = new Point(442, 11);
        _btnWhyNot = UiStyle.MakeButton("为什么分不出空间？", ButtonVariant.Secondary, 152, OnShowWhyNotShrink);
        _btnWhyNot.Location = new Point(598, 11);

        _lblRecommend = new Label
        {
            AutoSize = false,
            Location = new Point(12, 56),
            Size = new Size(700, 20),
            Font = UiStyle.Font(9.5f, bold: true),
            ForeColor = UiStyle.Text,
            AutoEllipsis = true,
        };
        // 与 _lblRecommend 同处一条"带"（48..80），靠左右分栏避免相交：按钮右对齐、标签占左侧
        _btnRecommend = UiStyle.MakeButton("推荐明细…", ButtonVariant.Secondary, 96, OnShowRecommendDetail);
        _btnRecommend.Enabled = false;

        _slider = new TrackBar
        {
            Location = new Point(10, 90),
            Width = 900,
            Height = 40,
            TickStyle = TickStyle.BottomRight,
            SmallChange = 1,
            LargeChange = 10,
            Enabled = false,
        };
        _slider.Scroll += (_, _) => OnSliderChanged();

        _lblKeepSplit = new Label
        {
            AutoSize = false,
            Location = new Point(12, 142),
            Size = new Size(700, 20),
            Font = UiStyle.Font(9.5f, bold: true),
            ForeColor = UiStyle.Primary,
        };
        _lblExplain = new Label
        {
            AutoSize = false,
            Location = new Point(12, 164),
            Size = new Size(900, 20),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };

        var lblLetter = new Label { AutoSize = true, Location = new Point(12, 196), Text = "新分区盘符：", ForeColor = UiStyle.Text };
        _cmbNewLetter = new ComboBox
        {
            Location = new Point(100, 192),
            Width = 70,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbNewLetter.SelectedIndexChanged += (_, _) => RebuildSplitPlan();
        var lblLabel = new Label { AutoSize = true, Location = new Point(190, 196), Text = "卷标：", ForeColor = UiStyle.Text };
        _txtNewLabel = new TextBox
        {
            Location = new Point(236, 192),
            Width = 180,
            Font = UiStyle.Font(9f),
            Text = PartitionPlanBuilder.DefaultNewVolumeLabel,
        };
        _txtNewLabel.TextChanged += (_, _) => RebuildSplitPlan();

        _lblSplitBlocks = new Label
        {
            AutoSize = false,
            Location = new Point(12, 236),
            Size = new Size(700, 20),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };
        _btnSplitBlocks = UiStyle.MakeButton("查看校验…", ButtonVariant.Secondary, 100, () => ShowBlocksDialog(_splitBlocks, "分盘校验结果"));
        _btnSplitBlocks.Enabled = false;

        _lstSplitSteps = BuildStepsList(270);
        _lstSplitSteps.DoubleClick += (_, _) => ShowStepDetail(_splitPlan, _lstSplitSteps);

        page.Controls.AddRange(new Control[]
        {
            lblSrc, _cmbSplitSource, _btnSplitAnalyze, _btnWhyNot,
            _lblRecommend, _btnRecommend, _slider, _lblKeepSplit, _lblExplain,
            lblLetter, _cmbNewLetter, lblLabel, _txtNewLabel,
            _lblSplitBlocks, _btnSplitBlocks, _lstSplitSteps,
        });
        page.Resize += (_, _) => LayoutSplitPage();
        return page;
    }

    private ListView BuildStepsList(int top)
    {
        var lv = new ListView { Location = new Point(12, top), Size = new Size(900, 118) };
        UiStyle.StyleListView(lv);
        lv.View = View.Details;
        lv.Columns.Add("计划步骤", 760);
        lv.Columns.Add("标记", 110);
        lv.Resize += (_, _) =>
        {
            if (lv.Columns.Count == 0) return;
            lv.Columns[0].Width = Math.Max(240, lv.ClientSize.Width - 138);
            lv.Columns[1].Width = 118;
        };
        return lv;
    }

    private void LayoutSplitPage()
    {
        if (_splitPage is null) return;
        int avail = _splitPage.ClientSize.Width - _splitPage.Padding.Horizontal;
        int inner = Math.Max(620, avail - 24);

        _cmbSplitSource.Width = Math.Max(220, Math.Min(360, avail - 660));
        _btnSplitAnalyze.Left = _cmbSplitSource.Right + 12;
        _btnWhyNot.Left = _btnSplitAnalyze.Right + 8;

        _btnRecommend.Left = 12 + inner - _btnRecommend.Width;
        _btnRecommend.Top = 48;                       // 与 _lblRecommend(56..76) 同带，靠左右分栏避让
        _lblRecommend.Width = Math.Max(240, _btnRecommend.Left - _lblRecommend.Left - 10);

        _slider.Width = inner;
        _lblKeepSplit.Width = inner;
        _lblExplain.Width = inner;

        _btnSplitBlocks.Left = 12 + inner - _btnSplitBlocks.Width;
        _btnSplitBlocks.Top = 228;                    // 必须显式设置：否则会停在 y=0，压到标签页标题栏上
        _lblSplitBlocks.Width = Math.Max(240, _btnSplitBlocks.Left - _lblSplitBlocks.Left - 10);

        _lstSplitSteps.Width = inner;
    }

    // ---------- 合盘页 ----------

    private TabPage BuildMergePage()
    {
        _mergePage = new TabPage("合盘（把 D 盘并回 C 盘）")
        {
            BackColor = UiStyle.Bg,
            Padding = new Padding(10),
            AutoScroll = true,
            AutoScrollMinSize = new Size(0, 342),
        };
        var page = _mergePage;

        var lblSrc = new Label { AutoSize = true, Location = new Point(12, 16), Text = "要合并掉的分区：", ForeColor = UiStyle.Text };
        _cmbMergeSource = new ComboBox
        {
            Location = new Point(132, 12),
            Width = 250,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbMergeSource.SelectedIndexChanged += (_, _) => OnMergeSelectionChanged();

        var lblTgt = new Label { AutoSize = true, Location = new Point(396, 16), Text = "要变大的分区：", ForeColor = UiStyle.Text };
        _cmbMergeTarget = new ComboBox
        {
            Location = new Point(504, 12),
            Width = 250,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbMergeTarget.SelectedIndexChanged += (_, _) => OnMergeSelectionChanged();

        _btnMergeAnalyze = UiStyle.MakeButton("分析可行性", ButtonVariant.Primary, 120, async () => await AnalyzeMergeAsync());

        _lblMergeBlocks = new Label
        {
            AutoSize = false,
            Location = new Point(12, 56),
            Size = new Size(700, 20),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };
        _btnMergeBlocks = UiStyle.MakeButton("查看校验…", ButtonVariant.Secondary, 100, () => ShowBlocksDialog(_mergeBlocks, "合盘校验结果"));
        _btnMergeBlocks.Enabled = false;

        // ---- 删除前的影响提示（只读盘点结果；工具不替用户备份，所以必须把规模摆出来）----
        _lblImpact = new Label
        {
            AutoSize = false,
            Location = new Point(12, 92),
            Size = new Size(700, 20),
            Font = UiStyle.Font(9f, bold: true),
            ForeColor = UiStyle.WarningText,
            AutoEllipsis = true,
            Text = "点「删除并扩容…」时会先盘点该分区里将被永久删除的内容（只读），并列出文件数与主要目录。",
        };
        _btnImpactDetail = UiStyle.MakeButton("查看将被删除的内容…", ButtonVariant.Secondary, 168, OnShowImpactDetail);
        _btnImpactDetail.Enabled = false;

        _lstMergeSteps = BuildStepsList(126);
        _lstMergeSteps.Height = 150;
        _lstMergeSteps.DoubleClick += (_, _) => ShowStepDetail(_mergePlan, _lstMergeSteps);

        page.Controls.AddRange(new Control[]
        {
            lblSrc, _cmbMergeSource, lblTgt, _cmbMergeTarget, _btnMergeAnalyze,
            _lblMergeBlocks, _btnMergeBlocks,
            _lblImpact, _btnImpactDetail, _lstMergeSteps,
        });
        page.Resize += (_, _) => LayoutMergePage();
        return page;
    }

    private void LayoutMergePage()
    {
        if (_mergePage is null) return;
        int avail = _mergePage.ClientSize.Width - _mergePage.Padding.Horizontal;
        int inner = Math.Max(620, avail - 24);

        _btnMergeAnalyze.Left = Math.Max(760, avail - _btnMergeAnalyze.Width - 12);
        _btnMergeAnalyze.Top = 11;

        _btnMergeBlocks.Left = 12 + inner - _btnMergeBlocks.Width;
        _btnMergeBlocks.Top = 48;                     // 必须显式设置：否则会停在 y=0，压到标签页标题栏上
        _lblMergeBlocks.Width = Math.Max(240, _btnMergeBlocks.Left - _lblMergeBlocks.Left - 10);

        _btnImpactDetail.Left = 12 + inner - _btnImpactDetail.Width;
        _btnImpactDetail.Top = 90;
        _lblImpact.Width = Math.Max(240, _btnImpactDetail.Left - _lblImpact.Left - 10);

        _lstMergeSteps.Width = inner;
    }

    // ---------- 底部（闸门行 + 状态行） ----------

    private Panel BuildBottom()
    {
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 114, Padding = new Padding(8), BackColor = UiStyle.Card };

        var gateRow = new Panel { Dock = DockStyle.Top, Height = 46 };
        _btnManual = UiStyle.MakeButton("生成说明书", ButtonVariant.Primary, 108, OnWriteManual);
        _btnManual.Enabled = false;
        _btnSessionLog = UiStyle.MakeButton("查看日志…", ButtonVariant.Secondary, 96, OnShowSessionLog);
        // 提权入口：程序默认以普通权限启动，磁盘信息读不到时用户需要一键提升权限
        _btnElevate = UiStyle.MakeButton("以管理员身份重新启动", ButtonVariant.Secondary, 160, OnElevate);
        _btnElevate.Visible = _needElevate;
        _chkConfirmRun = new CheckBox
        {
            AutoSize = true,
            Location = new Point(220, 12),
            Text = "我已阅读说明书，确认由本工具执行上面的步骤",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.Text,
        };
        _chkConfirmRun.CheckedChanged += (_, _) => UpdateGate();
        _btnExecute = UiStyle.MakeButton("开始执行（分盘）", ButtonVariant.Danger, 140, OnExecuteClicked);
        _btnExecute.Enabled = false;
        gateRow.Controls.AddRange(new Control[] { _btnManual, _btnSessionLog, _btnElevate, _chkConfirmRun, _btnExecute });

        // 状态行：文字在上（2..20）、进度条在下（24..42），互不相交
        var statusRow = new Panel { Dock = DockStyle.Top, Height = 52 };
        _lblGate = new Label
        {
            AutoSize = false,
            Location = new Point(12, 2),
            Size = new Size(700, 18),
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };
        _progressBar = new ProgressBar { Location = new Point(12, 24), Width = 600 };
        UiStyle.StyleProgressBar(_progressBar);
        _btnCancel = UiStyle.MakeSmallButton("取消", ButtonVariant.Quiet, 72, () => _cts?.Cancel());
        _btnCancel.Enabled = false;
        statusRow.Controls.AddRange(new Control[] { _lblGate, _progressBar, _btnCancel });

        gateRow.Resize += (_, _) => LayoutGateRow(gateRow);
        statusRow.Resize += (_, _) => LayoutStatusRow(statusRow);
        // 立即布局一次：只靠 Resize 事件会让"控件初始位置"一直保持声明值，
        // 布局检查（以及任何在 Show 之前读 Bounds 的地方）会看到重叠。
        LayoutGateRow(gateRow);
        LayoutStatusRow(statusRow);

        bottom.Controls.Add(statusRow);
        bottom.Controls.Add(gateRow);
        return bottom;
    }

    private void LayoutGateRow(Panel row)
    {
        int w = row.ClientSize.Width;
        _btnExecute.Left = w - _btnExecute.Width - 12;
        _btnExecute.Top = 8;
        _btnSessionLog.Left = _btnManual.Right + 8;
        _btnSessionLog.Top = 8;

        // 提权按钮只在非管理员时出现，插在「查看日志」与风险确认之间
        int next = _btnSessionLog.Right + 8;
        if (_needElevate)
        {
            _btnElevate.Left = next;
            _btnElevate.Top = 8;
            next = _btnElevate.Right + 14;
        }
        _chkConfirmRun.Left = next;
        _chkConfirmRun.Top = 14;
    }

    /// <summary>以管理员身份重新启动（磁盘信息受 ACL 保护，读不到时用这个）。</summary>
    private void OnElevate()
    {
        if (ElevationHelper.TryRestartElevated("--open=disk", out string error))
        {
            Application.Exit();
            return;
        }
        OperationLog.LogSystem("提权未完成：" + error);
        MessageBox.Show(this, error, "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// 「用未分配空间新建分区」：打开独立窗口。
    /// 修的是"上一个操作只完成了一半"的状态——收缩成功但新建失败、删除成功但扩容失败，
    /// 磁盘上都会留下一段未分配空间；以前只能靠脚本或磁盘管理处理，现在界面里就能补上。
    /// </summary>
    private async Task OnNewPartition()
    {
        if (_probe is null)
        {
            MessageBox.Show(this, "还没读到磁盘信息，请先点「重新检测」。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_probe.Layout.IsElevated)
        {
            MessageBox.Show(this, "当前进程没有管理员权限，无法读写分区表。\n\n" +
                                  "请点「以管理员身份重新启动」后再试。", "需要管理员权限",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        long totalFree = _probe.Layout.Disks
            .Sum(d => _probe.Layout.FreeExtentsOfDisk(d.Number).Sum(f => (long)f.SizeBytes));
        if (totalFree < FreeExtent.MinUsefulBytes)
        {
            MessageBox.Show(this,
                "所有磁盘上都没有可用的未分配空间（≥ 1 MB）。\n\n" +
                "如果磁盘管理里明明显示有「未分配」，请点「重新检测」刷新一次。",
                "没有可用的未分配空间", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppendLog("");
        AppendLog($"=== 打开「用未分配空间新建分区」（未分配合计 {SizeFormatter.Format(totalFree)}） ===");

        using var dlg = new NewPartitionForm(_probe);
        var result = dlg.ShowDialog(this);

        if (result == DialogResult.OK && dlg.Report is { } report)
        {
            AppendLog("新建分区完成：");
            foreach (var line in report.ToText().Split('\n')) AppendLog("  " + line.TrimEnd());
            OperationLog.Log("分区-新建", $"完成：{report.Summary}");
            await ReloadAsync();   // 让新盘符立刻出现在下拉框与条带图里
        }
        else
        {
            AppendLog("已取消或未完成「新建分区」，未做任何改动。");
        }
    }

    private void LayoutStatusRow(Panel row)
    {
        int w = row.ClientSize.Width;
        _btnCancel.Left = w - _btnCancel.Width - 12;
        _btnCancel.Top = 16;
        _progressBar.Left = 12;
        _progressBar.Top = 24;
        _progressBar.Width = Math.Max(160, _btnCancel.Left - _progressBar.Left - 12);
        _lblGate.Width = Math.Max(200, _btnCancel.Left - _lblGate.Left - 12);
    }

    // ==================== 只读探测 ====================

    private async Task ReloadAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在检测磁盘（只读，不会做任何改动）...", marquee: true);
        AppendLog("=== 开始只读检测磁盘与分区 ===");

        _cts = new CancellationTokenSource();
        try
        {
            var probe = await DiskLayoutService.ProbeAsync(_cts.Token, msg => Ui(() => AppendLog(msg)));
            _probe = probe;
            ApplyProbe(probe);
            OperationLog.LogPartitionProbe(BuildProbeSummary(probe));
            _lblGate.Text = probe.Layout.IsElevated
                ? $"检测完成：{probe.Layout.Disks.Count} 块磁盘、{probe.Layout.Partitions.Count} 个分区。"
                : "检测完成，但缺少管理员权限：无法读取磁盘与分区信息。";
        }
        catch (OperationCanceledException)
        {
            AppendLog("检测已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("检测失败：" + ex.Message);
            MessageBox.Show(this, "检测失败：\n\n" + ex.Message, "检测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
            UpdateGate();
        }
    }

    private void ApplyProbe(DiskProbeResult probe)
    {
        var layout = probe.Layout;

        // 磁盘下拉（多盘时才有意义，但仍保留控件以便统一切盘）
        _cmbDisk.Items.Clear();
        foreach (var d in layout.Disks.OrderBy(d => d.Number))
            _cmbDisk.Items.Add(new DiskChoice(d.Number, d.ChoiceText));

        int defaultDisk = layout.Disks.FirstOrDefault(d => d.IsBoot)?.Number
                          ?? layout.Disks.FirstOrDefault()?.Number ?? -1;
        _selectedDisk = defaultDisk;
        for (int i = 0; i < _cmbDisk.Items.Count; i++)
            if (_cmbDisk.Items[i] is DiskChoice dc && dc.Number == defaultDisk)
            {
                _cmbDisk.SelectedIndex = i;
                break;
            }

        int total = layout.Partitions.Count(p => p.IsOperable);
        _lblSummary.Text = layout.Disks.Count == 0
            ? $"磁盘信息不可读{(layout.IsElevated ? "（已提权，可能是 WMI 服务异常）" : "：需要管理员权限")}" +
              "　—— 点「磁盘明细…」查看原因"
            : $"{layout.Disks.Count} 块磁盘 · {layout.Partitions.Count} 个分区 · " +
              $"系统盘 {(layout.SystemDriveLetter is { } s ? s + ":" : "未知")} · " +
              $"可操作（基本数据）分区 {total} 个 · 检测用时 {layout.ElapsedMs} ms" +
              (total == 0 ? "　⚠ 没有可操作的分区——点「磁盘明细…」看每个分区的判定理由" : string.Empty);
        _lblSummary.ForeColor = layout.Disks.Count == 0 || total == 0 ? UiStyle.Danger : UiStyle.Primary;

        _strip.Snapshot = layout;
        _strip.DiskNumber = defaultDisk;
        _strip.Invalidate();

        RefreshDiskDependentUi();
    }

    private void OnDiskChanged()
    {
        if (_cmbDisk.SelectedItem is not DiskChoice dc) return;
        if (_selectedDisk == dc.Number) return;

        _selectedDisk = dc.Number;
        _strip.DiskNumber = dc.Number;
        _strip.Invalidate();

        // 切盘必须清掉上一个盘的分析结果，避免把 A 盘的结论用到 B 盘
        _splitSupported = null;
        _recommend = null;
        _splitPlan = null;
        _mergePlan = null;
        _mergeTargetSupported = null;
        ClearImpactCache();
        AppendLog($"已切换到磁盘 {dc.Number}。");
        RefreshDiskDependentUi();
    }

    /// <summary>刷新「用未分配空间新建分区」按钮的可用性与文案（含未分配合计）。</summary>
    private void UpdateNewPartitionButton()
    {
        if (_btnNewPartition is null) return;
        if (_probe is null)
        {
            _btnNewPartition.Enabled = false;
            _btnNewPartition.Text = "用未分配空间新建分区…";
            return;
        }

        var layout = _probe.Layout;
        long anyFree = layout.Disks.Sum(d => layout.FreeExtentsOfDisk(d.Number).Sum(f => (long)f.SizeBytes));
        _btnNewPartition.Enabled = !_busy && layout.IsElevated && anyFree >= FreeExtent.MinUsefulBytes;
        _btnNewPartition.Text = anyFree >= FreeExtent.MinUsefulBytes
            ? $"用未分配空间新建分区…（{SizeFormatter.Format(anyFree)}）"
            : "用未分配空间新建分区…";
    }

    /// <summary>刷新依赖"当前磁盘"的下拉框与状态。</summary>
    private void RefreshDiskDependentUi()
    {
        if (_probe is null) return;
        var layout = _probe.Layout;
        var candidates = layout.Partitions
            .Where(p => p.IsOperable && p.DriveLetter is not null && p.DiskNumber == _selectedDisk)
            .OrderBy(p => p.OffsetBytes)
            .ToArray();

        FillPartitionCombo(_cmbSplitSource, candidates);
        FillPartitionCombo(_cmbMergeSource, candidates);
        FillPartitionCombo(_cmbMergeTarget, candidates);

        SelectByLetter(_cmbSplitSource, layout.SystemDriveLetter);
        var nonSystem = candidates.FirstOrDefault(p => p.DriveLetter != layout.SystemDriveLetter);
        if (nonSystem?.DriveLetter is { } nsl) SelectByLetter(_cmbMergeSource, nsl);
        SelectByLetter(_cmbMergeTarget, layout.SystemDriveLetter);

        _cmbNewLetter.Items.Clear();
        for (char c = 'D'; c <= 'Z'; c++)
            if (!layout.UsedDriveLetters.Contains(c))
                _cmbNewLetter.Items.Add(c + ":");
        if (_cmbNewLetter.Items.Count > 0) _cmbNewLetter.SelectedIndex = 0;

        var disk = layout.DiskByNumber(_selectedDisk);
        int operable = layout.OperableCountOfDisk(_selectedDisk);

        // 「用未分配空间新建分区」：提权 + 有未分配空间才可点（与当前选中的盘无关，
        // 因为窗口里可以再选盘；下拉里的提示会写清哪块盘有空间）
        UpdateNewPartitionButton();
        _lblDiskInfo.Text = disk is null
            ? string.Empty
            : $"{disk.StyleText}　{disk.BusType}　健康 {disk.HealthText}　" +
              $"可操作分区 {operable} 个" +
              (disk.HasLdmPartitions ? "　[动态磁盘：不操作]" : string.Empty) +
              (operable == 0 ? "　⚠ 本盘没有可操作的分区" : string.Empty);
        _lblDiskInfo.ForeColor = operable == 0 ? UiStyle.Danger : UiStyle.SubText;

        _lblSummary.Text = BuildSummaryText(layout);
        _lblSummary.ForeColor = _lblSummary.Text.Contains("可操作（基本数据）分区 0 个") ? UiStyle.Danger : UiStyle.Primary;

        RefreshSplitState();
        RefreshMergeState();
    }

    private static string BuildSummaryText(DiskLayoutSnapshot layout)
    {
        int total = layout.Partitions.Count(p => p.IsOperable);
        if (layout.Disks.Count == 0)
            return $"磁盘信息不可读{(layout.IsElevated ? "（已提权，可能是 WMI 服务异常）" : "：需要管理员权限")}　—— 点「磁盘明细…」查看原因";
        return $"{layout.Disks.Count} 块磁盘 · {layout.Partitions.Count} 个分区 · " +
               $"系统盘 {(layout.SystemDriveLetter is { } s ? s + ":" : "未知")} · " +
               $"可操作（基本数据）分区 {total} 个 · 检测用时 {layout.ElapsedMs} ms" +
               (total == 0 ? "　⚠ 没有可操作的分区——点「磁盘明细…」看每个分区的判定理由" : string.Empty);
    }

    private void FillPartitionCombo(ComboBox combo, PartitionInfo[] partitions)
    {
        combo.Items.Clear();
        foreach (var p in partitions)
        {
            var vol = p.DriveLetter is { } c ? _probe?.Layout.VolumeByDriveLetter(c) : null;
            string text = $"{p.LetterText}　{SizeFormatter.Format((long)p.SizeBytes)}" +
                          (vol is null
                              ? string.Empty
                              : $"　{vol.FileSystem}　可用 {SizeFormatter.Format((long)vol.FreeBytes)}") +
                          (p.IsBoot ? "　[Windows 所在]" : string.Empty);
            combo.Items.Add(new PartitionChoice(p, text));
        }
    }

    private void SelectByLetter(ComboBox combo, char? letter)
    {
        if (letter is not { } l) return;
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is PartitionChoice pc && pc.Partition.DriveLetter == l)
            {
                combo.SelectedIndex = i;
                return;
            }
    }

    private static PartitionInfo? SelectedPartition(ComboBox combo) =>
        combo.SelectedItem is PartitionChoice pc ? pc.Partition : null;

    private sealed record PartitionChoice(PartitionInfo Partition, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record DiskChoice(int Number, string Text)
    {
        public override string ToString() => Text;
    }

    // ==================== 分盘 ====================

    private async Task AnalyzeSplitAsync()
    {
        if (_busy || _probe is null) return;
        if (SelectedPartition(_cmbSplitSource) is not { } source)
        {
            MessageBox.Show(this,
                "本盘中还没有可操作的分区。\n\n" +
                "本工具只操作判定为「基本数据」的分区。请点「磁盘明细…」查看每个分区的类型判定与理由；" +
                "若明细里显示某分区 Type 原文为 Basic 却判定为不可操作，请把该窗口的「导出诊断 JSON」结果反馈给开发者。",
                "没有可操作的分区", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在分析可压缩空间（可能需数分钟）...", marquee: true);
        AppendLog($"=== 分析 {source.DisplayName} 的可收缩范围（只读） ===");
        _cts = new CancellationTokenSource();
        try
        {
            _splitSupported = await DiskLayoutService.QuerySupportedSizeAsync(source, _cts.Token,
                msg => Ui(() => AppendLog(msg)));
            _recommend = null;
            RefreshSplitState();
            _lblGate.Text = _splitSupported.Ok
                ? $"分析完成：最多可分出 {SizeFormatter.Format((long)_splitSupported.MaxShrinkableBytes(source.SizeBytes))}。"
                : "分析未成功：" + DiskLayoutService.ExplainReturnCode(_splitSupported.ReturnValue);
        }
        catch (OperationCanceledException)
        {
            AppendLog("分析已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("分析失败：" + ex.Message);
            MessageBox.Show(this, "分析失败：\n\n" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void RefreshSplitState()
    {
        if (_probe is null) return;
        var source = SelectedPartition(_cmbSplitSource);

        _splitBlocks = Array.Empty<BlockReason>();
        _splitPlan = null;
        _lstSplitSteps.Items.Clear();
        _btnRecommend.Enabled = false;
        SetBlocksLabel(_lblSplitBlocks, _btnSplitBlocks, _splitBlocks, "请先选择并分析分区");

        if (source is null)
        {
            _lblRecommend.Text = "本盘没有可操作的分区。点「磁盘明细…」查看各分区的判定理由。";
            _lblRecommend.ForeColor = UiStyle.Danger;
            _lblKeepSplit.Text = string.Empty;
            _lblExplain.Text = string.Empty;
            _slider.Enabled = false;
            UpdateGate();
            return;
        }

        if (_splitSupported is null)
        {
            _lblRecommend.Text = "尚未分析：请点「分析可压缩空间」——它会实测本机最多能分出多少（只读，不改动任何东西）。";
            _lblRecommend.ForeColor = UiStyle.SubText;
            _lblKeepSplit.Text = string.Empty;
            _lblExplain.Text = string.Empty;
            _slider.Enabled = false;
            UpdateGate();
            return;
        }

        var layout = _probe.Layout;
        var volume = source.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
        long paging = source.DriveLetter is { } pl ? DiskLayoutService.MeasurePagingFiles(pl) : 0;
        long diskTotal = (long)(layout.DiskByNumber(source.DiskNumber)?.SizeBytes ?? source.SizeBytes);

        var rec = PartitionRecommender.Recommend(new RecommendInput
        {
            VolumeTotalBytes = source.SizeBytes,
            VolumeFreeBytes = volume?.FreeBytes ?? 0,
            PagingFileBytes = paging,
            DiskTotalBytes = (ulong)Math.Max(0, diskTotal),
            MaxShrinkableBytes = _splitSupported.Ok ? _splitSupported.MaxShrinkableBytes(source.SizeBytes) : null,
        });
        _recommend = rec;
        _btnRecommend.Enabled = true;

        _lblRecommend.Text = rec.CanSplit
            ? $"建议保留 ≥ {SizeFormatter.Format(rec.RecommendedKeepBytes)}（下限 {SizeFormatter.Format(rec.FloorKeepBytes)}）" +
              $"　|　本机最多可分出 {SizeFormatter.Format((long)(rec.MaxShrinkableBytes ?? 0))}" +
              $"　|　新分区最小建议 {SizeFormatter.Format(rec.NewPartMinBytes)}"
            : "⛔ " + (rec.BlockReason ?? "当前不具备分盘条件");
        _lblRecommend.ForeColor = rec.CanSplit ? UiStyle.Text : UiStyle.Danger;

        long minGb = Math.Max(1, rec.SliderMinKeepBytes / Gb);
        long maxGb = Math.Max(minGb + 1, rec.SliderMaxKeepBytes / Gb);
        long defaultGb = Math.Clamp(rec.DefaultKeepBytes / Gb, minGb, maxGb);

        _syncingSlider = true;
        try
        {
            _slider.Minimum = 0;
            _slider.Maximum = (int)Math.Min(int.MaxValue, maxGb - minGb);
            _slider.Value = (int)Math.Clamp(defaultGb - minGb, 0, _slider.Maximum);
            _slider.Tag = minGb;
            _slider.Enabled = rec.CanSplit;
        }
        finally
        {
            _syncingSlider = false;
        }

        UpdateSliderLabels();
        RebuildSplitPlan();
    }

    private long CurrentKeepBytes()
    {
        if (_recommend is null) return 0;
        long minGb = _slider.Tag is long t ? t : 1L;
        return (minGb + _slider.Value) * Gb;
    }

    private void OnSliderChanged()
    {
        if (_syncingSlider) return;
        UpdateSliderLabels();
        RebuildSplitPlan();
    }

    private void UpdateSliderLabels()
    {
        if (_recommend is null) return;
        long keep = CurrentKeepBytes();
        long split = Math.Max(0, _recommend.TotalCapacityBytes - keep);
        var src = SelectedPartition(_cmbSplitSource);
        string letter = src?.DriveLetter is { } c ? c.ToString() : "该卷";

        _lblKeepSplit.Text = $"{letter} 盘保留 {SizeFormatter.Format(keep)}　／　新分区分得 {SizeFormatter.Format(split)}";
        _lblExplain.Text = _recommend.Explain(keep);
        _lblExplain.ForeColor = keep < _recommend.FloorKeepBytes ? UiStyle.Danger
            : keep < _recommend.RecommendedKeepBytes ? UiStyle.WarningText
            : UiStyle.Success;
    }

    private void RebuildSplitPlan()
    {
        if (_probe is null || _recommend is null || _splitSupported is null) return;
        if (SelectedPartition(_cmbSplitSource) is not { } source) return;

        long keep = CurrentKeepBytes();
        char? newLetter = _cmbNewLetter.SelectedItem is string s && s.Length > 0 ? s[0] : null;
        string label = PartitionPlanBuilder.SanitizeLabel(_txtNewLabel.Text);

        _splitBlocks = PartitionPlanValidator.ValidateSplit(_probe, source, _splitSupported, keep, newLetter);
        SetBlocksLabel(_lblSplitBlocks, _btnSplitBlocks, _splitBlocks, null);

        if (!_splitBlocks.HasBlocking() && newLetter is { } nl)
        {
            _splitPlan = PartitionPlanBuilder.BuildSplit(_probe, source, _splitSupported, keep, nl, label, dryRun: true);
            FillSteps(_lstSplitSteps, _splitPlan);

            // 计划与实际之间的口径差异（1 MB 对齐）直接写在一行摘要里，别让用户以为工具算错了
            if (_splitPlan.Note.Length > 0)
                _lblKeepSplit.Text = $"{source.LetterText} 盘保留 {SizeFormatter.Format(keep)}　／　" +
                                     $"新分区实际可建 {SizeFormatter.Format(_splitPlan.FreedBytes)}";
        }
        else
        {
            _splitPlan = null;
            _lstSplitSteps.Items.Clear();
        }

        UpdateGate();
    }

    // ==================== 合盘 ====================

    private void OnMergeSelectionChanged()
    {
        _mergeTargetSupported = null;
        _mergePlan = null;
        RefreshMergeState();   // 内部会按源分区归属判断是否清掉影响盘点缓存
    }

    private async Task AnalyzeMergeAsync()
    {
        if (_busy || _probe is null) return;
        var source = SelectedPartition(_cmbMergeSource);
        var target = SelectedPartition(_cmbMergeTarget);
        if (source is null || target is null)
        {
            MessageBox.Show(this, "请先选择「要合并掉的分区」和「要变大的分区」。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (source.DiskNumber == target.DiskNumber && source.PartitionNumber == target.PartitionNumber)
        {
            MessageBox.Show(this, "两个下拉框选的是同一个分区，请重新选择。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在分析合盘可行性（只读）...", marquee: true);
        AppendLog($"=== 分析合盘可行性：{source.LetterText} → {target.LetterText}（只读） ===");

        _cts = new CancellationTokenSource();
        try
        {
            // 删除源分区之前，目标分区右侧没有空闲空间，SizeMax 必然等于当前大小；
            // 这里仍实测一次，既验证接口可用，也把结果作为「提示」（不阻断）展示。
            _mergeTargetSupported = await DiskLayoutService.QuerySupportedSizeAsync(target, _cts.Token,
                msg => Ui(() => AppendLog(msg)));
            RefreshMergeState();
            _lblGate.Text = "可行性分析完成。";
        }
        catch (OperationCanceledException)
        {
            AppendLog("分析已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("分析失败：" + ex.Message);
            MessageBox.Show(this, "分析失败：\n\n" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void RefreshMergeState()
    {
        if (_probe is null) return;
        var source = SelectedPartition(_cmbMergeSource);
        var target = SelectedPartition(_cmbMergeTarget);

        // 源/目标变了，之前的影响盘点就失效了
        if (source?.PartitionNumber != _impactForPartition?.PartitionNumber ||
            source?.DiskNumber != _impactForPartition?.DiskNumber)
        {
            _mergeImpact = null;
            _impactForPartition = null;
        }

        _mergePlan = null;
        _lstMergeSteps.Items.Clear();
        _mergeBlocks = Array.Empty<BlockReason>();
        SetBlocksLabel(_lblMergeBlocks, _btnMergeBlocks, _mergeBlocks, "请先选择两个分区");
        RefreshImpactLabel();

        if (source is null || target is null)
        {
            UpdateGate();
            return;
        }

        var supported = _mergeTargetSupported ?? new SupportedSize { ReturnValue = -1, ExtendedStatus = "尚未分析" };
        _mergeBlocks = PartitionPlanValidator.ValidateMerge(_probe, source, target, supported);
        SetBlocksLabel(_lblMergeBlocks, _btnMergeBlocks, _mergeBlocks, null);

        if (!_mergeBlocks.HasBlocking())
        {
            long extendable = source.EndBytes >= target.EndBytes ? (long)(source.EndBytes - target.EndBytes) : 0;
            _mergePlan = PartitionPlanBuilder.BuildMerge(_probe, source, target, extendable, dryRun: true, _mergeImpact);
            FillSteps(_lstMergeSteps, _mergePlan);
        }

        UpdateGate();
    }

    /// <summary>刷新"将被删除的内容"那一行提示。</summary>
    private void RefreshImpactLabel()
    {
        var source = SelectedPartition(_cmbMergeSource);
        if (source is null)
        {
            _lblImpact.Text = "请先选择「要合并掉的分区」。";
            _lblImpact.ForeColor = UiStyle.SubText;
            _btnImpactDetail.Enabled = false;
            return;
        }

        if (_mergeImpact is null)
        {
            _lblImpact.Text = "点「" + (_btnExecute?.Text ?? "删除并扩容…") +
                              "」时会先盘点该分区里将被永久删除的内容（只读），并列出文件数与主要目录。";
            _lblImpact.ForeColor = UiStyle.SubText;
            _btnImpactDetail.Enabled = false;
            return;
        }

        if (_mergeImpact.IsEmpty)
        {
            _lblImpact.Text = $"✔ {source.LetterText} 里没有统计到文件，删除它基本没有数据损失（本工具不备份）。";
            _lblImpact.ForeColor = UiStyle.Success;
        }
        else
        {
            _lblImpact.Text = $"⚠ {source.LetterText} 里的 {_mergeImpact.ScaleText} 会被永久删除" +
                              "（本工具不备份，请先自行复制出去）。";
            _lblImpact.ForeColor = UiStyle.Danger;
        }
        _btnImpactDetail.Enabled = true;
    }

    /// <summary>展示"将被删除的内容"完整清单（弹窗，只读）。</summary>
    private void OnShowImpactDetail()
    {
        if (_probe is null || _mergeImpact is null) return;
        var source = SelectedPartition(_cmbMergeSource);
        var target = SelectedPartition(_cmbMergeTarget);
        if (source is null || target is null) return;

        long extendable = source.EndBytes >= target.EndBytes ? (long)(source.EndBytes - target.EndBytes) : 0;
        string text = PartitionImpactScanner.BuildWarningText(_mergeImpact, source, target, extendable);
        using var dlg = new TextViewForm($"将被永久删除的内容 — {source.LetterText}", text,
            "本窗口只做只读盘点与说明，不会移动、复制或删除任何文件。");
        dlg.ShowDialog(this);
    }

    // ==================== 校验结论（一行 + 弹窗） ====================

    private void SetBlocksLabel(Label label, RoundedButton button, IReadOnlyList<BlockReason> blocks, string? hint)
    {
        if (hint is not null && blocks.Count == 0)
        {
            label.Text = hint;
            label.ForeColor = UiStyle.SubText;
            button.Enabled = false;
            return;
        }

        bool blocking = blocks.HasBlocking();
        label.Text = BlockersForm.Summarize(blocks);
        label.ForeColor = blocking ? UiStyle.Danger : blocks.Count > 0 ? UiStyle.WarningText : UiStyle.Success;
        button.Enabled = blocks.Count > 0;
    }

    private void ShowBlocksDialog(IReadOnlyList<BlockReason> blocks, string title)
    {
        using var dlg = new BlockersForm(title, blocks,
            "「阻断」项必须全部解决才能执行；「提示」项不影响执行，但建议看一眼。");
        dlg.ShowDialog(this);
    }

    // ==================== 计划步骤 ====================

    private static void FillSteps(ListView lv, PartitionPlan plan)
    {
        lv.Items.Clear();
        foreach (var st in plan.Steps)
        {
            var lvi = new ListViewItem($"{st.Index}. {st.Title}")
            {
                ForeColor = st.IsDestructive ? UiStyle.Danger : UiStyle.Text,
            };
            lvi.SubItems.Add(st.IsDestructive ? "★ 不可逆" : st.IsInterruptible ? string.Empty : "不可中断");
            lvi.ToolTipText = RichTextRenderer.StripMarkdown($"{st.Title}\n\n做什么：{st.Action}\n预期结果：{st.Expected}\n出错了：{st.OnFailure}\n\n（双击看完整说明）");
            lv.Items.Add(lvi);
        }
    }

    private void ShowStepDetail(PartitionPlan? plan, ListView lv)
    {
        if (plan is null || lv.SelectedIndices.Count == 0) return;
        int index = lv.SelectedIndices[0];
        if (index < 0 || index >= plan.Steps.Count) return;
        var st = plan.Steps[index];

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"第 {st.Index} 步：{st.Title}");
        sb.AppendLine(st.IsDestructive ? "★ 这一步不可逆" : "这一步不会删除你的文件");
        sb.AppendLine(st.IsInterruptible ? "执行中可以取消" : "执行中不可中断（发出后必须等它完成）");
        sb.AppendLine();
        sb.AppendLine("做什么：");
        sb.AppendLine("  " + st.Action);
        sb.AppendLine();
        sb.AppendLine("预期结果：");
        sb.AppendLine("  " + st.Expected);
        sb.AppendLine();
        sb.AppendLine("出错了怎么办：");
        sb.AppendLine("  " + st.OnFailure);
        if (!string.IsNullOrWhiteSpace(st.PowerShell))
        {
            sb.AppendLine();
            sb.AppendLine("等价命令（可在管理员 PowerShell 中逐条粘贴执行）：");
            foreach (var line in st.PowerShell.Split('\n'))
                sb.AppendLine("  " + line.TrimEnd('\r'));
        }

        using var dlg = new TextViewForm($"第 {st.Index} 步：{st.Title}", sb.ToString(),
            "本窗口仅展示说明文字，不会执行任何命令。");
        dlg.ShowDialog(this);
    }

    // ==================== 明细弹窗 ====================

    private void OnShowDiskDetail()
    {
        if (_probe is null)
        {
            MessageBox.Show(this, "尚未完成检测。请先点「重新检测」。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new DiskDetailForm(_probe);
        dlg.ShowDialog(this);
    }

    private void OnShowEnvironment()
    {
        if (_probe is null)
        {
            MessageBox.Show(this, "尚未完成检测。请先点「重新检测」。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string text = PartitionScriptWriter.BuildEnvironmentCheck(_probe);
        AppendLog("=== 环境检查 ===");
        using var dlg = new TextViewForm("环境检查（只读）", text,
            "BitLocker / 页面文件 / 休眠 / 卷影副本 / 系统保护——它们都会影响分区能不能调整。");
        dlg.ShowDialog(this);
    }

    private void OnShowRecommendDetail()
    {
        if (_probe is null || _recommend is null || SelectedPartition(_cmbSplitSource) is not { } source) return;
        string text = PartitionScriptWriter.BuildRecommendDetail(_recommend, source,
            source.DriveLetter is { } c ? _probe.Layout.VolumeByDriveLetter(c) : null);
        using var dlg = new TextViewForm("分盘推荐值明细", text,
            "全部数字都按本机实测计算，不是写死的固定值。");
        dlg.ShowDialog(this);
    }

    private void OnShowWhyNotShrink()
    {
        if (_probe is null) return;
        if (SelectedPartition(_cmbSplitSource) is not { } source)
        {
            MessageBox.Show(this, "请先选择要分盘的卷。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string text = ShrinkBlockerAdvisor.BuildText(_probe, source, _splitSupported);
        AppendLog("=== 为什么分不出空间（本机实测 + 补救清单） ===");
        foreach (var line in text.Split('\n')) AppendLog("  " + line.TrimEnd('\r'));

        using var dlg = new TextViewForm($"为什么 {source.LetterText} 分不出空间？—— 补救清单", text,
            "按「收益 / 风险」排序，建议从上往下逐条试；每做一条就回到主窗口重新点「分析可压缩空间」。");
        dlg.ShowDialog(this);
    }

    private void OnShowSessionLog()
    {
        string text = _sessionLog.Count == 0
            ? "（本次会话还没有日志）"
            : RichTextRenderer.StripMarkdown(string.Join(Environment.NewLine, _sessionLog));
        using var dlg = new TextViewForm("本次会话日志", text,
            $"共 {_sessionLog.Count} 行。持久化日志见主界面「操作日志」：{OperationLog.CurrentPath}");
        dlg.ShowDialog(this);
    }

    // ==================== 说明书 ====================

    private PartitionPlan? ActivePlan => _tabs?.SelectedIndex == 1 ? _mergePlan : _splitPlan;

    private IReadOnlyList<BlockReason> ActiveBlocks => _tabs?.SelectedIndex == 1 ? _mergeBlocks : _splitBlocks;

    private void OnWriteManual()
    {
        if (_probe is null || ActivePlan is not { } plan)
        {
            MessageBox.Show(this, "请先完成分析并生成计划，再导出说明书。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RecommendResult? rec = plan.Kind == PartitionOpKind.Split ? _recommend : null;

        try
        {
            string content = PartitionScriptWriter.BuildManual(_probe, plan, rec, ActiveBlocks);
            string path = PartitionScriptWriter.WriteManual(content);
            _lastManualPath = path;
            OperationLog.LogPartitionManual(path);
            AppendLog($"已生成说明书：{path}");

            var r = MessageBox.Show(this, $"说明书已生成：\n{path}\n\n是否现在打开查看？",
                "说明书已生成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) OpenManual();
        }
        catch (Exception ex)
        {
            OperationLog.Log("分区-说明书", "生成失败：" + ex.Message);
            AppendLog("生成说明书失败：" + ex.Message);
            MessageBox.Show(this, "生成说明书失败：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UpdateGate();
        }
    }

    private void OpenManual()
    {
        if (_lastManualPath is null || !File.Exists(_lastManualPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _lastManualPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppendLog("打开说明书失败：" + ex.Message);
        }
    }

    // ==================== 闸门 ====================

    private void OnTabChanged()
    {
        UpdateGate();
        if (_tabs.SelectedIndex == 1)
        {
            RefreshMergeState();
        }
        else
        {
            RefreshSplitState();
        }
    }

    private void UpdateGate()
    {
        bool ack = _chkAck.Checked;
        bool confirm = _chkConfirmRun.Checked;
        var plan = ActivePlan;
        bool hasPlan = plan is not null && !ActiveBlocks.HasBlocking();
        bool elevated = _probe?.Layout.IsElevated ?? false;
        bool merge = _tabs?.SelectedIndex == 1;

        _btnManual.Enabled = hasPlan;

        // 合盘不再要求"先搬迁校验"：工具不替用户备份，改由风险弹窗 + 两次确认把关
        _btnExecute.Text = merge ? "删除并扩容…" : "开始执行（分盘）";
        _btnExecute.Enabled = !_busy && elevated && ack && confirm && hasPlan;

        if (_probe is not null && !elevated)
            _lblGate.Text = "当前进程没有管理员权限：无法读取磁盘信息，也无法执行分区操作。请点左下方的「以管理员身份重新启动」。";
        else if (!ack)
            _lblGate.Text = "请先勾选上方的风险确认。";
        else if (!hasPlan)
            _lblGate.Text = "请先选择分区、完成分析并解决全部「阻断」项（点「查看校验…」）。";
        else if (!confirm)
            _lblGate.Text = "请勾选「我已阅读说明书，确认由本工具执行」。（也可以只用说明书，在系统「磁盘管理」里自己操作）";
        else if (merge)
            _lblGate.Text = "点「删除并扩容…」后会先只读盘点该分区内容，再弹出风险确认（需勾选）与最后一次确认。";
        else
            _lblGate.Text = "一切就绪。点「开始执行（分盘）」后会弹出确认框（收缩只会搬移文件，不会删除数据）。";

        _chkConfirmRun.Enabled = ack && hasPlan;
    }

    private void OnExecuteClicked()
    {
        if (_busy) return;
        var plan = ActivePlan;
        if (plan is null || ActiveBlocks.HasBlocking())
        {
            ShowBlocksDialog(ActiveBlocks, "校验未通过");
            return;
        }
        if (!_chkAck.Checked || !_chkConfirmRun.Checked)
        {
            MessageBox.Show(this, "请先勾选风险确认与「我已阅读说明书，确认由本工具执行」。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (plan.Kind == PartitionOpKind.Split) _ = RunSplitAsync(plan);
        else _ = RunMergeAsync(plan);
    }

    // ==================== 执行 ====================

    private async Task RunSplitAsync(PartitionPlan plan)
    {
        var letter = plan.NewDriveLetter is { } c ? c + ":" : "新盘符";
        var source = SelectedPartition(_cmbSplitSource);
        var confirm = MessageBox.Show(this,
            "即将执行分盘（共 3 步）：\n\n" +
            $"  1) 把 {source?.LetterText ?? "源分区"} 从 {SizeFormatter.Format(plan.SourceSizeBeforeBytes)} " +
            $"收缩到 {SizeFormatter.Format(plan.SourceSizeAfterBytes)}\n" +
            $"  2) 在释放出的 {SizeFormatter.Format(plan.FreedBytes)} 未分配空间新建分区 {letter}\n" +
            $"  3) 把 {letter} 格式化为 NTFS（卷标「{plan.NewVolumeLabel}」）\n\n" +
            "第 1 步只会把文件挪到分区前部，不会删除你的文件；第 3 步只会格式化刚刚新建的那个分区。\n" +
            "执行期间请不要关机、不要断电、不要手动操作磁盘管理。\n\n" +
            "确认开始执行？",
            "确认执行分盘", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true, "正在执行分盘：收缩 → 新建 → 格式化", marquee: true, risk: BusyRisk.Critical);
        AppendLog("");
        AppendLog("################ 开始执行分盘 ################");
        OperationLog.Log("分区-分盘", $"用户确认执行：{plan.Title}");

        _cts = new CancellationTokenSource();
        try
        {
            var report = await PartitionExecutor.ExecuteSplitAsync(_probe!, plan,
                (i, total, msg) => Ui(() => ReportStep(i, total, msg)),
                msg => Ui(() => AppendLog(msg)), _cts.Token);
            FinishExecution(report, "分盘");
        }
        catch (OperationCanceledException)
        {
            AppendLog("执行已取消（取消只在步骤边界生效，已发出的分区操作不会被打断）。");
        }
        catch (Exception ex)
        {
            AppendLog("执行失败：" + ex.Message);
            OperationLog.LogPartitionResult(false, "异常：" + ex.Message);
            MessageBox.Show(this, "执行失败：\n\n" + ex.Message, "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
            await ReloadAsync();
        }
    }

    private async Task RunMergeAsync(PartitionPlan plan)
    {
        var source = SelectedPartition(_cmbMergeSource);
        var target = SelectedPartition(_cmbMergeTarget);
        if (source is null || target is null) return;

        // ---------- ① 先盘点"将被删除的内容"（只读；没盘点过才跑） ----------
        if (_mergeImpact is null)
        {
            if (source.DriveLetter is not { } scanLetter)
            {
                MessageBox.Show(this, "源分区没有盘符，无法遍历其内容。请先在磁盘管理中分配盘符。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, $"正在盘点 {scanLetter}: 里将被删除的内容（只读）...", marquee: true);
            AppendLog("");
            AppendLog($"=== 盘点 {scanLetter}: 里将被永久删除的内容（只读，不会改动任何文件） ===");
            _cts = new CancellationTokenSource();
            try
            {
                _mergeImpact = await PartitionImpactScanner.ScanAsync(scanLetter, _cts.Token,
                    msg => Ui(() => AppendLog(msg)));
                _impactForPartition = source;
            }
            catch (OperationCanceledException)
            {
                AppendLog("盘点已取消，未执行任何删除。");
                return;
            }
            catch (Exception ex)
            {
                AppendLog("盘点失败：" + ex.Message);
                MessageBox.Show(this, "盘点失败，出于安全没有继续：\n\n" + ex.Message,
                    "盘点失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                SetBusy(false, null);
                _cts?.Dispose();
                _cts = null;
            }

            RefreshImpactLabel();
            // 用盘点结果重建计划，让说明书与日志都带上"删掉了什么"
            _mergePlan = PartitionPlanBuilder.BuildMerge(_probe!, source, target,
                source.EndBytes >= target.EndBytes ? (long)(source.EndBytes - target.EndBytes) : 0,
                dryRun: true, _mergeImpact);
            FillSteps(_lstMergeSteps, _mergePlan);
        }

        // 记录到操作日志：这是删除之后唯一的可追溯线索
        OperationLog.LogMergeImpact(source.LetterText, _mergeImpact.FileCount, _mergeImpact.TotalBytes,
            _mergeImpact.Denied, _mergeImpact.Truncated);

        // ---------- ② 风险弹窗：勾选 + 危险按钮（默认按钮是取消） ----------
        using (var warn = new MergeDeleteWarningForm(_mergeImpact, source, target, plan.FreedBytes))
        {
            if (warn.ShowDialog(this) != DialogResult.OK)
            {
                AppendLog("用户在风险确认弹窗中选择了取消，未执行任何删除。");
                OperationLog.Log("分区-合盘", $"用户取消：{source.LetterText} 未删除");
                return;
            }
        }

        // ---------- ③ 最后一次确认 ----------
        var typed = MessageBox.Show(this,
            $"这是最后一次确认。\n\n将永久删除分区 {source.LetterText}（含其分区表信息，" +
            $"其中 {_mergeImpact.ScaleText}），随后把空间并入 {target.LetterText}。\n" +
            "此后无法撤销，本工具也无法帮你恢复。\n\n真的继续吗？",
            "最后一次确认", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2);
        if (typed != DialogResult.Yes)
        {
            AppendLog("用户在最后一次确认中选择了否，未执行任何删除。");
            return;
        }

        SetBusy(true, "正在执行合盘：删除分区 → 并入空间", marquee: true, risk: BusyRisk.Critical);
        AppendLog("");
        AppendLog("################ 开始执行合盘（删除 + 扩容） ################");
        OperationLog.Log("分区-合盘", $"用户已完成风险确认并执行：删除 {source.LetterText} 并扩容 {target.LetterText}；" +
                                    $"其中 {(_mergeImpact is null ? "未盘点" : _mergeImpact.ScaleText)}，本工具未做备份");

        _cts = new CancellationTokenSource();
        try
        {
            var report = await PartitionExecutor.ExecuteMergeAsync(_probe!, plan,
                (i, total, msg) => Ui(() => ReportStep(i, total, msg)),
                msg => Ui(() => AppendLog(msg)), _cts.Token);
            FinishExecution(report, "合盘");
        }
        catch (OperationCanceledException)
        {
            AppendLog("执行已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("执行失败：" + ex.Message);
            OperationLog.LogPartitionResult(false, "异常：" + ex.Message);
            MessageBox.Show(this, "执行失败：\n\n" + ex.Message, "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
            await ReloadAsync();
        }
    }

    private void ReportStep(int current, int total, string message)
    {
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Maximum = Math.Max(total, 1);
        _progressBar.Value = Math.Min(current, _progressBar.Maximum);
        string text = total > 0 ? $"执行中 {current}/{total}：{message}" : message;
        _lblGate.Text = text;
        _busyHost.Update(text);   // 遮罩上的动画文案跟着走，用户才看得到进展
    }

    private void FinishExecution(PartitionExecutionReport report, string kind)
    {
        string text = report.ToText();
        AppendLog(text);
        _lblGate.Text = report.Succeeded ? $"{kind}完成。" : $"{kind}未完成：{report.FirstFailure?.Title}";

        using var dlg = new TextViewForm(
            report.Succeeded ? $"{kind}完成" : $"{kind}未完成",
            text,
            report.Succeeded
                ? "请在资源管理器中核对新分区/新容量；详细记录见操作日志。"
                : "数据不会因为分区调整失败而丢失。请按报告里的「后续处理建议」操作，或用说明书里的等价命令手工完成剩余步骤。");
        dlg.ShowDialog(this);
    }

    /// <summary>源分区变了（或换盘了）→ 之前的影响盘点失效，必须重新盘点。</summary>
    private void ClearImpactCache()
    {
        _mergeImpact = null;
        _impactForPartition = null;
        RefreshImpactLabel();
    }

    // ==================== 条带图点击 ====================

    private void OnStripPartitionClicked(PartitionInfo p)
    {
        AppendLog($"条带图选中：{p.DisplayName}（{p.KindText}）" +
                  (p.IsOperable ? string.Empty : $" —— {p.ClassificationReason}"));

        if (!p.IsOperable || p.DriveLetter is null)
        {
            _lblGate.Text = $"{p.KindText} 不参与分区操作：{p.ClassificationReason}";
            return;
        }

        if (p.DriveLetter == _probe?.Layout.SystemDriveLetter)
        {
            SelectByLetter(_cmbSplitSource, p.DriveLetter);
            SelectByLetter(_cmbMergeTarget, p.DriveLetter);
            _lblGate.Text = $"{p.LetterText} 已设为「分盘的源」与「合盘的目标」。";
        }
        else
        {
            SelectByLetter(_cmbMergeSource, p.DriveLetter);
            _lblGate.Text = $"{p.LetterText} 已设为「合盘要合并掉的分区」。";
        }
    }

    // ==================== 通用 ====================

    /// <summary>
    /// 统一设置"忙"状态：禁用/启用按钮、驱动中央的处理动画遮罩。
    ///
    /// <paramref name="risk"/> = <see cref="BusyRisk.Critical"/> 表示**不可中断的危险操作**
    /// （分区收缩/删除/扩容）：此时登记到 <see cref="OperationGuard"/>（关闭窗口会被拒绝），
    /// 遮罩不提供取消按钮，并显示"不要关闭/不要断电"的红色警示。
    /// 只读扫描（检测磁盘、分析压缩空间、分析合盘、盘点影响）传 <see cref="BusyRisk.Safe"/>：
    /// 仍可用「取消」中断，也可以直接关窗口（等于取消该操作）。
    /// </summary>
    private void SetBusy(bool busy, string? status, bool marquee = false,
        BusyRisk risk = BusyRisk.Safe)
    {
        _busy = busy;
        _btnRefresh.Enabled = !busy;
        _btnSplitAnalyze.Enabled = !busy;
        _btnMergeAnalyze.Enabled = !busy;
        UpdateNewPartitionButton();
        _progressBar.Style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!marquee) _progressBar.Value = 0;
        if (status != null) _lblGate.Text = status;

        if (busy)
        {
            bool critical = risk == BusyRisk.Critical;
            string message = status ?? "处理中…";
            // 危险操作：遮罩不提供取消入口（取消只在步骤边界生效，容易让人误以为能停下），
            // 且期间拒绝关闭窗口（返回按钮一并禁用，避免"点返回=关窗"绕过闸门）
            _busyHost.Begin(message, risk, () => _cts?.Cancel());
            _btnBack.Enabled = !critical;
            _btnCancel.Enabled = !critical;
            UpdateGate();
        }
        else
        {
            _busyHost.End();
            _btnBack.Enabled = true;
            _btnCancel.Enabled = false;
            UpdateGate();
        }
    }

    /// <summary>会话日志：只进内存缓冲（不再内联到窗口），点「查看日志…」查看。</summary>
    private void AppendLog(string text)
    {
        _sessionLog.Add(text);
        if (_sessionLog.Count > MaxSessionLogLines)
            _sessionLog.RemoveRange(0, _sessionLog.Count - MaxSessionLogLines);
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private static string BuildProbeSummary(DiskProbeResult probe)
    {
        var layout = probe.Layout;
        var parts = string.Join("；", layout.Partitions
            .Where(p => p.IsOperable)
            .Select(p =>
            {
                var v = p.DriveLetter is { } c ? layout.VolumeByDriveLetter(c) : null;
                return $"{p.LetterText} {SizeFormatter.Format((long)p.SizeBytes)}" +
                       (v is null ? string.Empty : $"（{v.FileSystem}，可用 {SizeFormatter.Format((long)v.FreeBytes)}）");
            }));
        return $"磁盘 {layout.Disks.Count} 块 / 分区 {layout.Partitions.Count} 个 / 卷 {layout.Volumes.Count} 个；" +
               $"系统盘 {(layout.SystemDriveLetter is { } s ? s + ":" : "未知")}；" +
               $"管理员 {(layout.IsElevated ? "是" : "否")}；可操作分区：{(parts.Length == 0 ? "无" : parts)}";
    }

    // ==================== 磁盘条带图控件 ====================

    /// <summary>
    /// 磁盘布局条带图：按起始偏移与容量比例绘制各分区，未分配区域留白；点击可选中分区。
    /// 颜色按判定结果区分——所有分区都灰=判定失败（正是线上故障的直观表现）。
    /// </summary>
    private sealed class DiskStrip : Control
    {
        private DiskLayoutSnapshot? _layout;
        private PartitionInfo? _selected;
        private readonly ToolTip _tip = new();
        private readonly Dictionary<Rectangle, PartitionInfo> _hit = new();

        public event EventHandler<PartitionInfo>? PartitionClicked;

        public DiskStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        /// <summary>当前展示哪一块磁盘。</summary>
        public int DiskNumber { get; set; } = -1;

        public DiskLayoutSnapshot? Snapshot
        {
            get => _layout;
            set
            {
                _layout = value;
                if (DiskNumber < 0)
                {
                    DiskNumber = value?.Disks.FirstOrDefault(d => d.IsBoot)?.Number
                                 ?? value?.Disks.FirstOrDefault()?.Number ?? -1;
                }
                _selected = null;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            foreach (var kv in _hit)
            {
                if (!kv.Key.Contains(e.Location)) continue;
                var p = kv.Value;
                var vol = p.DriveLetter is { } c ? _layout?.VolumeByDriveLetter(c) : null;
                _tip.SetToolTip(this,
                    $"{p.LetterText}　{p.KindText}\n" +
                    $"起始 {SizeFormatter.Format((long)p.OffsetBytes)}　容量 {SizeFormatter.Format((long)p.SizeBytes)}\n" +
                    (vol is null
                        ? "（无卷信息）"
                        : $"{vol.FileSystem}　已用 {SizeFormatter.Format((long)vol.UsedBytes)}　可用 {SizeFormatter.Format((long)vol.FreeBytes)}\n") +
                    (p.IsOperable ? "可参与分区操作" : "不可操作：" + p.ClassificationReason));
                return;
            }
            _tip.SetToolTip(this, string.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            foreach (var kv in _hit)
            {
                if (!kv.Key.Contains(e.Location)) continue;
                _selected = kv.Value;
                Invalidate();
                PartitionClicked?.Invoke(this, kv.Value);
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(UiStyle.Card);
            _hit.Clear();

            if (_layout is null || DiskNumber < 0)
            {
                TextRenderer.DrawText(g, "尚未取得磁盘布局（需要管理员权限）", Font, ClientRectangle, UiStyle.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var disk = _layout.DiskByNumber(DiskNumber);
            if (disk is null || disk.SizeBytes == 0) return;

            var bars = new Rectangle(12, 10, Math.Max(160, ClientSize.Width - 24), 34);
            int legendTop = bars.Bottom + 4;

            using (var brush = new SolidBrush(Color.FromArgb(244, 245, 247)))
                g.FillRectangle(brush, bars);
            using (var pen = new Pen(UiStyle.Border))
                g.DrawRectangle(pen, bars);

            foreach (var p in _layout.PartitionsOfDisk(DiskNumber))
            {
                int x0 = bars.Left + (int)((long)p.OffsetBytes * bars.Width / (long)disk.SizeBytes);
                int x1 = bars.Left + (int)((long)p.EndBytes * bars.Width / (long)disk.SizeBytes);
                int w = Math.Max(3, x1 - x0);
                if (x0 >= bars.Right) continue;
                if (x0 + w > bars.Right) w = bars.Right - x0;
                var rc = new Rectangle(x0, bars.Top + 2, w, bars.Height - 4);
                _hit[rc] = p;

                using var path = UiStyle.Rounded(rc, 4);
                using (var brush = new SolidBrush(ColorOf(p)))
                    g.FillPath(brush, path);

                bool isSel = _selected is not null &&
                             _selected.DiskNumber == p.DiskNumber &&
                             _selected.PartitionNumber == p.PartitionNumber;
                using (var pen = new Pen(isSel ? Color.FromArgb(230, 126, 34) : Color.White, isSel ? 2.5f : 1f))
                    g.DrawPath(pen, path);

                string text = p.DriveLetter is { } c
                    ? $"{char.ToUpperInvariant(c)}:"
                    : p.Kind switch
                    {
                        PartitionKind.EfiSystem => "EFI",
                        PartitionKind.MicrosoftReserved => "MSR",
                        PartitionKind.Recovery => "WinRE",
                        _ => string.Empty,
                    };
                if (text.Length > 0 && w >= 26)
                    TextRenderer.DrawText(g, text, UiStyle.Font(8.5f, bold: true), rc, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            string cap = $"{disk.DisplayName}　{SizeFormatter.Format((long)disk.SizeBytes)}　{disk.StyleText}" +
                         $"　可操作分区 {_layout.OperableCountOfDisk(DiskNumber)} 个" +
                         (disk.HasLdmPartitions ? "　[动态磁盘：不操作]" : string.Empty);
            TextRenderer.DrawText(g, cap, UiStyle.Font(8.5f, bold: true),
                new Rectangle(12, legendTop, Math.Max(200, ClientSize.Width - 24), 15), UiStyle.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int lx = 12;
            foreach (var (name, color) in Legend())
            {
                if (lx > ClientSize.Width - 86) break;
                var box = new Rectangle(lx, legendTop + 17, 10, 10);
                using (var brush = new SolidBrush(color))
                    g.FillRectangle(brush, box);
                TextRenderer.DrawText(g, name, UiStyle.Font(7.5f),
                    new Rectangle(box.Right + 3, box.Top - 4, 66, 16), UiStyle.SubText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                lx += 76;
            }
        }

        private static IEnumerable<(string Name, Color Color)> Legend()
        {
            yield return ("基本数据", ColorOf(new PartitionInfo { Kind = PartitionKind.BasicData }));
            yield return ("EFI 系统", ColorOf(new PartitionInfo { Kind = PartitionKind.EfiSystem }));
            yield return ("MSR", ColorOf(new PartitionInfo { Kind = PartitionKind.MicrosoftReserved }));
            yield return ("恢复", ColorOf(new PartitionInfo { Kind = PartitionKind.Recovery }));
            yield return ("其它/动态", ColorOf(new PartitionInfo { Kind = PartitionKind.Other }));
        }

        private static Color ColorOf(PartitionInfo p) => p.Kind switch
        {
            PartitionKind.BasicData => UiStyle.Primary,
            PartitionKind.EfiSystem => Color.FromArgb(140, 148, 160),
            PartitionKind.MicrosoftReserved => Color.FromArgb(176, 184, 196),
            PartitionKind.Recovery => Color.FromArgb(150, 120, 200),
            PartitionKind.LdmMetadata or PartitionKind.LdmData => Color.FromArgb(214, 150, 60),
            PartitionKind.StorageSpaceProtective => Color.FromArgb(196, 176, 120),
            PartitionKind.ShadowCopy => Color.FromArgb(120, 180, 200),
            _ => Color.FromArgb(190, 195, 205),
        };
    }
}
