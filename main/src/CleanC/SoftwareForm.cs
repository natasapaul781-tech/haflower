using CleanC.Core;
using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 软件卸载窗口：检测本机已安装软件并卸载。
/// 分为「常规软件卸载」与「深度软件卸载」两个标签页：深度页仅含商店(UWP)应用与
/// 无官方卸载程序的软件（高风险，需先确认风险提示才展示列表，默认不勾选）。
/// 常规页按 官方卸载程序 → 复验 → 残留候选清理 的流程执行；深度页同样受名称确认门槛保护。
/// </summary>
public sealed class SoftwareForm : Form
{
    private static readonly string[] NormalCategoryOrder = { "系统安装（需管理员）", "用户安装" };
    private static readonly string[] DeepCategoryOrder = { "应用商店（UWP）", "无官方卸载程序（仅强制删除）" };

    private const int AutoColIndex = 7; // “风险说明”列自动填满

    private List<InstalledApp> _allApps = new();
    private List<InstalledApp> _normalApps = new();
    private List<InstalledApp> _deepApps = new();
    private bool _deepView;
    private bool _deepUnlocked;
    private readonly Dictionary<string, ListViewItem> _lookup = new(StringComparer.OrdinalIgnoreCase);

    private ListView _listView = null!;
    private Panel _banner = null!;
    private TextBox _txtFilter = null!;
    private ChipLabel _lblSummary = null!;
    private Label _lblStatus = null!;
    private ProgressBar _progressBar = null!;
    private RichTextBox _log = null!;
    private RoundedButton _btnBack = null!;
    private RoundedButton _btnScan = null!;
    private RoundedButton _btnTabNormal = null!;
    private RoundedButton _btnTabDeep = null!;
    private RoundedButton _btnSelectAll = null!;
    private RoundedButton _btnSelectNone = null!;
    private RoundedButton _btnMeasure = null!;
    private RoundedButton _btnUninstall = null!;
    private RoundedButton _btnCancel = null!;
    private CheckBox _chkSortBySize = null!;
    private Panel _deepStrip = null!;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private UiBusyHost _busyHost = null!;
    private string _busyLabel = string.Empty;

    public SoftwareForm()
    {
        Text = "软件卸载";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(1200, 720), new Size(1080, 620));

