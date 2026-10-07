using CleanC.Core;
using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

public sealed class MainForm : Form
{
    private readonly IReadOnlyList<CleanItem> _items;
    private readonly Dictionary<string, ListViewItem> _lookup = new();
    private readonly CleanerEngine _engine;

    private Panel _banner = null!;
    private ListView _listView = null!;
    private ProgressBar _progressBar = null!;
    private Label _lblFree = null!;
    private Label _lblEstimate = null!;
    private Label _lblStatus = null!;
    private RichTextBox _log = null!;
    private Button _btnRefresh = null!;
    private Button _btnDevEnv = null!;
    private Button _btnSoftware = null!;
    private Button _btnSpace = null!;
    private Button _btnDisk = null!;
    private Button _btnLog = null!;
    private Button _btnSelectAll = null!;
    private Button _btnSelectNone = null!;
    private Button _btnAnalyze = null!;
    private Button _btnClean = null!;
    private Button _btnExit = null!;
    private Button _btnCancel = null!;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private UiBusyHost _busyHost = null!;
    private string _busyLabel = string.Empty;
    private readonly bool _openDiskAfterShown;

    public MainForm() : this(Array.Empty<string>()) { }

    public MainForm(string[] args)
    {
        _openDiskAfterShown = args.Any(a =>
            a.Equals("--open=disk", StringComparison.OrdinalIgnoreCase));

        _items = ItemRegistry.CreateAll();
        _engine = new CleanerEngine(msg => Ui(() => AppendLog(msg)));

        InitializeUi();
        ReloadItems();
        RefreshFreeSpace();
        OperationLog.LogSystem($"操作日志文件：{OperationLog.CurrentPath}（启动）");

        if (_openDiskAfterShown)
        {
            // 提权重启后直接落在用户刚才想用的窗口上，而不是让他再点一次；
            // 同时抑制捐赠弹窗（同一次操作不重复打扰）。
            SuppressDonateDialog = true;
            Shown += (_, _) => OpenDiskPartition();
        }

        // 权限状态放在标题栏：不占用横幅布局，但用户随时看得到"为什么系统级项要我提权"
        if (!ElevationHelper.IsElevated())
            Text = "C盘清理工具（普通权限运行）";
    }

