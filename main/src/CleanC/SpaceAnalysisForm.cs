using CleanC.Core;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 磁盘占用分析窗口（只读）：按大小排名目录，双击逐层下钻；
/// 被深度上限折叠的目录按深度 2 重新分析后展开；联接/符号链接与无权限目录跳过并计数。
/// 本窗口仅统计，不提供任何删除入口。
/// </summary>
public sealed class SpaceAnalysisForm : Form
{
    /// <summary>根路径下拉框的选项；Path 为空串表示「选择目录…」哨兵项。</summary>
    private sealed class RootEntry
    {
        public RootEntry(string path, string text)
        {
            Path = path;
            Text = text;
        }

        /// <summary>该选项对应的根路径（哨兵项为空串）。</summary>
        public string Path { get; }

        /// <summary>下拉框中显示的文字（盘符/路径 + 卷标 + 已用/可用）。</summary>
        public string Text { get; }

        public override string ToString() => Text;
    }

    private const string BrowseText = "选择目录…";
    private const int PathColIndex = 5;   // “路径”列自动填满剩余宽度

    private ListView _listView = null!;
    private Panel _banner = null!;
    private Panel _navRow = null!;
    private ComboBox _rootCombo = null!;
    private ComboBox _depthCombo = null!;
    private ChipLabel _lblSummary = null!;
    private Label _lblCrumb = null!;
    private Label _lblStatus = null!;
    private ProgressBar _progressBar = null!;
    private RichTextBox _log = null!;
    private RoundedButton _btnBack = null!;
    private RoundedButton _btnStart = null!;
    private RoundedButton _btnCancel = null!;
    private RoundedButton _btnUp = null!;
    private RoundedButton _btnRoot = null!;
    private RoundedButton _btnCopyPath = null!;
    private RoundedButton _btnExport = null!;

    /// <summary>每一层视图（视图路径 + 该层所用快照），用于「上一级」回溯。</summary>
    private readonly Stack<(string Path, SpaceSnapshot Snap)> _history = new();

    private SpaceSnapshot? _snapshot;
    private string _viewPath = string.Empty;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private UiBusyHost _busyHost = null!;
    private string _busyLabel = string.Empty;
    private bool _updatingRootCombo;
    private int _lastRootIndex;
    private int _lastProgressSecond = -1;

    /// <param name="initialRoot">可选的初始根路径（磁盘或目录）；为空时默认选中系统盘。</param>
    public SpaceAnalysisForm(string? initialRoot = null)
    {
        Text = "磁盘占用分析";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(1120, 720), new Size(980, 620));