        InitializeUi();
        AppendLog("提示：扫描完成后勾选要卸载的软件；危险项已列入「深度软件卸载」页，卸载前会再次确认。");
        Shown += async (_, _) => await OnScanAsync();
    }

    private void InitializeUi()
    {
        // ================= 顶部横幅（双缓冲容器，防残影） =================
        _banner = UiStyle.CardPanel(DockStyle.Top);
        _banner.Height = 128;
        var banner = _banner;

        var lblTitle = new Label
        {
            AutoSize = true,
            Location = new Point(18, 12),
            Text = "软件卸载",
            Font = UiStyle.Font(15f, bold: true),
            ForeColor = UiStyle.Text
        };
        var lblSubtitle = new Label
        {
            AutoSize = true,
            Location = new Point(18, 46),
            Text = "检测本机已安装软件：先运行官方卸载程序，卸载后自动清理残留；高风险项已列入「深度软件卸载」。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };

        _lblSummary = new ChipLabel(string.Empty, UiStyle.HoverBg, UiStyle.Primary)
        {
            Location = new Point(0, 10)
        };
        _btnBack = UiStyle.MakeButton("返回", ButtonVariant.Quiet, 84, () => Close());
        _btnBack.Location = new Point(0, 8);

        _btnTabNormal = UiStyle.MakeButton("常规软件卸载", ButtonVariant.Primary, 150,
            () => ShowTab(deep: false));
        _btnTabDeep = UiStyle.MakeButton("深度软件卸载", ButtonVariant.Secondary, 150,
            () => ShowTab(deep: true));
        _btnTabNormal.Location = new Point(18, 84);
        _btnTabDeep.Location = new Point(176, 84);

        _txtFilter = new TextBox
        {
            Location = new Point(340, 84),
            Size = new Size(214, 30),
            PlaceholderText = "筛选：名称 / 发布者..."
        };
        _txtFilter.TextChanged += (_, _) => ReloadItems();

        _btnScan = UiStyle.MakeButton("重新扫描", ButtonVariant.Secondary, 96, async () => await OnScanAsync());
        _btnScan.Location = new Point(566, 84);

        banner.Controls.AddRange(new Control[]
        {
            lblTitle, lblSubtitle, _lblSummary, _btnBack,
            _btnTabNormal, _btnTabDeep, _txtFilter, _btnScan
        });
        banner.Resize += (_, _) => LayoutBanner();
        Shown += (_, _) => LayoutBanner();

        // ================= 深度警示条 =================
        _deepStrip = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = UiStyle.WarningBg,
            Visible = false
        };
        var lblDeepWarn = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "深度软件卸载：仅包含商店(UWP)应用与无官方卸载程序的软件；卸载不可逆，可能导致系统功能或依赖缺失，请逐项核对「风险说明」列。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.WarningText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18, 0, 18, 0)
        };
        _deepStrip.Controls.Add(lblDeepWarn);

        // ================= 中间列表 =================
        _listView = new ListView();
        UiStyle.StyleListView(_listView);
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.CheckBoxes = true;
        _listView.Columns.Add("名称", 200);
        _listView.Columns.Add("版本", 92, HorizontalAlignment.Left);
        _listView.Columns.Add("来源", 70);
        _listView.Columns.Add("安装日期", 78);
        _listView.Columns.Add("占用", 88, HorizontalAlignment.Right);
        _listView.Columns.Add("安装位置", 170);
        _listView.Columns.Add("数据位置", 130);
        _listView.Columns.Add("风险说明", 230);
        _listView.DoubleClick += (_, _) =>
        {
            if (_listView.SelectedItems.Count > 0) ShowDetail(_listView.SelectedItems[0]);
        };
        _listView.Resize += (_, _) => AutoFitColumns();

        // ================= 底部面板 =================
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 208, Padding = new Padding(10, 8, 10, 8), BackColor = UiStyle.Card };

        _log = new RichTextBox();
        UiStyle.StyleLog(_log);
        _log.Dock = DockStyle.Fill;

        var progressRow = new Panel { Dock = DockStyle.Top, Height = 50 };
        _progressBar = new ProgressBar { Location = new Point(2, 12) };
        UiStyle.StyleProgressBar(_progressBar);
        _lblStatus = new Label
        {
            AutoSize = true,
            Location = new Point(2, 34),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _btnCancel = UiStyle.MakeSmallButton("取消", ButtonVariant.Quiet, 72, () => _cts?.Cancel());
        _btnCancel.Enabled = false;
        progressRow.Controls.AddRange(new Control[] { _progressBar, _lblStatus, _btnCancel });
        // 全部手动排布（不依赖 Anchor：面板未布局时加入的控件锚点会记录旧边距，导致进度条超宽/按钮错位）
        progressRow.Resize += (_, _) => LayoutProgressRow(progressRow);
        progressRow.SizeChanged += (_, _) => LayoutProgressRow(progressRow);
        LayoutProgressRow(progressRow);

        var buttonRow = new Panel { Dock = DockStyle.Top, Height = 44 };
        _btnSelectAll = UiStyle.MakeButton("全选", ButtonVariant.Secondary, 78, () => SetAllChecked(true));
        _btnSelectNone = UiStyle.MakeButton("全不选", ButtonVariant.Secondary, 78, () => SetAllChecked(false));
        _btnMeasure = UiStyle.MakeButton("重新测算", ButtonVariant.Secondary, 96, async () => await MeasureAsync(force: true));
        _btnUninstall = UiStyle.MakeButton("卸载选中项", ButtonVariant.Danger, 112, async () => await OnUninstallAsync());
        _btnSelectAll.Location = new Point(0, 6);
        _btnSelectNone.Location = new Point(86, 6);
        _btnMeasure.Location = new Point(172, 6);
        _btnUninstall.Location = new Point(276, 6);

        _chkSortBySize = new CheckBox
        {
            Text = "按占用排序",
            AutoSize = true,
            Location = new Point(400, 12),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            Checked = false
        };
        _chkSortBySize.CheckedChanged += (_, _) =>
        {
            ReloadItems();
            AppendLog(_chkSortBySize.Checked ? "已按占用大小排序（组内降序）。" : "已恢复默认排序（分组内按名称）。");
        };

        buttonRow.Controls.AddRange(new Control[] { _btnSelectAll, _btnSelectNone, _btnMeasure, _btnUninstall, _chkSortBySize });

        bottom.Controls.Add(_log);
        bottom.Controls.Add(progressRow);
        bottom.Controls.Add(buttonRow);

        // ================= 组装（Dock：先 Fill 后 Top/Bottom） =================
        Controls.Add(_listView);
        Controls.Add(bottom);
        Controls.Add(_deepStrip);
        Controls.Add(banner);

        FormClosing += (_, _) => _cts?.Cancel();

        // 处理中遮罩 + 关窗闸门（最后创建，盖在所有控件之上）
        _busyHost = new UiBusyHost(this);
        UpdateTabButtons();
    }

    /// <summary>进度行手动布局：进度条拉伸到「取消」按钮左侧，按钮靠右（避免 Anchor 旧边距错位）。</summary>
    private void LayoutProgressRow(Panel row)
    {
        int w = row.ClientSize.Width;
        _btnCancel.Left = w - _btnCancel.Width - 12;
        _btnCancel.Top = 8;
        _progressBar.Left = 2;
        _progressBar.Top = 12;
        _progressBar.Width = Math.Max(200, _btnCancel.Left - _progressBar.Left - 12);
    }

    /// <summary>同步排布右上角：返回按钮 + 汇总胶囊（不依赖 Anchor，避免残影/重叠）。</summary>
    private void LayoutBanner()
    {
        if (_btnBack == null || _lblSummary == null) return;
        _btnBack.Left = _banner.ClientSize.Width - _btnBack.Width - 18;
        _lblSummary.Left = _btnBack.Left - _lblSummary.Width - 10;
        _banner.Invalidate(true);
    }

    /// <summary>更新汇总胶囊并立即重新排布（宽度变化会移动左侧定位）。</summary>
    private void UpdateSummary(string text)
    {
        _lblSummary.SetText(text);
        LayoutBanner();
    }

    // ---------- 标签页 ----------

    private void ShowTab(bool deep)
    {
        if (deep && !_deepUnlocked)
        {
            var r = MessageBox.Show(this,
                "深度软件卸载包含高风险操作：\n\n" +
                "· 商店(UWP)应用：无法运行官方卸载程序，卸载后需从 Microsoft Store 重新安装；" +
                "部分预装应用删除后可能影响系统功能。\n" +
                "· 无官方卸载程序的软件：只能强制删除安装目录与注册表候选，可能残留引用或影响依赖它的其他软件。\n\n" +
                "卸载不可逆，请逐项核对列表中的「风险说明」列。\n\n确认进入深度软件卸载？",
                "深度软件卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            _deepUnlocked = true;
        }

        _deepView = deep;
        _deepStrip.Visible = deep;
        UpdateTabButtons();
        ReloadItems();
        AppendLog(deep ? "已切换到「深度软件卸载」：列表仅显示高风险条目，默认不勾选。" : "已切换到「常规软件卸载」。");
    }

    private void UpdateTabButtons()
    {
        SetTabStyle(_btnTabNormal, !_deepView);
        SetTabStyle(_btnTabDeep, _deepView);
    }

    private static void SetTabStyle(RoundedButton b, bool active)
    {
        if (active) b.SetVariant(ButtonVariant.Primary);
        else b.SetVariant(ButtonVariant.Secondary);
        b.Invalidate();
    }

    // ---------- 扫描与列表 ----------

    private async Task OnScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "扫描中...", marquee: true);
        _cts = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(() => SoftwareScanner.Scan(_cts.Token), _cts.Token);
            _allApps = result.Items.ToList();
            _normalApps = _allApps.Where(a => !a.IsDeep).ToList();
            _deepApps = _allApps.Where(a => a.IsDeep).ToList();
            ReloadItems();
            UpdateSummaryText();
            _lblStatus.Text = $"扫描完成：{_allApps.Count} 项（常规 {_normalApps.Count} / 深度 {_deepApps.Count}）。";
            AppendLog($"扫描完成：{_allApps.Count} 项（常规 {_normalApps.Count} / 深度 {_deepApps.Count}）。");
            foreach (var e in result.Errors)
                AppendLog("  ~ " + e);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "扫描已取消。";
            AppendLog("扫描已取消，保留上次列表。");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "扫描失败。";
            AppendLog("扫描失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }

        // 扫描后自动实测：注册表没有大小/位置或商店应用（注册表口径拿不到占用的条目）
        await MeasureAsync(force: false);
    }

    /// <summary>
    /// 实测占用：force=true 对当前页全部条目强制重测；force=false 只测「注册表无大小/无有效位置/UWP」的条目。
    /// 只读操作（遍历目录统计字节数），不做任何删除。
    /// </summary>
    private async Task MeasureAsync(bool force)
    {
        if (_busy) return;

        if (_allApps.Count == 0) return;

        // 待测范围：force=当前勾选项（未勾选则当前页）；否则=注册表拿不到大小/位置的条目（含商店应用）
        List<InstalledApp> scope;
        if (force)
        {
            var checkedItems = SelectedItems().ToArray();
            scope = checkedItems.Length > 0 ? checkedItems.ToList() : (_deepView ? _deepApps : _normalApps).ToList();
        }
        else
        {
            scope = _allApps.Where(a => !a.HasReliableSize).ToList();
        }

        var scopeIds = new HashSet<string>(scope.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

        if (scope.Count == 0)
        {
            if (force)
            {
                _lblStatus.Text = "当前页没有可测算的条目。";
                AppendLog("当前页没有可测算的条目（请先切换到有软件的分类页）。");
                return;
            }

            // 自动模式：注册表数据已足够，只补做一次位置解析（毫秒级），不遍历目录
            await Task.Run(() => SoftwareScanner.ResolveFootprints(_allApps, measure: false, null, CancellationToken.None));
            ReloadItems();
            UpdateSummaryText();
            _lblStatus.Text = "全部条目已有注册表大小与位置；如需精确值可点「重新测算」。";
            AppendLog("无需自动实测：全部条目已有注册表 EstimatedSize 与有效安装位置（已刷新位置解析）。");
            return;
        }

        AppendLog(force
            ? $"开始实测 {scope.Count} 项占用（只读遍历目录，可能较慢；可点「取消」中断）..."
            : $"自动实测 {scope.Count} 项占用（注册表无大小/无有效位置/UWP 条目）；其余条目保留注册表估算值。");

        SetBusy(true, $"测算占用 0/{scope.Count}...", marquee: false);
        _cts = new CancellationTokenSource();
        try
        {
            var token = _cts.Token;
            // 位置解析覆盖全部条目（很快，几毫秒级），仅对 scope 内的条目做实测
            await Task.Run(() => SoftwareScanner.ResolveFootprints(_allApps, measure: true,
                (i, total, app) =>
                {
                    Ui(() =>
                    {
                        _lblStatus.Text = $"测算占用 {i}/{total}：{Truncate(app.DisplayName, 24)}";
                        ReportProgress(i, total);
                    });
                }, token, a => scopeIds.Contains(a.Id)), token);

            AppendLog("实测完成，刷新列表...");
            ReloadItems();
            UpdateSummaryText();
            var (measured, _, measuredCount, noData) = AppFootprintResolver.Totals(_allApps);
            _lblStatus.Text = $"实测完成：{measuredCount} 项已测量，合计约 {SizeFormatter.Format(measured)}" +
                              (noData > 0 ? $"（{noData} 项无法读取目录）" : string.Empty) + "。";
            AppendLog($"实测完成：{measuredCount} 项已测量，合计约 {SizeFormatter.Format(measured)}" +
                      (noData > 0 ? $"；{noData} 项目录无法读取（可能需管理员权限）" : string.Empty) + "。");
            AppendLogList(scope);
            var suspect = _allApps.Where(a => a.Footprint?.Install?.Suspect != null).ToArray();
            if (suspect.Length > 0)
                AppendLog($"  另有 {suspect.Length} 项的位置过宽（注册表把父目录当安装位置），已跳过测量：" +
                          string.Join("、", suspect.Take(5).Select(a => a.DisplayName)));
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "测算已取消（已测部分保留）。";
            AppendLog("测算已取消，已完成的测量结果保留在列表中。");
            ReloadItems();
            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "测算失败。";
            AppendLog("测算失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>把实测结果按大小列出前 15 项（方便直接看出占用最大的软件）。</summary>
    private void AppendLogList(IEnumerable<InstalledApp> apps)
    {
        var top = apps.Where(a => a.IsMeasured)
            .OrderByDescending(a => a.MeasuredBytes)
            .Take(15)
            .ToArray();
        if (top.Length == 0) return;

        AppendLog("  占用最大的条目：");
        foreach (var a in top)
        {
            string path = a.Footprint?.Install?.Path ?? (a.Footprint?.DataLocationText ?? string.Empty);
            AppendLog($"    {SizeFormatter.Format(a.MeasuredBytes),10}  {a.DisplayName}  →  {path}");
        }
        AppendLog("  提示：勾选下方「按占用排序」可在列表内按占用从大到小排列。");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void ReloadItems()
    {
        _listView.Items.Clear();
        _listView.Groups.Clear();
        _lookup.Clear();

        string filter = _txtFilter.Text.Trim();
        var source = _deepView ? _deepApps : _normalApps;
        var categories = _deepView ? DeepCategoryOrder : NormalCategoryOrder;

        // 按占用排序（组内降序）：实测值优先，其次注册表估算值
        var ordered = _chkSortBySize.Checked
            ? source.OrderByDescending(a => a.IsMeasured ? a.MeasuredBytes : a.EstimatedBytes)
                    .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            : source;

        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var cat in categories)
        {
            var g = new ListViewGroup(cat);
            _listView.Groups.Add(g);
            groups[cat] = g;
        }

        foreach (var item in ordered)
        {
            if (filter.Length > 0 &&
                !item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(item.Publisher) || !item.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;

            string category = _deepView ? item.DeepCategory : item.Category;
            if (!groups.TryGetValue(category, out var grp))
            {
                grp = new ListViewGroup(category);
                _listView.Groups.Add(grp);
                groups[category] = grp;
            }

            var lvi = new ListViewItem(item.DisplayName) { Tag = item, Checked = false };
            lvi.SubItems.Add(item.VersionText);
            lvi.SubItems.Add(item.SourceText);
            lvi.SubItems.Add(item.DateText);
            lvi.SubItems.Add(item.SizeText);
            lvi.SubItems.Add(item.LocationText);
            lvi.SubItems.Add(string.IsNullOrEmpty(item.DataLocationText) ? "—" : item.DataLocationText);
            lvi.SubItems.Add(item.RiskText);
            lvi.ToolTipText = BuildTooltip(item);
            lvi.Group = grp;
            _listView.Items.Add(lvi);
            _lookup[item.Id] = lvi;
        }

        AutoFitColumns();
    }

    /// <summary>行工具提示：位置来源与占用明细（鼠标悬停即可看到“在哪、多大”）。</summary>
    private static string BuildTooltip(InstalledApp app)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("占用：").Append(app.SizeText).Append("（").Append(app.SizeSourceText).Append('）');
        if (app.Footprint?.Install != null)
            sb.Append('\n').Append("安装位置：").Append(app.Footprint.Install.Path)
              .Append("（来源：").Append(app.Footprint.Install.SourceText).Append('）');
        if (app.Footprint?.Install?.Suspect != null)
            sb.Append('\n').Append("位置提示：").Append(app.Footprint.Install.Suspect)
              .Append("（这类“父目录”会被多个软件共用，为避免把整个父目录算到它头上，已跳过测量）");
        foreach (var d in app.Footprint?.DataPaths ?? new List<AppPathInfo>())
            sb.Append('\n').Append("数据位置：").Append(d.Path).Append("（").Append(d.SizeText).Append('）');
        return sb.ToString();
    }

    /// <summary>汇总胶囊：条目数 + 实测进度与合计（实测合计只计唯一目录，避免重复累计）。</summary>
    private void UpdateSummaryText()
    {
        var (measured, estimated, measuredCount, _) = AppFootprintResolver.Totals(_allApps);
        int pending = _allApps.Count(a => !a.HasReliableSize && !a.IsMeasured);
        string text = $"发现 {_allApps.Count} 项 · 常规 {_normalApps.Count} · 深度 {_deepApps.Count}" +
                      $" · 已实测 {measuredCount}（{SizeFormatter.Format(measured)}）";
        if (pending > 0) text += $" · 待测 {pending}";
        else if (estimated > 0) text += $" · 估算 {SizeFormatter.Format(estimated)}";
        UpdateSummary(text);
    }

    /// <summary>列宽自适应：末列（风险说明）填满剩余宽度；最小宽度下也不出现裁剪。</summary>
    private void AutoFitColumns()
    {
        if (_listView.Columns.Count == 0) return;
        int reserved = 26; // 垂直滚动条 + 边距
        int fixedSum = 0;
        for (int i = 0; i < _listView.Columns.Count; i++)
            if (i != AutoColIndex)
                fixedSum += _listView.Columns[i].Width;
        int auto = Math.Max(200, _listView.ClientSize.Width - fixedSum - reserved);
        _listView.Columns[AutoColIndex].Width = auto;
    }

    private void ShowDetail(ListViewItem lvi)
    {
        if (lvi.Tag is not InstalledApp it) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"名称：{it.DisplayName}");
        sb.AppendLine($"版本：{it.VersionText}");
        sb.AppendLine($"发布者：{it.PublisherText}");
        sb.AppendLine($"来源：{it.SourceText}（{(it.RequiresElevation ? "需要管理员权限" : "无需管理员")}）");
        if (it.IsDeep) sb.AppendLine("分级：【深度软件卸载·高风险】");
        sb.AppendLine($"风险：{it.RiskText}");
        sb.AppendLine($"安装日期：{it.DateText}");
        sb.AppendLine($"占用大小：{it.SizeText}");
        sb.AppendLine($"占用来源：{it.SizeSourceText}");
        sb.AppendLine($"安装位置：{it.LocationText}");
        if (it.Footprint?.Install != null)
            sb.AppendLine($"位置来源：{it.Footprint.Install.SourceText}" +
                          (it.Footprint.Install.Files > 0 ? $"（{it.Footprint.Install.Files:N0} 个文件）" : string.Empty));
        if (it.Footprint is { DataPaths.Count: > 0 })
        {
            sb.AppendLine("数据位置：");
            foreach (var d in it.Footprint.DataPaths)
                sb.AppendLine($"  · {d.Path}（{d.SizeText}，{d.SourceText}）");
        }
        if (it.Footprint is { OtherCandidates.Count: > 0 })
        {
            sb.AppendLine("其他候选位置（未计入占用）：");
            foreach (var c in it.Footprint.OtherCandidates.Take(5))
                sb.AppendLine("  · " + c);
        }
        if (it.Footprint is { Inaccessible: true })
            sb.AppendLine("提示：部分目录无法读取（如商店包体），建议以管理员身份运行后重新测算。");
        sb.AppendLine($"卸载方式：{it.MethodText}");
        if (!string.IsNullOrWhiteSpace(it.UninstallString))
            sb.AppendLine($"卸载命令：{it.UninstallString}");
        if (!string.IsNullOrWhiteSpace(it.QuietUninstallString))
            sb.AppendLine($"静默卸载：{it.QuietUninstallString}");
        if (it.IsMsi && !string.IsNullOrEmpty(it.ProductCode))
            sb.AppendLine($"MSI ProductCode：{it.ProductCode}");
        if (!string.IsNullOrEmpty(it.KeyDisplayPath))
            sb.AppendLine($"注册表项：{it.KeyDisplayPath}");
        if (it.IsUwp)
            sb.AppendLine($"包全名：{it.PackageFullName}");
        MessageBox.Show(this, sb.ToString(), $"{it.DisplayName} 详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetAllChecked(bool check)
    {
        foreach (ListViewItem lvi in _listView.Items)
            lvi.Checked = check;
    }

    private IEnumerable<InstalledApp> SelectedItems()
    {
        foreach (ListViewItem lvi in _listView.CheckedItems)
            if (lvi.Tag is InstalledApp app)
                yield return app;
    }

    // ---------- 卸载流程 ----------

    private async Task OnUninstallAsync()
    {
        if (_busy) return;
        var selected = SelectedItems().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, _deepView
                ? "请至少勾选一个深度卸载条目（风险较高，请核对「风险说明」列）。"
                : "请至少勾选一个软件条目。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 管理员权限处理（HKLM 系统级条目）
        if (selected.Any(i => i.RequiresElevation) && !ElevationHelper.IsElevated())
        {
            var r = MessageBox.Show(this,
                "所选条目中包含需要管理员权限的系统级软件（HKLM 条目）。\n\n" +
                "是否以管理员身份重启本程序？\n\n" +
                "点「否」则跳过这些条目，仅处理无需管理员的软件。",
                "需要管理员权限", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
            {
                if (ElevationHelper.TryRestartElevated(null, out string error))
                {
                    Application.Exit();
                    return;
                }
                MessageBox.Show(this, error + "\n\n将继续处理不需要管理员权限的条目。",
                    "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("提权未完成：" + error);
            }

            selected = selected.Where(i => !i.RequiresElevation).ToArray();
            if (selected.Length == 0)
            {
                AppendLog("已跳过所有需要管理员权限的条目，未执行卸载。");
                return;
            }
            AppendLog("已跳过需要管理员权限的系统级条目。");
        }

        // 无官方卸载程序的条目（只能强制删除，走残留清理路线）
        var unsupported = selected.Where(IsUnsupported).ToArray();
        var runnable = selected.Where(i => !IsUnsupported(i)).ToArray();

        // 卸载方式选择（仅常规页、且存在必要的软件时询问；深度页默认静默处理）
        bool silent = true;
        if (!_deepView && runnable.Any(i => !i.CanSilentUninstall))
        {
            var mode = MessageBox.Show(this,
                "部分软件没有静默卸载参数，将运行其官方卸载程序（可能弹出卸载向导）。\n\n" +
                "是否优先尝试静默卸载？\n「是」＝静默（推荐）；「否」＝全部运行官方卸载程序。",
                "卸载方式", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (mode == DialogResult.No) silent = false;
        }

        // 确认框（按页提供差异化文案）
        string names = string.Join("、", selected.Take(8).Select(i => i.DisplayName));
        if (selected.Length > 8) names += $" 等 {selected.Length} 项";

        string confirmText;
        string confirmTitle;
        if (_deepView)
        {
            confirmTitle = "确认深度卸载（高风险）";
            confirmText =
                $"将深度卸载 {selected.Length} 项软件：\n{names}\n\n" +
                "· 商店(UWP)应用：无法运行官方卸载程序，删除后需从 Microsoft Store 重装；部分预装项可能影响系统功能。\n" +
                "· 无官方卸载程序：仅能强制删除安装目录与注册表候选，可能残留引用或影响依赖它的软件。\n\n" +
                (unsupported.Length > 0
                    ? $"其中 {unsupported.Length} 项无官方卸载程序，将直接进入「残留清理」逐项确认（默认不勾选，需再次确认）。\n"
                    : string.Empty) +
                "此操作不可逆，建议先创建系统还原点或备份。\n确认继续？";
        }
        else
        {
            confirmTitle = "确认卸载";
            confirmText =
                $"将卸载 {selected.Length} 项软件：\n{names}\n\n" +
                (silent ? "方式：静默卸载（MSI /qn、静默参数；不支持的自动转官方程序）\n" : "方式：运行官方卸载程序\n") +
                (unsupported.Length > 0
                    ? $"其中 {unsupported.Length} 项没有官方卸载程序，将进入「残留清理」逐项确认（默认不勾选，需再次确认）。\n"
                    : string.Empty) +
                "卸载后软件本体、数据与注册表引用将被移除，不可通过本工具恢复；建议先创建系统还原点或备份。\n确认继续？";
        }

        var confirm = MessageBox.Show(this, confirmText, confirmTitle,
            MessageBoxButtons.YesNo, _deepView ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true, "卸载中...", marquee: false, risk: BusyRisk.Critical);
        _cts = new CancellationTokenSource();
        var failed = new List<InstalledApp>();
        var succeeded = new List<InstalledApp>();
        try
        {
            var queue = runnable.ToList();
            for (int i = 0; i < queue.Count; i++)
            {
                var app = queue[i];
                ReportProgress(i, queue.Count);
                AppendLog($"卸载：{app.DisplayName} ...");
                _lblStatus.Text = $"卸载中 {i + 1}/{queue.Count}：{app.DisplayName}";
                // 遮罩上的动画文案跟着走，用户才知道卡在哪个软件上
                _busyHost.Update($"卸载中　{i + 1}/{queue.Count}：{app.DisplayName}");

                var outcome = await UninstallRunner.UninstallAsync(app,
                    (!_deepView && !silent) ? UninstallMode.Interactive : UninstallMode.Silent,
                    m => Ui(() => AppendLog(m)), _cts.Token);

                switch (outcome.Status)
                {
                    case UninstallStatus.Success:
                        AppendLog($"  [完成] {app.DisplayName}：{outcome.Detail}");
                        succeeded.Add(app);
                        break;
                    case UninstallStatus.RebootRequired:
                        AppendLog($"  [需重启] {app.DisplayName}：{outcome.Detail} 重启后可重新扫描确认。");
                        break;
                    default:
                        AppendLog($"  [未完成] {app.DisplayName}：{outcome.Detail}");
                        failed.Add(app);
                        break;
                }
                OperationLog.LogUninstall(app.DisplayName, StatusText(outcome.Status), outcome.Detail);
            }
            ReportProgress(queue.Count, queue.Count);

            // 重新扫描刷新列表
            AppendLog("重新扫描已安装软件列表...");
            foreach (var app in succeeded.Concat(failed).Concat(unsupported))
                foreach (var p in app.Footprint?.AllPaths() ?? Enumerable.Empty<AppPathInfo>())
                    SizeCache.InvalidateSubtree(p.Path);
            var result = await Task.Run(() => SoftwareScanner.Scan(_cts.Token), _cts.Token);
            _allApps = result.Items.ToList();
            _normalApps = _allApps.Where(a => !a.IsDeep).ToList();
            _deepApps = _allApps.Where(a => a.IsDeep).ToList();
            ReloadItems();
            UpdateSummaryText();

            // 卸载成功后也检测残留（确保注册表/文件一并清理，发现候选由用户确认）
            foreach (var app in succeeded)
            {
                _cts.Token.ThrowIfCancellationRequested();
                AppendLog($"卸载成功；继续检查“{app.DisplayName}”是否还有残留数据...");
                await ProcessLeftoversAsync(app);
            }
            // 未完成 / 无官方程序的：进入残留清理（强制删除路径）
            foreach (var app in failed.Concat(unsupported))
            {
                _cts.Token.ThrowIfCancellationRequested();
                await ProcessLeftoversAsync(app);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("卸载已取消（已启动的卸载程序不被强制终止；可重新扫描确认状态）。");
        }
        catch (Exception ex)
        {
            AppendLog("卸载过程中出现错误：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }

        // 卸载/残留清理后：新列表中缺数据的条目再补一次实测（读取操作，不影响已完成的卸载）
        await MeasureAsync(force: false);
    }

    private static bool IsUnsupported(InstalledApp app) =>
        !app.IsUwp && !app.IsMsi && string.IsNullOrWhiteSpace(app.UninstallString);

    private static string StatusText(UninstallStatus s) => s switch
    {
        UninstallStatus.Success => "成功",
        UninstallStatus.RebootRequired => "需重启生效",
        UninstallStatus.StillRegistered => "未完成",
        UninstallStatus.Failed => "失败",
        UninstallStatus.NotSupported => "无官方卸载程序",
        _ => s.ToString(),
    };

    /// <summary>对单个未完成卸载的应用：扫描残留候选 → 用户勾选 → 二次确认 → 删除。</summary>
    private async Task ProcessLeftoversAsync(InstalledApp app)
    {
        AppendLog($"检查“{app.DisplayName}”的残留情况...");
        List<LeftoverCandidate> leftovers;
        try
        {
            leftovers = await Task.Run(() => LeftoverScanner.Scan(app), _cts!.Token);
        }
        catch (Exception ex)
        {
            AppendLog($"  残留扫描失败：{ex.Message}");
            return;
        }

        if (leftovers.Count == 0)
        {
            // 区分“卸载未完成（条目仍在）”与“卸载成功但仍有残留数据”
            bool stillRegistered = SoftwareScanner.IsEntryStillRegistered(app);
            AppendLog(stillRegistered
                ? "  未发现可确认的残留候选；请在“设置 → 应用”或官方途径跟进处理。"
                : "  未发现残留候选。");
            return;
        }

        AppendLog($"  发现 {leftovers.Count} 项残留候选，请逐项确认是否清理（默认不勾选）。");
        using var dlg = new LeftoverForm(app, leftovers);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            AppendLog("  已跳过残留清理。");
            return;
        }

        var picked = dlg.Selected.ToArray();
        if (picked.Length == 0)
        {
            AppendLog("  未勾选任何候选，跳过。");
            return;
        }

        // 管理员门槛（HKLM 注册表项 / Program Files / ProgramData 目录）
        if (picked.Any(c => c.RequiresElevation) && !ElevationHelper.IsElevated())
        {
            var r = MessageBox.Show(this,
                "所选残留中含需要管理员权限的项（HKLM 注册表项 / Program Files / ProgramData）。\n\n" +
                "是否以管理员身份重启本程序后再清理？\n点「否」则跳过这些系统级残留。",
                "需要管理员权限", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
            {
                if (ElevationHelper.TryRestartElevated(null, out string error))
                {
                    Application.Exit();
                    return;
                }
                MessageBox.Show(this, error + "\n\n将继续处理不需要管理员权限的残留项。",
                    "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("  提权未完成：" + error);
            }
            picked = picked.Where(c => !c.RequiresElevation).ToArray();
            if (picked.Length == 0)
            {
                AppendLog("  已跳过所有需要管理员权限的残留项。");
                return;
            }
        }

        var confirm = MessageBox.Show(this,
            $"将对“{app.DisplayName}”执行残留清理，共 {picked.Length} 项" +
            $"（预计释放约 {SizeFormatter.Format(picked.Sum(c => c.SizeBytes ?? 0))}）：\n\n" +
            string.Join("\n", picked.Take(10).Select(c => $"  [{c.KindText}] {c.SizeText}  {c.Target}")) +
            (picked.Length > 10 ? $"\n  ... 其余 {picked.Length - 10} 项" : string.Empty) +
            "\n\n此操作不可撤销（不会扫描其他软件引用），确认继续？",
            "确认清理残留", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            AppendLog("  已取消残留清理。");
            return;
        }

        try
        {
            _busyHost.Update($"清理残留　{app.DisplayName}");
            var r = await Task.Run(
                () => LeftoverScanner.Delete(picked, m => Ui(() => AppendLog(m)), _cts!.Token), _cts.Token);
            AppendLog($"  [残留清理] 完成：释放 {SizeFormatter.Format(r.FreedBytes)}，{picked.Length - r.Errors.Count} 项成功，{r.Errors.Count} 项提示：");
            foreach (var e in r.Errors)
                AppendLog("    ~ " + e);
            OperationLog.LogLeftoverDelete(app.DisplayName, picked.Length, r.FreedBytes, r.Errors.Count);
        }
        catch (OperationCanceledException)
        {
            AppendLog("  残留清理已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("  残留清理失败：" + ex.Message);
        }
    }

    // ---------- 通用 ----------

    private void ReportProgress(int current, int total)
    {
        Ui(() =>
        {
            _progressBar.Maximum = Math.Max(total, 1);
            _progressBar.Value = Math.Min(current, _progressBar.Maximum);
            // 遮罩上的动画文案跟着走（卸载时不显示"取消"，但进度要看得见）
            if (_busyLabel.Length > 0)
                _busyHost.Update($"{_busyLabel}　{current}/{total}");
        });
    }

    /// <summary>
    /// 统一设置"忙"状态并驱动处理中遮罩。
    ///
    /// 扫描/测算只读 → <see cref="BusyRisk.Safe"/>；
    /// 「卸载选中项」传 <see cref="BusyRisk.Critical"/>——卸载程序跑到一半被强行结束，
    /// 会留下注册表与磁盘不一致的状态，所以期间不提供取消、也拒绝关闭窗口。
    /// </summary>
    private void SetBusy(bool busy, string? status, bool marquee = false,
        BusyRisk risk = BusyRisk.Safe)
    {
        _busy = busy;
        _btnScan.Enabled = !busy;
        _btnTabNormal.Enabled = !busy;
        _btnTabDeep.Enabled = !busy;
        _txtFilter.Enabled = !busy;
        _btnSelectAll.Enabled = !busy && !_deepView;
        _btnSelectNone.Enabled = !busy;
        _btnMeasure.Enabled = !busy;
        _chkSortBySize.Enabled = !busy;
        _btnUninstall.Enabled = !busy;
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