    private void InitializeUi()
    {
        Text = "C盘清理工具";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        // 按屏幕工作区裁剪（含最小尺寸）：否则高 DPI / 小屏上底部的「清理选中项」按钮会跑到屏幕外
        UiStyle.FitToWorkingArea(this, new Size(1080, 640), new Size(880, 520));

        // ---- 顶部横幅（双缓冲，防残影） ----
        _banner = UiStyle.CardPanel(DockStyle.Top);
        _banner.Height = 74;
        var lblTitle = new Label
        {
            AutoSize = true,
            Location = new Point(16, 12),
            Text = "C盘清理工具",
            Font = UiStyle.Font(14f, bold: true),
            ForeColor = UiStyle.Text
        };
        _lblFree = new Label
        {
            AutoSize = true,
            Location = new Point(16, 44),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _lblEstimate = new Label
        {
            AutoSize = true,
            Location = new Point(18, 44),
            Font = UiStyle.Font(9f),
            ForeColor = Color.FromArgb(200, 90, 20)
        };
        _btnRefresh = UiStyle.MakeButton("刷新空间", ButtonVariant.Secondary, 96, () => RefreshFreeSpace());

        _btnDevEnv = UiStyle.MakeButton("开发者环境", ButtonVariant.Primary, 116, () => OpenDevEnvironment());

        _btnSoftware = UiStyle.MakeButton("软件卸载", ButtonVariant.Primary, 116, () => OpenSoftwareUninstall());
        _btnSpace = UiStyle.MakeButton("占用分析", ButtonVariant.Secondary, 96, () => OpenSpaceAnalysis());
        _btnDisk = UiStyle.MakeButton("磁盘分区", ButtonVariant.Secondary, 96, () => OpenDiskPartition());
        _btnLog = UiStyle.MakeButton("操作日志", ButtonVariant.Quiet, 88, () => OpenLogViewer());

        _banner.Controls.Add(lblTitle);
        _banner.Controls.Add(_lblFree);
        _banner.Controls.Add(_lblEstimate);
        _banner.Controls.Add(_btnRefresh);
        _banner.Controls.Add(_btnDevEnv);
        _banner.Controls.Add(_btnSoftware);
        _banner.Controls.Add(_btnSpace);
        _banner.Controls.Add(_btnDisk);
        _banner.Controls.Add(_btnLog);
        _banner.Resize += (_, _) => PositionBanner();
        Shown += (_, _) => PositionBanner();
        Shown += (_, _) => ShowDonateDialog();

        // ---- 底部面板 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 250, Padding = new Padding(8), BackColor = UiStyle.Card, AutoScroll = true };

        // 日志（填充剩余空间）
        _log = new RichTextBox();
        UiStyle.StyleLog(_log);
        _log.Dock = DockStyle.Fill;

        // 进度行
        var progressRow = new Panel { Dock = DockStyle.Top, Height = 52 };
        _progressBar = new ProgressBar
        {
            Location = new Point(12, 10),
            Width = 700
        };
        UiStyle.StyleProgressBar(_progressBar);
        _lblStatus = new Label
        {
            AutoSize = true,
            Location = new Point(12, 34),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _btnCancel = UiStyle.MakeSmallButton("取消", ButtonVariant.Quiet, 72, () => _cts?.Cancel());
        _btnCancel.Enabled = false;
        progressRow.Controls.Add(_progressBar);
        progressRow.Controls.Add(_lblStatus);
        progressRow.Controls.Add(_btnCancel);
        // 全部手动排布（不依赖 Anchor：面板未布局时加入的控件锚点会记录旧边距，导致进度条超宽/按钮错位）
        progressRow.Resize += (_, _) => LayoutProgressRow(progressRow);
        progressRow.SizeChanged += (_, _) => LayoutProgressRow(progressRow);
        LayoutProgressRow(progressRow);

        // 按钮行
        var buttonRow = new Panel { Dock = DockStyle.Top, Height = 44 };
        _btnSelectAll = UiStyle.MakeButton("全选", ButtonVariant.Secondary, 78, () => SetAllChecked(true));
        _btnSelectNone = UiStyle.MakeButton("全不选", ButtonVariant.Secondary, 78, () => SetAllChecked(false));
        _btnAnalyze = UiStyle.MakeButton("分析", ButtonVariant.Primary, 80, async () => await OnAnalyzeAsync());
        _btnClean = UiStyle.MakeButton("清理选中项", ButtonVariant.Danger, 112, async () => await OnCleanAsync());
        _btnExit = UiStyle.MakeButton("退出", ButtonVariant.Quiet, 80, () => Close());
        _btnSelectAll.Location = new Point(0, 6);
        _btnSelectNone.Location = new Point(84, 6);
        _btnAnalyze.Location = new Point(168, 6);
        _btnClean.Location = new Point(254, 6);
        _btnExit.Location = new Point(372, 6);
        buttonRow.Controls.AddRange(new Control[] { _btnSelectAll, _btnSelectNone, _btnAnalyze, _btnClean, _btnExit });

        // 加入底部面板（添加顺序决定 Dock 处理顺序：日志先、进度次之、按钮最后）
        bottom.Controls.Add(_log);
        bottom.Controls.Add(progressRow);
        bottom.Controls.Add(buttonRow);

        // ---- 清理列表（中间填充） ----
        _listView = new ListView();
        UiStyle.StyleListView(_listView);
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.CheckBoxes = true;
        _listView.Columns.Add("清理项", 130);
        _listView.Columns.Add("预估大小", 85, HorizontalAlignment.Right);
        _listView.Columns.Add("位置", 240);
        _listView.Columns.Add("说明", 280);
        _listView.Columns.Add("警告", 200);
        _listView.Resize += (_, _) => AutoFitColumns();
        _listView.DoubleClick += (_, _) => CopySelectedLocation();

        // ---- 组装 ----
        Controls.Add(_listView);
        Controls.Add(bottom);
        Controls.Add(_banner);

        // 处理中遮罩 + 关窗闸门（必须最后创建：要盖在所有控件之上）
        _busyHost = new UiBusyHost(this);
    }

    /// <summary>列宽自适应：“说明”列填满剩余宽度（最小宽度下也不裁剪）。</summary>
    private void AutoFitColumns()
    {
        if (_listView.Columns.Count == 0) return;
        int reserved = 30;
        int fixedSum = _listView.Columns[0].Width + _listView.Columns[1].Width +
                       _listView.Columns[2].Width + _listView.Columns[4].Width;
        // 下限从 240 降到 160：窗口压到最小时（DPI 放大后更容易触发），240 的下限会让总宽超过
        // 客户区，出现横向滚动条并裁掉「说明/警告」两列。
        int auto = Math.Max(160, _listView.ClientSize.Width - fixedSum - reserved);
        _listView.Columns[3].Width = auto;
    }

    /// <summary>双击清理项：把该项的位置复制到剪贴板（便于在资源管理器中定位）。</summary>
    private void CopySelectedLocation()
    {
        if (_listView.SelectedItems.Count == 0) return;
        if (_listView.SelectedItems[0].Tag is not CleanItem item) return;
        if (item.Paths.Count == 0)
        {
            AppendLog($"{item.Name}：该项没有固定位置（如回收站）。");
            return;
        }

        string text = string.Join(Environment.NewLine, item.Paths);
        try
        {
            Clipboard.SetText(text);
            AppendLog($"已复制位置到剪贴板（{item.Paths.Count} 个）：{text.Replace(Environment.NewLine, "、")}");
        }
        catch (Exception ex)
        {
            AppendLog("复制位置失败：" + ex.Message);
        }
    }

    /// <summary>打开“磁盘占用分析”窗口（只读统计，不提供删除）。</summary>
    private void OpenSpaceAnalysis()
    {
        using var form = new SpaceAnalysisForm();
        form.ShowDialog(this);
        RefreshFreeSpace();
    }

    /// <summary>
    /// 打开「磁盘分区（分盘 / 合盘）」窗口：只读检测 + 推荐值 + 《操作说明书》。
    /// 窗口本身不做任何改动（破坏性步骤需用户显式确认后才执行）。
    ///
    /// 磁盘/分区信息受系统 ACL 保护，**必须管理员权限**才能读：因此这里先按需提权，
    /// 用户不同意也仍然打开窗口（窗口里会说明"为什么看不到磁盘"），而不是直接拒绝。
    /// </summary>
    private void OpenDiskPartition()
    {
        if (!ElevationHelper.IsElevated())
        {
            var r = MessageBox.Show(this,
                "磁盘与分区信息受系统保护，读取和执行分区操作都需要管理员权限。\n\n" +
                "是否以管理员身份重新启动本程序？（会弹出 UAC 确认，重启后请重新勾选清理项）\n\n" +
                "点「否」也可以继续打开该窗口，但只能看到「需要管理员权限」的说明。",
                "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                if (ElevationHelper.TryRestartElevated("--open=disk", out string error))
                {
                    Application.Exit();
                    return;
                }
                MessageBox.Show(this, error, "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        using var form = new DiskForm();
        form.ShowDialog(this);
        RefreshFreeSpace();
    }

    private void PositionBanner()
    {
        _lblEstimate.Location = new Point(_lblFree.Right + 24, _lblFree.Top);
        int top = 20;
        _btnRefresh.Location = new Point(_banner.ClientSize.Width - _btnRefresh.Width - 16, top);
        _btnDevEnv.Location = new Point(_btnRefresh.Left - _btnDevEnv.Width - 8, top);
        _btnSoftware.Location = new Point(_btnDevEnv.Left - _btnSoftware.Width - 8, top);
        _btnSpace.Location = new Point(_btnSoftware.Left - _btnSpace.Width - 8, top);
        _btnDisk.Location = new Point(_btnSpace.Left - _btnDisk.Width - 8, top);
        _btnLog.Location = new Point(_btnDisk.Left - _btnLog.Width - 8, top);
    }

    /// <summary>打开“开发者环境检测与清理”窗口。</summary>
    private void OpenDevEnvironment()
    {
        using var form = new DevEnvironmentForm();
        form.ShowDialog(this);
        RefreshFreeSpace();
    }

    /// <summary>打开“软件卸载”窗口。</summary>
    private void OpenSoftwareUninstall()
    {
        using var form = new SoftwareForm();
        form.ShowDialog(this);
        RefreshFreeSpace();
    }

    /// <summary>打开“操作日志”查看窗口。</summary>
    private void OpenLogViewer()
    {
        using var form = new LogViewerForm();
        form.ShowDialog(this);
    }

    /// <summary>
    /// 测试/布局检查专用：抑制 Shown 时自动弹出的捐赠弹窗。
    /// 该弹窗是模态的（ShowDialog），在自动化的屏幕外渲染里会永久阻塞。
    /// </summary>
    internal bool SuppressDonateDialog { get; set; }

    /// <summary>启动时自动弹出腾讯公益捐赠二维码弹窗（按任意键/点击关闭）；图片缺失时跳过。</summary>
    private void ShowDonateDialog()
    {
        if (SuppressDonateDialog) return;
        if (DonateForm.TryLoadImage() is not { } img)
        {
            OperationLog.LogSystem("捐赠弹窗：未找到二维码图片（嵌入资源或 image\\xiaohonghua.jpg），本次跳过显示");
            return;
        }
        using var form = new DonateForm(img);
        form.ShowDialog(this);
    }

    private void ReloadItems()
    {
        _listView.Items.Clear();
        _listView.Groups.Clear();
        _lookup.Clear();

        string[] catOrder = _items.Select(i => i.Category).Distinct().ToArray();
        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var c in catOrder)
        {
            var g = new ListViewGroup(c);
            _listView.Groups.Add(g);
            groups[c] = g;
        }

        foreach (var item in _items)
        {
            var lvi = new ListViewItem(item.Name) { Tag = item, Checked = item.Recommended };
            lvi.SubItems.Add("待分析");
            lvi.SubItems.Add(item.LocationText);
            lvi.SubItems.Add(item.Description);
            lvi.SubItems.Add(item.Warning ?? string.Empty);
            lvi.ToolTipText = item.Paths.Count > 0
                ? string.Join(Environment.NewLine, item.Paths)
                : item.Description;
            lvi.Group = groups[item.Category];
            _listView.Items.Add(lvi);
            _lookup[item.Id] = lvi;
        }
    }

    private void SetAllChecked(bool check)
    {
        foreach (ListViewItem lvi in _listView.Items)
            lvi.Checked = check;
        UpdateEstimate();
    }

    private IEnumerable<CleanItem> SelectedItems()
    {
        foreach (ListViewItem lvi in _listView.CheckedItems)
            if (lvi.Tag is CleanItem item)
                yield return item;
    }

    private void RefreshFreeSpace()
    {
        // 系统盘不一定是 C:（自定义镜像/改过盘符的机器上常见），文案必须跟着实际盘符走，
        // 否则会把 D: 的容量标成「C盘空闲」。
        string root = "系统盘";
        try
        {
            string? r = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrWhiteSpace(r)) root = r.TrimEnd('\\');
        }
        catch (Exception) { /* 忽略 */ }

        try
        {
            var drv = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!);
            string free = SizeFormatter.Format(drv.AvailableFreeSpace);
            string total = SizeFormatter.Format(drv.TotalSize);
            _lblFree.Text = $"{root} 空闲：{free}  /  共 {total}";
        }
        catch (Exception ex)
        {
            _lblFree.Text = $"{root} 空闲：无法读取";
            AppendLog("读取磁盘空间失败：" + ex.Message);
        }
        PositionBanner();
    }

    private void UpdateEstimate()
    {
        long total = 0;
        int checkedNum = 0, analyzedNum = 0;
        foreach (ListViewItem lvi in _listView.CheckedItems)
        {
            if (lvi.Tag is not CleanItem) continue;
            checkedNum++;
            var txt = lvi.SubItems[1].Text;
            if (txt != "待分析")
            {
                analyzedNum++;
                total += ParseSize(txt);
            }
        }

        if (checkedNum == 0)
            _lblEstimate.Text = "未勾选任何项";
        else if (analyzedNum == 0)
            _lblEstimate.Text = "点击「分析」查看预计可释放";
        else
            _lblEstimate.Text = $"预计可释放：{SizeFormatter.Format(total)}（勾选项）";
        PositionBanner();
    }

    private static long ParseSize(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 与 SizeFormatter.Format 一样用不变区域性：否则德语/法语等区域下会把
        // "1.5 GB" 解析成 15 GB 之类的错值（历史实现依赖"两边同区域"这一巧合）。
        if (parts.Length == 0 ||
            !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
            return 0;

        string unit = parts.Length > 1 ? parts[1] : string.Empty;
        return (long)(val * (unit switch
        {
            "TB" => 1024L * 1024 * 1024 * 1024,
            "GB" => 1024L * 1024 * 1024,
            "MB" => 1024L * 1024,
            "KB" => 1024L,
            _ => 1L
        }));
    }

    private async Task OnAnalyzeAsync()
    {
        if (_busy) return;
        var selected = SelectedItems().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请至少勾选一个清理项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "分析中...", BusyRisk.Safe);
        _cts = new CancellationTokenSource();
        try
        {
            var results = await Task.Run(
                () => _engine.AnalyzeAsync(selected, ReportProgress, _cts.Token), _cts.Token);

            foreach (var kv in results)
                if (_lookup.TryGetValue(kv.Key, out var lvi))
                    lvi.SubItems[1].Text = SizeFormatter.Format(kv.Value);

            UpdateEstimate();
            _lblStatus.Text = "分析完成。";
            AppendLog($"分析完成，共 {selected.Length} 项。");
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "已取消分析。";
            AppendLog("分析已取消。");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "分析失败。";
            AppendLog("分析失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task OnCleanAsync()
    {
        if (_busy) return;
        var selected = SelectedItems().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请至少勾选一个清理项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 管理员权限处理：程序默认以普通权限启动，因此这条分支是**日常路径**，
        // 用户取消 UAC 必须能优雅退回，不能抛异常（此前 RestartElevated 会抛 Win32Exception，
        // 而本方法在 try 之外——在标准用户账户上就是点一下弹一个崩溃框）。
        if (selected.Any(i => i.RequiresElevation) && !ElevationHelper.IsElevated())
        {
            var r = MessageBox.Show(this,
                "所选清理项中包含需要管理员权限的项（见列表中的警告列）。\n\n" +
                "是否以管理员身份重启本程序？\n\n" +
                "点「否」则跳过这些系统级项，仅清理无需管理员的项。",
                "需要管理员权限", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
            {
                if (ElevationHelper.TryRestartElevated(null, out string error))
                {
                    Application.Exit();
                    return;
                }
                MessageBox.Show(this, error + "\n\n将继续清理不需要管理员权限的项。",
                    "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("提权未完成：" + error);
            }

            selected = selected.Where(i => !i.RequiresElevation).ToArray();
            if (selected.Length == 0)
            {
                AppendLog("已跳过所有需要管理员权限的项，未执行清理。");
                return;
            }
            AppendLog("已跳过需要管理员权限的系统级项。");
        }

        // 汇总当前预估（取列表中已分析的大小）
        long est = 0;
        foreach (var item in selected)
            if (_lookup.TryGetValue(item.Id, out var lvi))
                est += ParseSize(lvi.SubItems[1].Text);

        var confirm = MessageBox.Show(this,
            $"将清理 {selected.Length} 项，预计释放约 {SizeFormatter.Format(est)}。\n\n" +
            "部分清理项（回收站等）为永久删除，请确认后继续。",
            "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true, "清理中...", BusyRisk.Interrupt);
        _cts = new CancellationTokenSource();
        try
        {
            var summary = await Task.Run(
                () => _engine.CleanAsync(selected, ReportProgress, _cts.Token), _cts.Token);

            _lblStatus.Text = $"清理完成，释放 {SizeFormatter.Format(summary.FreedBytes)}。";
            AppendLog($"清理完成：释放 {SizeFormatter.Format(summary.FreedBytes)}，共 {summary.ItemsCount} 项。");
            AppendLog(summary.ErrorSummary());
            OperationLog.LogClean(selected.Length, summary.FreedBytes, summary.Errors.Count);

            // 清理后重新扫描：更新各清理项的最新剩余大小与空闲空间
            _lblStatus.Text = "清理完成，重新扫描剩余大小...";
            await ReAnalyzeAsync(selected);
            RefreshFreeSpace();
            AppendLog("已重新扫描，清理项剩余大小为最新状态。");
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "已取消清理。";
            AppendLog("清理已取消，重新扫描剩余大小...");
            OperationLog.Log("清理", $"已取消（{selected.Length} 项未全部完成）。");
            await ReAnalyzeAsync(selected);   // 部分清理也已生效，刷新为最新状态
            RefreshFreeSpace();
            AppendLog("已重新扫描，清理项剩余大小为最新状态。");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "清理失败。";
            AppendLog("清理失败：" + ex.Message);
            OperationLog.Log("清理", "失败：" + ex.Message);
            await ReAnalyzeAsync(selected);
            RefreshFreeSpace();
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>重新分析选中的清理项并刷新列表中的“预估大小”列（展示清理后实际剩余）。</summary>
    private async Task ReAnalyzeAsync(IReadOnlyList<CleanItem> items)
    {
        if (items.Count == 0) return;
        try
        {
            var results = await Task.Run(
                () => _engine.AnalyzeAsync(items, ReportProgress, CancellationToken.None), CancellationToken.None);
            foreach (var kv in results)
                if (_lookup.TryGetValue(kv.Key, out var lvi))
                    lvi.SubItems[1].Text = SizeFormatter.Format(kv.Value);
            UpdateEstimate();
        }
        catch (Exception ex)
        {
            AppendLog("重新扫描剩余大小失败：" + ex.Message);
        }
    }

    /// <summary>进度行手动布局：进度条拉伸到「取消」按钮左侧，按钮靠右（避免 Anchor 旧边距错位）。</summary>
    private void LayoutProgressRow(Panel row)
    {
        int w = row.ClientSize.Width;
        _btnCancel.Left = w - _btnCancel.Width - 12;
        _btnCancel.Top = 10;
        _progressBar.Left = 12;
        _progressBar.Top = 10;
        _progressBar.Width = Math.Max(200, _btnCancel.Left - _progressBar.Left - 12);
    }

    private void ReportProgress(int current, int total)
    {
        Ui(() =>
        {
            _progressBar.Maximum = Math.Max(total, 1);
            _progressBar.Value = Math.Min(current, _progressBar.Maximum);
            _lblStatus.Text = $"进行中 {current}/{total} ...";
            // 遮罩上的动画文案跟着走，用户才看得到进展
            if (_busyLabel.Length > 0)
                _busyHost.Update($"{_busyLabel}　{current}/{total}");
        });
    }

    /// <summary>
    /// 统一设置"忙"状态。
    ///
    /// <paramref name="risk"/> 决定遮罩与关窗行为：只读分析传 <see cref="BusyRisk.Safe"/>（随时可取消、可关窗）；
    /// 「清理选中项」会真的删除文件，传 <see cref="BusyRisk.Interrupt"/>——仍可取消，
    /// 但关闭窗口前会先确认一次"会停在半途"。这两类都不会留下坏状态，所以不锁死窗口。
    /// </summary>
    private void SetBusy(bool busy, string? status, BusyRisk risk = BusyRisk.Safe)
    {
        _busy = busy;
        _btnAnalyze.Enabled = !busy;
        _btnClean.Enabled = !busy;
        _btnSelectAll.Enabled = !busy;
        _btnSelectNone.Enabled = !busy;
        _btnCancel.Enabled = busy && risk != BusyRisk.Critical;
        if (status != null)
        {
            _lblStatus.Text = status;
            _busyLabel = status.TrimEnd('.', '…', '。');
        }
        if (busy)
        {
            _progressBar.Value = 0;
            _busyHost.Begin(status ?? "处理中…", risk, () => _cts?.Cancel());
        }
        else
        {
            _busyHost.End();
            _busyLabel = string.Empty;
        }
    }

    private void AppendLog(string text)
    {
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            // 检查与调用之间存在窗口被销毁的窗口期：BeginInvoke 会抛 InvalidOperationException/
            // ObjectDisposedException，异常会顺着 await 冒到"清理失败"提示上，把一次正常清理报成失败。
            if (!IsHandleCreated) return;
            // ObjectDisposedException 派生自 InvalidOperationException，一个 catch 就够
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { /* 窗口正在关闭/句柄已销毁 */ }
        }
        else action();
    }
}