        InitializeUi();
        PopulateRoots(initialRoot);
        AppendLog("提示：选择根路径与深度后点「开始分析」；双击列表中的目录可逐层下钻。本窗口只读，不提供删除。");
        AppendLog("说明：硬链接（WinSxS）、压缩/去重存储按逻辑大小统计，可能高于实际占用；无权限目录与联接（junction）跳过并计数。");
    }

    private void InitializeUi()
    {
        // ---------- 顶部横幅（双缓冲，防残影） ----------
        _banner = UiStyle.CardPanel(DockStyle.Top);
        _banner.Height = 104;
        var banner = _banner;

        var lblTitle = new Label
        {
            AutoSize = true,
            Location = new Point(16, 8),
            Text = "磁盘占用分析",
            Font = UiStyle.Font(14f, bold: true),
            ForeColor = UiStyle.Text
        };
        var lblHint = new Label
        {
            AutoSize = true,
            Location = new Point(16, 42),
            Text = "只读统计：按大小排名目录，双击逐层下钻；本窗口不提供任何删除操作。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };

        var lblRoot = new Label
        {
            AutoSize = true,
            Location = new Point(16, 74),
            Text = "根路径",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _rootCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(74, 68),
            Size = new Size(300, 30),
            FlatStyle = FlatStyle.Flat
        };
        _rootCombo.SelectedIndexChanged += (_, _) => OnRootSelectionChanged();

        var lblDepth = new Label
        {
            AutoSize = true,
            Location = new Point(388, 74),
            Text = "深度",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _depthCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(430, 68),
            Size = new Size(64, 30),
            FlatStyle = FlatStyle.Flat
        };
        _depthCombo.Items.AddRange(new object[] { "2", "3", "4", "5" });
        _depthCombo.SelectedIndex = 2;   // 默认深度 4

        _btnStart = UiStyle.MakeButton("开始分析", ButtonVariant.Primary, 80, async () => await OnAnalyzeAsync());
        _btnStart.Location = new Point(508, 66);
        _btnCancel = UiStyle.MakeSmallButton("取消", ButtonVariant.Quiet, 72, () => _cts?.Cancel());
        _btnCancel.Location = new Point(596, 69);
        _btnCancel.Enabled = false;

        _btnBack = UiStyle.MakeButton("返回", ButtonVariant.Quiet, 84, () => Close());
        _btnBack.Location = new Point(0, 6);
        _lblSummary = new ChipLabel("等待分析", UiStyle.HoverBg, UiStyle.Primary)
        {
            Location = new Point(0, 6)
        };

        banner.Controls.AddRange(new Control[]
        {
            lblTitle, lblHint, lblRoot, _rootCombo, lblDepth, _depthCombo,
            _btnStart, _btnCancel, _lblSummary, _btnBack
        });
        banner.Resize += (_, _) => LayoutBanner();
        Shown += (_, _) => LayoutBanner();

        // ---------- 导航行（面包屑 + 上一级/回到根/复制路径/导出 CSV） ----------
        _navRow = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = UiStyle.Card };
        _lblCrumb = new Label
        {
            AutoSize = false,
            Location = new Point(16, 9),
            Height = 28,
            Text = "（尚未分析，请点「开始分析」）",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _btnUp = UiStyle.MakeSmallButton("上一级", ButtonVariant.Secondary, 76, OnGoUp);
        _btnRoot = UiStyle.MakeSmallButton("回到根", ButtonVariant.Secondary, 76, OnGoRoot);
        _btnCopyPath = UiStyle.MakeSmallButton("复制路径", ButtonVariant.Secondary, 88, OnCopyPath);
        _btnExport = UiStyle.MakeSmallButton("导出 CSV", ButtonVariant.Secondary, 92, OnExportCsv);
        _navRow.Controls.AddRange(new Control[] { _lblCrumb, _btnUp, _btnRoot, _btnCopyPath, _btnExport });
        // 全部手动排布（不依赖 Anchor：面板未布局时加入的控件锚点会记录旧边距，导致按钮错位）
        _navRow.Resize += (_, _) => LayoutNavRow();
        _navRow.SizeChanged += (_, _) => LayoutNavRow();
        LayoutNavRow();

        // ---------- 列表（中间填充） ----------
        _listView = new ListView();
        UiStyle.StyleListView(_listView);
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.Columns.Add("大小", 90, HorizontalAlignment.Right);
        _listView.Columns.Add("占比", 64, HorizontalAlignment.Right);
        _listView.Columns.Add("文件数", 80, HorizontalAlignment.Right);
        _listView.Columns.Add("子目录", 76, HorizontalAlignment.Right);
        _listView.Columns.Add("最后修改", 110);
        _listView.Columns.Add("路径", 320);
        _listView.Resize += (_, _) => AutoFitColumns();
        _listView.DoubleClick += async (_, _) => await OnDrillDownAsync();

        // ---------- 底部面板（进度条 + 状态 + 日志） ----------
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 190,
            Padding = new Padding(8),
            BackColor = UiStyle.Card
        };

        _log = new RichTextBox();
        UiStyle.StyleLog(_log);
        _log.Dock = DockStyle.Fill;

        var progressRow = new Panel { Dock = DockStyle.Top, Height = 52 };
        _progressBar = new ProgressBar
        {
            Location = new Point(12, 10),
            Style = ProgressBarStyle.Continuous
        };
        UiStyle.StyleProgressBar(_progressBar);
        _lblStatus = new Label
        {
            AutoSize = true,
            Location = new Point(12, 34),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        progressRow.Controls.AddRange(new Control[] { _progressBar, _lblStatus });
        // 全部手动排布（不依赖 Anchor：面板未布局时加入的控件锚点会记录旧边距，导致进度条超宽）
        progressRow.Resize += (_, _) => LayoutProgressRow(progressRow);
        progressRow.SizeChanged += (_, _) => LayoutProgressRow(progressRow);
        LayoutProgressRow(progressRow);

        bottom.Controls.Add(_log);
        bottom.Controls.Add(progressRow);

        // ---------- 底部固定提示条 ----------
        var warnStrip = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = UiStyle.WarningBg };
        var lblWarn = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "本窗口仅统计，不提供删除；硬链接（WinSxS）、压缩/去重存储按逻辑大小统计，可能高于实际占用；" +
                   "无权限目录与联接（junction）已跳过并计数。如需清理请使用主界面 / 开发者环境 / 软件卸载窗口。",
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.WarningText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18, 0, 18, 0),
            AutoEllipsis = true
        };
        warnStrip.Controls.Add(lblWarn);

        // ---------- 组装（Dock：先 Fill 后 Top/Bottom；后加入的同边控件更靠外边缘） ----------
        Controls.Add(_listView);
        Controls.Add(bottom);
        Controls.Add(warnStrip);
        Controls.Add(_navRow);
        Controls.Add(_banner);

        FormClosing += (_, _) => _cts?.Cancel();

        // 处理中遮罩（最后创建，盖在所有控件之上）
        _busyHost = new UiBusyHost(this);
    }

    /// <summary>横幅右上角手动布局：返回按钮贴右，汇总胶囊在其左侧（不依赖 Anchor，避免残影/重叠）。</summary>
    private void LayoutBanner()
    {
        if (_btnBack == null || _lblSummary == null) return;
        _btnBack.Left = _banner.ClientSize.Width - _btnBack.Width - 16;
        _btnBack.Top = 6;
        _lblSummary.Left = Math.Max(150, _btnBack.Left - _lblSummary.Width - 12);
        _lblSummary.Top = 6;
        _banner.Invalidate(true);
    }

    /// <summary>导航行手动布局：面包屑占左侧剩余宽度，四个按钮靠右依次排列。</summary>
    private void LayoutNavRow()
    {
        int right = _navRow.ClientSize.Width - 16;
        foreach (var b in new[] { _btnExport, _btnCopyPath, _btnRoot, _btnUp })
        {
            b.Left = right - b.Width;
            b.Top = 9;
            right = b.Left - 8;
        }
        _lblCrumb.Width = Math.Max(120, _btnUp.Left - _lblCrumb.Left - 12);
    }

    /// <summary>进度行手动布局：进度条拉伸到面板右侧留白（避免 Anchor 旧边距错位）。</summary>
    private void LayoutProgressRow(Panel row)
    {
        _progressBar.Left = 12;
        _progressBar.Top = 10;
        _progressBar.Width = Math.Max(200, row.ClientSize.Width - 24);
    }

    /// <summary>列宽自适应：“路径”列填满剩余宽度（最小宽度下也不裁剪）。</summary>
    private void AutoFitColumns()
    {
        if (_listView.Columns.Count == 0) return;
        int reserved = 26;   // 垂直滚动条 + 边距
        int fixedSum = 0;
        for (int i = 0; i < _listView.Columns.Count; i++)
            if (i != PathColIndex)
                fixedSum += _listView.Columns[i].Width;
        int auto = Math.Max(200, _listView.ClientSize.Width - fixedSum - reserved);
        _listView.Columns[PathColIndex].Width = auto;
    }

    /// <summary>更新汇总胶囊并立即重新排布（宽度变化会移动左侧定位）。</summary>
    private void UpdateSummary(string text)
    {
        _lblSummary.SetText(text);
        LayoutBanner();
    }

    // ---------- 根路径选择 ----------

    /// <summary>填充根路径下拉框：所有就绪的固定磁盘 + 「选择目录…」哨兵项；随后按需选中 initialRoot。</summary>
    private void PopulateRoots(string? initialRoot)
    {
        var drives = EnumerateFixedDrives();

        _updatingRootCombo = true;
        try
        {
            _rootCombo.Items.Clear();
            foreach (var d in drives)
                _rootCombo.Items.Add(d);
            _rootCombo.Items.Add(new RootEntry(string.Empty, BrowseText));
            _rootCombo.SelectedIndex = 0;
            _lastRootIndex = 0;
        }
        finally
        {
            _updatingRootCombo = false;
        }

        if (drives.Count == 0)
            AppendLog("未检测到就绪的固定磁盘，请用「选择目录…」手动指定要分析的目录。");

        if (!string.IsNullOrWhiteSpace(initialRoot))
            SelectRoot(initialRoot!);
        else
            SelectSystemDrive(drives);
    }

    /// <summary>默认选中系统盘（存在时）；否则保留第一个盘符。</summary>
    private void SelectSystemDrive(List<RootEntry> drives)
    {
        string? systemRoot = null;
        try { systemRoot = Path.GetPathRoot(Environment.SystemDirectory); }
        catch (Exception) { /* 取不到系统盘根：忽略 */ }
        if (string.IsNullOrEmpty(systemRoot)) return;

        for (int i = 0; i < drives.Count; i++)
        {
            if (!string.Equals(drives[i].Path, systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
            _updatingRootCombo = true;
            try
            {
                _rootCombo.SelectedIndex = i;
                _lastRootIndex = i;
            }
            finally
            {
                _updatingRootCombo = false;
            }
            return;
        }
    }

    /// <summary>选中指定根路径；不在列表中时插入到「选择目录…」之前（路径无效则写日志并保留原选项）。</summary>
    private void SelectRoot(string path)
    {
        string full;
        try
        {
            // 裸盘符（如 "C:"）补上根分隔符，避免被解析为“该盘当前目录”
            string input = path.Trim();
            if (input.Length == 2 && input[1] == ':') input += '\\';
            full = Path.GetFullPath(input);
        }
        catch (Exception ex)
        {
            AppendLog("根路径无效，已忽略：" + ex.Message);
            return;
        }

        if (!Directory.Exists(full))
        {
            AppendLog("根路径不存在，已保留默认磁盘：" + full);
            return;
        }

        int index = FindRootIndex(full);
        _updatingRootCombo = true;
        try
        {
            if (index < 0)
            {
                index = Math.Max(0, _rootCombo.Items.Count - 1);   // 插到哨兵项之前
                _rootCombo.Items.Insert(index, new RootEntry(full, full));
            }
            _rootCombo.SelectedIndex = index;
            _lastRootIndex = index;
        }
        finally
        {
            _updatingRootCombo = false;
        }
    }

    /// <summary>在根路径列表中查找路径所在下标（忽略大小写；未找到返回 -1）。</summary>
    private int FindRootIndex(string path)
    {
        for (int i = 0; i < _rootCombo.Items.Count; i++)
            if (_rootCombo.Items[i] is RootEntry e && !string.IsNullOrEmpty(e.Path) &&
                string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>当前选中的根路径（哨兵项或未选择时返回 null）。</summary>
    private string? CurrentRootPath()
        => _rootCombo.SelectedItem is RootEntry e && !string.IsNullOrEmpty(e.Path) ? e.Path : null;

    /// <summary>根路径下拉框选择变化：选中「选择目录…」时弹目录选择框，取消则回退到上一项。</summary>
    private void OnRootSelectionChanged()
    {
        if (_updatingRootCombo) return;
        if (_rootCombo.SelectedItem is not RootEntry entry) return;

        if (!string.IsNullOrEmpty(entry.Path))
        {
            _lastRootIndex = _rootCombo.SelectedIndex;
            return;
        }

        // 哨兵项：手动选择目录
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择要统计占用的目录（只读统计，不会修改或删除任何文件）",
            ShowNewFolderButton = false
        };
        // 以「上一次选中的真实路径」作为选择框的起始位置
        if (_lastRootIndex >= 0 && _lastRootIndex < _rootCombo.Items.Count &&
            _rootCombo.Items[_lastRootIndex] is RootEntry last && !string.IsNullOrEmpty(last.Path))
            dlg.SelectedPath = last.Path;

        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            SelectRoot(dlg.SelectedPath);
            AppendLog("已选择根路径：" + dlg.SelectedPath);
            return;
        }

        // 取消：回退到上一项
        _updatingRootCombo = true;
        try
        {
            _rootCombo.SelectedIndex = Math.Clamp(_lastRootIndex, 0, _rootCombo.Items.Count - 1);
        }
        finally
        {
            _updatingRootCombo = false;
        }
    }

    /// <summary>枚举就绪的固定磁盘（卷标/容量不可读时只显示盘符；单个盘符异常跳过，不抛出）。</summary>
    private static List<RootEntry> EnumerateFixedDrives()
    {
        var list = new List<RootEntry>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return list;
        }

        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string name = d.Name;

                string label = string.Empty;
                try { label = d.VolumeLabel ?? string.Empty; }
                catch (Exception) { /* 卷标不可读：忽略 */ }

                string usage = string.Empty;
                try
                {
                    usage = $"已用 {SizeFormatter.Format(d.TotalSize - d.AvailableFreeSpace)}" +
                            $" · 可用 {SizeFormatter.Format(d.AvailableFreeSpace)}";
                }
                catch (Exception) { /* 容量不可读：忽略 */ }

                string text = name;
                if (!string.IsNullOrWhiteSpace(label)) text += $"（{label}）";
                if (!string.IsNullOrEmpty(usage)) text += "　" + usage;
                list.Add(new RootEntry(name, text));
            }
            catch (Exception)
            {
                // 单个盘符不可读：跳过
            }
        }
        return list;
    }

    /// <summary>当前选择的深度（解析失败时按默认 4）。</summary>
    private int SelectedDepth()
        => _depthCombo.SelectedItem is string s && int.TryParse(s, out int d) ? Math.Clamp(d, 1, 8) : 4;

    // ---------- 分析 ----------

    /// <summary>执行一次分析：后台线程统计，进度与结果经 Ui(...) 回贴；支持取消。</summary>
    private async Task OnAnalyzeAsync()
    {
        if (_busy) return;

        string? root = CurrentRootPath();
        if (string.IsNullOrEmpty(root))
        {
            MessageBox.Show(this, "请先选择要分析的磁盘或目录。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Directory.Exists(root))
        {
            AppendLog("根路径不存在，无法分析：" + root);
            _lblStatus.Text = "根路径不存在。";
            return;
        }

        int depth = SelectedDepth();
        _lastProgressSecond = -1;
        SetBusy(true, "分析中...", marquee: true);
        AppendLog($"开始分析：{root}（深度 {depth}）");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var options = new AnalyzeOptions { MaxDepth = depth };
            var snap = await SpaceAnalyzer.AnalyzeAsync(root, options,
                p => Ui(() => ReportProgress(p)), ct);
            ct.ThrowIfCancellationRequested();

            _history.Clear();
            _snapshot = snap;
            _viewPath = snap.Root;
            ReloadView();
            LogSnapshot(snap, "分析完成");
            _lblStatus.Text = FormattableString.Invariant(
                $"分析完成：{SizeFormatter.Format(snap.TotalBytes)}，用时 {snap.ElapsedMs / 1000.0:0.#}s。");
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "分析已取消。";
            AppendLog(_snapshot == null ? "分析已取消（无结果）。" : "分析已取消，保留上次结果。");
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

    /// <summary>进度回贴：引擎每 2 万文件回调一次；日志只在“秒”变化时写，避免刷屏。</summary>
    private void ReportProgress(SpaceProgress p)
    {
        int second = p.ElapsedMs / 1000;
        _lblStatus.Text = $"分析中... 已统计 {p.Files:N0} 个文件 / {SizeFormatter.Format(p.Bytes)}，用时 {second}s";
        if (second == _lastProgressSecond) return;
        _lastProgressSecond = second;
        AppendLog($"　进度：{p.Files:N0} 个文件 / {SizeFormatter.Format(p.Bytes)}（{second}s）");
    }

    /// <summary>把一次分析的统计写入日志（用时/合计/文件数/目录数/无权限/重解析点/截断提示）。</summary>
    private void LogSnapshot(SpaceSnapshot snap, string prefix)
    {
        AppendLog($"{prefix}：{snap.Root}");
        AppendLog(FormattableString.Invariant(
            $"　用时 {snap.ElapsedMs / 1000.0:0.#}s · 合计 {SizeFormatter.Format(snap.TotalBytes)} · {snap.FileCount:N0} 个文件 · {snap.DirCount:N0} 个目录"));
        if (snap.VolumeTotalBytes > 0)
        {
            if (snap.UnaccountedBytes >= 0)
                AppendLog($"　卷容量 {SizeFormatter.Format(snap.VolumeTotalBytes)}" +
                          $" · 可用 {SizeFormatter.Format(snap.VolumeFreeBytes)}" +
                          $" · 已用中未统计到 {SizeFormatter.Format(snap.UnaccountedBytes)}（无权限/卷元数据等）");
            else
                AppendLog($"　卷容量 {SizeFormatter.Format(snap.VolumeTotalBytes)}" +
                          $" · 可用 {SizeFormatter.Format(snap.VolumeFreeBytes)}" +
                          $" · 统计值高于卷已用 {SizeFormatter.Format(-snap.UnaccountedBytes)}" +
                          "（硬链接/压缩/去重存储按逻辑大小统计，属正常）");
        }
        if (snap.Denied > 0)
            AppendLog($"　无权限目录 {snap.Denied} 个（已跳过，实际合计会偏小）。");
        if (snap.SkippedReparse > 0)
            AppendLog($"　跳过的重解析点（联接/符号链接）{snap.SkippedReparse:N0} 个。");
        if (snap.Truncated)
            AppendLog("　结果被上限截断，当前数值为下界。");
        if (snap.TotalBytes == 0)
            AppendLog("　未统计到任何文件（目录为空或全部无权限）。");
    }

    // ---------- 视图与导航 ----------

    /// <summary>按当前视图刷新列表、面包屑、汇总与导航按钮。</summary>
    private void ReloadView()
    {
        var snap = _snapshot;
        if (snap == null)
        {
            _listView.Items.Clear();
            _lblCrumb.Text = "（尚未分析，请点「开始分析」）";
            UpdateNavigationState();
            return;
        }

        _lblCrumb.Text = _viewPath;
        UpdateSummary(SummaryText(snap));

        long total = snap.TotalBytes;
        int folded = 0;
        _listView.BeginUpdate();
        try
        {
            _listView.Items.Clear();

            // 根视图补一行「（根目录直属文件）」：pagefile.sys、hiberfil.sys 等（该行不可下钻）
            if (IsRootView(snap) && snap.RootOwnBytes > 0)
                _listView.Items.Add(MakeOwnRow(snap, total));

            foreach (var node in snap.ChildrenOf(_viewPath))
            {
                if (node.IsFolded && node.Children.Count == 0) folded++;
                string tip = node.Path +
                             (node.IsFolded && node.Children.Count == 0 ? "（已达深度上限，双击展开下一层）" : string.Empty);
                var lvi = new ListViewItem(SizeFormatter.Format(node.TotalBytes))
                {
                    Tag = node,
                    ToolTipText = tip
                };
                lvi.SubItems.Add(PercentText(node.TotalBytes, total));
                lvi.SubItems.Add(node.Files.ToString("N0"));
                lvi.SubItems.Add(node.Dirs.ToString("N0"));
                lvi.SubItems.Add(LastWriteText(node.Path));
                lvi.SubItems.Add(node.Path);
                _listView.Items.Add(lvi);
            }
        }
        finally
        {
            _listView.EndUpdate();
        }

        if (_listView.Items.Count == 0)
            _lblStatus.Text = "当前层没有可显示的目录（该目录可能只有直属文件、为空，或子目录已在更深层）。";
        else if (folded > 0)
            _lblStatus.Text = $"当前层有 {folded} 个目录已达深度上限，双击可展开其下一层。";
        else
            _lblStatus.Text = $"当前层共 {_listView.Items.Count} 行，双击目录逐层下钻。";

        AutoFitColumns();
        UpdateNavigationState();
    }

    /// <summary>汇总文本：合计 + 文件数 + 目录数 + 用时，并在有跳过/截断时附加说明。</summary>
    private static string SummaryText(SpaceSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.Append($"合计 {SizeFormatter.Format(snap.TotalBytes)}");
        sb.Append($" · {snap.FileCount:N0} 个文件");
        sb.Append($" · {snap.DirCount:N0} 个目录");
        sb.Append(FormattableString.Invariant($" · 用时 {snap.ElapsedMs / 1000.0:0.#}s"));
        if (snap.Denied > 0) sb.Append($" · 无权限 {snap.Denied} 个目录");
        if (snap.SkippedReparse > 0) sb.Append($" · 跳过 {snap.SkippedReparse:N0} 个重解析点");
        if (snap.Truncated) sb.Append(" · 已截断（下界）");
        return sb.ToString();
    }

    /// <summary>根视图的「（根目录直属文件）」行：表示 RootOwnBytes，Tag 为 null 故不可下钻。</summary>
    private static ListViewItem MakeOwnRow(SpaceSnapshot snap, long total)
    {
        var lvi = new ListViewItem(SizeFormatter.Format(snap.RootOwnBytes))
        {
            Tag = null,
            ToolTipText = "根目录直属文件（不含子目录），不可下钻",
            ForeColor = UiStyle.SubText
        };
        lvi.SubItems.Add(PercentText(snap.RootOwnBytes, total));
        lvi.SubItems.Add("—");
        lvi.SubItems.Add("—");
        lvi.SubItems.Add(LastWriteText(snap.Root));
        lvi.SubItems.Add("（根目录直属文件）");
        return lvi;
    }

    /// <summary>占比文本（相对当前快照合计）。</summary>
    private static string PercentText(long bytes, long total)
        => total > 0 ? FormattableString.Invariant($"{(double)bytes / total * 100:0.0}%") : "—";

    /// <summary>目录最后修改时间（仅对当前显示的行读取，异常返回「—」）。</summary>
    private static string LastWriteText(string path)
    {
        try
        {
            return TimeText.Minute(Directory.GetLastWriteTime(path));
        }
        catch (Exception)
        {
            return "—";
        }
    }

    /// <summary>当前视图是否根视图。</summary>
    private bool IsRootView(SpaceSnapshot snap)
        => string.Equals(_viewPath, snap.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>双击列表行：折叠节点按深度 2 重新分析后展开，否则用快照子节点直接下钻。</summary>
    private async Task OnDrillDownAsync()
    {
        if (_busy) return;
        var snap = _snapshot;
        if (snap == null) return;
        if (_listView.SelectedItems.Count == 0) return;

        if (_listView.SelectedItems[0].Tag is not DirNode node)
        {
            _lblStatus.Text = "该行表示根目录直属文件，不能下钻。";
            return;
        }

        if (node.IsFolded && node.Children.Count == 0)
        {
            await ExpandFoldedAsync(snap, node);
            return;
        }

        _history.Push((_viewPath, snap));
        _viewPath = node.Path;
        ReloadView();
        AppendLog($"下钻：{node.Path}（{SizeFormatter.Format(node.TotalBytes)}）");
        _lblStatus.Text = $"已下钻到：{node.Path}";
    }

    /// <summary>展开被深度上限折叠的目录：对该路径重新分析（深度 2），并把结果作为新视图。</summary>
    private async Task ExpandFoldedAsync(SpaceSnapshot current, DirNode node)
    {
        _lastProgressSecond = -1;
        SetBusy(true, $"展开 {node.Name} ...", marquee: true);
        AppendLog($"展开折叠目录：{node.Path}（按深度 2 重新分析）");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var options = new AnalyzeOptions { MaxDepth = 2 };
            var snap = await SpaceAnalyzer.AnalyzeAsync(node.Path, options,
                p => Ui(() => ReportProgress(p)), ct);
            ct.ThrowIfCancellationRequested();

            _history.Push((_viewPath, current));
            _snapshot = snap;
            _viewPath = snap.Root;
            ReloadView();
            LogSnapshot(snap, "展开完成");
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "展开已取消。";
            AppendLog("展开已取消。");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "展开失败。";
            AppendLog("展开失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>返回上一级视图（由导航栈回溯）。</summary>
    private void OnGoUp()
    {
        if (_busy) return;
        if (_history.Count == 0 || _snapshot == null)
        {
            _lblStatus.Text = "已在最上层视图。";
            return;
        }

        var (path, snap) = _history.Pop();
        _snapshot = snap;
        _viewPath = path;
        ReloadView();
        AppendLog("返回上一级：" + path);
    }

    /// <summary>回到根视图（清空导航栈）。</summary>
    private void OnGoRoot()
    {
        if (_busy) return;
        var snap = _snapshot;
        if (snap == null)
        {
            _lblStatus.Text = "尚未分析，请先点「开始分析」。";
            return;
        }
        if (IsRootView(snap))
        {
            _lblStatus.Text = "已在根视图。";
            return;
        }

        _history.Clear();
        _viewPath = snap.Root;
        ReloadView();
        AppendLog("已回到根视图：" + snap.Root);
    }

    /// <summary>按当前视图与导航栈更新「上一级 / 回到根」的可用性。</summary>
    private void UpdateNavigationState()
    {
        var snap = _snapshot;
        _btnUp.Enabled = !_busy && _history.Count > 0;
        _btnRoot.Enabled = !_busy && snap != null && !IsRootView(snap);
    }

    // ---------- 复制与导出 ----------

    /// <summary>选中行的路径（未选中或为「根目录直属文件」行时返回当前视图路径）。</summary>
    private string? SelectedPath()
    {
        if (_listView.SelectedItems.Count == 0) return _viewPath;
        return _listView.SelectedItems[0].Tag is DirNode node ? node.Path : _viewPath;
    }

    /// <summary>复制选中行的路径到剪贴板（无选中时复制当前视图路径；失败只写日志）。</summary>
    private void OnCopyPath()
    {
        if (_busy) return;
        string? path = SelectedPath();
        if (string.IsNullOrEmpty(path))
        {
            _lblStatus.Text = "暂无可复制的路径（请先分析）。";
            return;
        }

        try
        {
            Clipboard.SetText(path);
            _lblStatus.Text = "已复制路径：" + path;
            AppendLog("已复制路径：" + path);
        }
        catch (Exception ex)
        {
            // 剪贴板被其它程序占用等：写日志而不弹异常
            AppendLog("复制路径失败：" + ex.Message);
        }
    }

    /// <summary>把当前视图导出为 CSV（大小/占比/文件数/子目录/最后修改/路径，UTF-8 带 BOM）。</summary>
    private void OnExportCsv()
    {
        if (_busy) return;
        var snap = _snapshot;
        if (snap == null)
        {
            MessageBox.Show(this, "请先执行一次分析。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_listView.Items.Count == 0)
        {
            _lblStatus.Text = "当前视图没有可导出的行。";
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "导出当前视图（CSV）",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"CleanC-占用分析-{SanitizeFileName(snap.Root)}-{TimeText.CompactNow()}.csv",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("大小,占比,文件数,子目录,最后修改,路径");
            foreach (ListViewItem lvi in _listView.Items)
            {
                string path = lvi.Tag is DirNode node ? node.Path : _viewPath;
                sb.Append(Csv(lvi.SubItems[0].Text)).Append(',')
                  .Append(Csv(lvi.SubItems[1].Text)).Append(',')
                  .Append(Csv(lvi.SubItems[2].Text)).Append(',')
                  .Append(Csv(lvi.SubItems[3].Text)).Append(',')
                  .Append(Csv(lvi.SubItems[4].Text)).Append(',')
                  .Append(Csv(path)).AppendLine();
            }

            // 带 BOM 的 UTF-8：Excel 直接双击打开不乱码
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            _lblStatus.Text = "已导出：" + dlg.FileName;
            AppendLog($"已导出 CSV：{dlg.FileName}（{_listView.Items.Count} 行，当前视图 {_viewPath}）");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "导出失败。";
            AppendLog("导出失败：" + ex.Message);
        }
    }

    /// <summary>CSV 字段转义（含逗号/引号/换行时用双引号包裹）。</summary>
    private static string Csv(string? value)
    {
        string v = value ?? string.Empty;
        if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    /// <summary>把路径转换为可用作文件名的片段（去掉非法字符）。</summary>
    private static string SanitizeFileName(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in path)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim('_', ' ', '.');
        return s.Length == 0 ? "root" : s;
    }

    // ---------- 通用 ----------

    /// <summary>
    /// 切换忙碌态：禁用开始按钮与下拉、显示 marquee 进度条，并启用「取消」。
    /// 本窗体只做只读扫描，一律 <see cref="BusyRisk.Safe"/>：随时可取消、可直接关窗。
    /// </summary>
    private void SetBusy(bool busy, string? status, bool marquee = false,
        BusyRisk risk = BusyRisk.Safe)
    {
        _busy = busy;
        _btnStart.Enabled = !busy;
        _rootCombo.Enabled = !busy;
        _depthCombo.Enabled = !busy;
        _btnCopyPath.Enabled = !busy;
        _btnExport.Enabled = !busy;
        _btnCancel.Enabled = busy && risk != BusyRisk.Critical;
        _progressBar.Style = busy && marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
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
        UpdateNavigationState();
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
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
