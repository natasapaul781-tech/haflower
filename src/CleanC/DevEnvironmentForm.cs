using CleanC.Core;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 开发者环境检测与清理窗口：扫描虚拟环境/依赖目录/包缓存，
/// 展示库清单与大小，勾选后经确认执行删除（删除受 DevScanner 安全复验保护）。
/// </summary>
public sealed class DevEnvironmentForm : Form
{
    private static readonly string[] CategoryOrder =
    {
        "Python 虚拟环境", "Node.js 相关", "Rust 相关", "Go 相关",
        "Java 相关", "PHP 相关", "语言包缓存", "AI / 模型缓存",
        "项目构建产物", "IDE 相关", "工具链本体（仅统计）", "虚拟磁盘与容器（仅统计）"
    };

    private readonly DevScanner _scanner = new();
    private readonly List<string> _extraRoots = new();
    private readonly Dictionary<string, ListViewItem> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private bool _reportOnlyHintShown;
    private IReadOnlyList<DevItem> _lastItems = Array.Empty<DevItem>();

    private ListView _listView = null!;
    private Panel _banner = null!;
    private ComboBox _scopeCombo = null!;
    private Label _lblSummary = null!;
    private Label _lblStatus = null!;
    private ProgressBar _progressBar = null!;
    private RichTextBox _log = null!;
    private Button _btnBack = null!;
    private Button _btnScan = null!;
    private Button _btnAddRoot = null!;
    private Button _btnSelectAll = null!;
    private Button _btnSelectNone = null!;
    private Button _btnDelete = null!;
    private Button _btnCancel = null!;
    private CheckBox _chkSortBySize = null!;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private UiBusyHost _busyHost = null!;
    private string _busyLabel = string.Empty;

    public DevEnvironmentForm()
    {
        Text = "开发者环境检测与清理";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(1040, 680), new Size(920, 560));

        InitializeUi();
        AppendLog("提示：点击「扫描」检测本机的虚拟环境、依赖目录与包缓存；仅勾选项会被删除，删除前会再次确认。");
    }

    private void InitializeUi()
    {
        // ---- 顶部横幅（双缓冲，防残影） ----
        _banner = UiStyle.CardPanel(DockStyle.Top);
        _banner.Height = 104;
        var banner = _banner;
        var lblTitle = new Label
        {
            AutoSize = true,
            Location = new Point(16, 10),
            Text = "开发者环境检测与清理",
            Font = UiStyle.Font(14f, bold: true),
            ForeColor = UiStyle.Text
        };
        var lblHint = new Label
        {
            AutoSize = true,
            Location = new Point(16, 40),
            Text = "自动扫描虚拟环境、依赖目录与包缓存；勾选后经确认删除。环境/依赖目录将被整体移除，需重新创建或安装。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText
        };
        _scopeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(16, 68),
            Size = new Size(216, 30),
            FlatStyle = FlatStyle.Flat
        };
        _scopeCombo.Items.AddRange(new object[]
        {
            "扫描范围：用户目录 + 常用位置",
            "扫描范围：整个系统盘（较慢）",
            "扫描范围：所有固定磁盘（最全，最慢）"
        });
        _scopeCombo.SelectedIndex = 0;

        _btnScan = UiStyle.MakeButton("扫描", ButtonVariant.Primary, 80, async () => await OnScanAsync());
        _btnScan.Location = new Point(248, 66);
        _btnAddRoot = UiStyle.MakeButton("添加扫描目录", ButtonVariant.Secondary, 120, () => OnAddRoot());
        _btnAddRoot.Location = new Point(340, 66);
        _btnBack = UiStyle.MakeButton("返回", ButtonVariant.Quiet, 84, () => Close());
        _btnBack.Location = new Point(0, 10);

        _lblSummary = new Label
        {
            AutoSize = true,
            Location = new Point(0, 72),
            Font = UiStyle.Font(9f),
            ForeColor = Color.FromArgb(200, 90, 20)
        };

        banner.Controls.AddRange(new Control[] { lblTitle, lblHint, _scopeCombo, _btnScan, _btnAddRoot, _btnBack, _lblSummary });
        banner.Resize += (_, _) => LayoutBanner();
        Shown += (_, _) => LayoutBanner();

        // ---- 底部面板 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 216, Padding = new Padding(8), BackColor = UiStyle.Card };

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
        _btnDelete = UiStyle.MakeButton("删除选中项", ButtonVariant.Danger, 112, async () => await OnDeleteAsync());
        _btnSelectAll.Location = new Point(0, 6);
        _btnSelectNone.Location = new Point(84, 6);
        _btnDelete.Location = new Point(168, 6);

        _chkSortBySize = new CheckBox
        {
            Text = "按占用排序",
            AutoSize = true,
            Location = new Point(292, 12),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            Checked = true
        };
        _chkSortBySize.CheckedChanged += (_, _) =>
        {
            ReloadItems(_lastItems);
            AppendLog(_chkSortBySize.Checked ? "已按占用大小排序（组内降序）。" : "已恢复扫描返回顺序。");
        };

        buttonRow.Controls.AddRange(new Control[] { _btnSelectAll, _btnSelectNone, _btnDelete, _chkSortBySize });

        bottom.Controls.Add(_log);
        bottom.Controls.Add(progressRow);
        bottom.Controls.Add(buttonRow);

        // ---- 列表（中间填充） ----
        _listView = new ListView();
        UiStyle.StyleListView(_listView);
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.CheckBoxes = true;
        _listView.Columns.Add("类型", 122);
        _listView.Columns.Add("名称", 150);
        _listView.Columns.Add("已安装库", 72, HorizontalAlignment.Right);
        _listView.Columns.Add("路径", 300);
        _listView.Columns.Add("预估大小", 88, HorizontalAlignment.Right);
        _listView.Columns.Add("警告", 170);
        _listView.Resize += (_, _) => AutoFitColumns();
        _listView.DoubleClick += (_, _) =>
        {
            if (_listView.SelectedItems.Count > 0) ShowDetail(_listView.SelectedItems[0]);
        };
        // 仅统计项（工具链本体、虚拟磁盘等）不可勾选：本工具不提供删除
        _listView.ItemCheck += (_, e) =>
        {
            if (e.NewValue != CheckState.Checked) return;
            if (e.Index < 0 || e.Index >= _listView.Items.Count) return;
            if (_listView.Items[e.Index].Tag is not DevItem { IsReportOnly: true }) return;

            e.NewValue = CheckState.Unchecked;
            if (_reportOnlyHintShown) return;
            _reportOnlyHintShown = true;
            AppendLog("提示：「仅统计」条目（工具链本体 / 虚拟磁盘 / AI 工具运行时等）只展示占用与位置，本工具不提供删除。");
        };

        // ---- 组装 ----
        Controls.Add(_listView);
        Controls.Add(bottom);
        Controls.Add(banner);

        FormClosing += (_, _) => _cts?.Cancel();

        // 处理中遮罩 + 关窗闸门（最后创建，盖在所有控件之上）
        _busyHost = new UiBusyHost(this);
    }

    /// <summary>进度行手动布局：进度条拉伸到「取消」按钮左侧，按钮靠右（避免 Anchor 旧边距错位）。</summary>
    private void LayoutProgressRow(Panel row)
    {
        int w = row.ClientSize.Width;
        _btnCancel.Left = w - _btnCancel.Width - 12;
        _btnCancel.Top = 8;
        _progressBar.Left = 12;
        _progressBar.Top = 10;
        _progressBar.Width = Math.Max(200, _btnCancel.Left - _progressBar.Left - 12);
    }

    /// <summary>同步排布右上角（返回按钮 + 汇总），不依赖 Anchor 避免残影/重叠。</summary>
    private void LayoutBanner()
    {
        if (_btnBack == null || _lblSummary == null) return;
        _btnBack.Left = _banner.ClientSize.Width - _btnBack.Width - 16;
        _lblSummary.Left = _banner.ClientSize.Width - _lblSummary.Width - 16;
        _lblSummary.Top = 72;
        _banner.Invalidate(true);
    }

    /// <summary>更新汇总文本并重新排布（AutoSize 宽度变化需要重定位）。</summary>
    private void UpdateSummary(string text)
    {
        _lblSummary.Text = text;
        LayoutBanner();
    }

    /// <summary>列宽自适应：“路径”列填满剩余宽度（最小宽度下也不裁剪）。</summary>
    private void AutoFitColumns()
    {
        if (_listView.Columns.Count == 0) return;
        int reserved = 26;
        int fixedSum = _listView.Columns[0].Width + _listView.Columns[1].Width +
                       _listView.Columns[2].Width + _listView.Columns[4].Width + _listView.Columns[5].Width;
        int auto = Math.Max(220, _listView.ClientSize.Width - fixedSum - reserved);
        _listView.Columns[3].Width = auto;
    }

    private void OnAddRoot()
    {
        if (_busy) return;
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择要额外扫描的项目/源码目录（仅安全扫描其内部的依赖目录与虚拟环境）",
            ShowNewFolderButton = false
        };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            if (_extraRoots.Any(r => string.Equals(r, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                AppendLog("该目录已在扫描范围内。");
                return;
            }
            _extraRoots.Add(dlg.SelectedPath);
            AppendLog($"已添加扫描目录：{dlg.SelectedPath}，点击「扫描」生效。");
        }
    }

    private async Task OnScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "扫描中...", marquee: true);
        _cts = new CancellationTokenSource();
        try
        {
            var scope = _scopeCombo.SelectedIndex switch
            {
                1 => ScanScope.WholeDrive,
                2 => ScanScope.AllFixedDrives,
                _ => ScanScope.UserProfile,
            };
            var callbacks = new DevScanCallbacks
            {
                Log = m => Ui(() => AppendLog(m)),
                DirsVisited = n => Ui(() => _lblStatus.Text = $"扫描中... 已检查 {n} 个目录"),
                MeasureProgress = (done, total) => Ui(() =>
                {
                    _lblStatus.Text = $"统计占用 {done}/{total} ...";
                    ReportProgress(done, total);
                }),
            };

            var result = await Task.Run(
                () => _scanner.ScanAsync(scope, _extraRoots, callbacks, _cts.Token), _cts.Token);

            _lastItems = result.Items;
            ReloadItems(result.Items);
            UpdateSummary(BuildSummaryText(result));
            var done = FormattableString.Invariant(
                $"扫描完成：检查 {result.VisitedDirs} 个目录，发现 {result.Items.Count} 项，用时 {result.ElapsedMs / 1000.0:0.0}s。");
            _lblStatus.Text = done;
            AppendLog(done);
            AppendLog($"  可清理合计 {SizeFormatter.Format(result.DeletableBytes)}；" +
                      $"仅统计（不提供删除）{result.ReportOnlyCount} 项 / {SizeFormatter.Format(result.ReportOnlyBytes)}；" +
                      $"重叠 {result.OverlapCount} 项" +
                      (result.HiddenCount > 0 ? $"；因条目上限隐藏 {result.HiddenCount} 项（已优先保留占用最大的）" : string.Empty) +
                      (result.DeniedCount > 0 ? $"；{result.DeniedCount} 项含无权限子目录（实际占用可能更大）" : string.Empty) + "。");
            AppendTopList(result.Items);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "扫描已取消。";
            AppendLog("扫描已取消，保留上次结果。");
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
    }

    private void ReloadItems(IReadOnlyList<DevItem> items)
    {
        _listView.Items.Clear();
        _listView.Groups.Clear();
        _lookup.Clear();

        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var cat in CategoryOrder)
        {
            var g = new ListViewGroup(cat);
            _listView.Groups.Add(g);
            groups[cat] = g;
        }
        foreach (var cat in items.Select(i => i.Category).Where(c => !groups.ContainsKey(c)).Distinct())
        {
            var g = new ListViewGroup(cat);
            _listView.Groups.Add(g);
            groups[cat] = g;
        }

        // 默认按占用排序（组内降序）：一眼看出最占空间的项
        IEnumerable<DevItem> ordered = items;
        if (_chkSortBySize.Checked)
            ordered = items.OrderBy(i => CategoryIndex(i.Category)).ThenByDescending(i => i.Size);

        foreach (var item in ordered)
        {
            var lvi = new ListViewItem(item.Type) { Tag = item, Checked = item.Recommended };
            lvi.SubItems.Add(item.Name);
            lvi.SubItems.Add(item.LibText);
            lvi.SubItems.Add(item.Path);
            lvi.SubItems.Add(item.SizeText);
            lvi.SubItems.Add(item.WarningText);
            lvi.ToolTipText = item.DetailText;
            lvi.Group = groups[item.Category];
            _listView.Items.Add(lvi);
            _lookup[item.Id] = lvi;
        }
    }

    private static int CategoryIndex(string category)
    {
        int idx = Array.IndexOf(CategoryOrder, category);
        return idx < 0 ? CategoryOrder.Length : idx;
    }

    /// <summary>汇总文案：可清理合计与仅统计（不可删除）分别展示。</summary>
    private static string BuildSummaryText(DevScanResult r)
    {
        string text = $"可清理 {SizeFormatter.Format(r.DeletableBytes)}" +
                      $"（{r.Items.Count(i => !i.IsReportOnly && !i.IsOverlapping)} 项）" +
                      $" · 仅统计 {SizeFormatter.Format(r.ReportOnlyBytes)}（{r.ReportOnlyCount} 项）";
        if (r.OverlapCount > 0) text += $" · 重叠 {r.OverlapCount}";
        return text;
    }

    /// <summary>日志中列出占用最大的若干项（含位置），便于直接判断“空间都去哪了”。</summary>
    private void AppendTopList(IReadOnlyList<DevItem> items)
    {
        var top = items.OrderByDescending(i => i.Size).Take(15).ToArray();
        if (top.Length == 0) return;
        AppendLog("  占用最大的条目：");
        foreach (var it in top)
            AppendLog($"    {it.SizeText,10}  [{it.Category}] {it.Type} → {it.Path}" +
                      (it.IsReportOnly ? "（仅统计）" : string.Empty));
    }

    private void ShowDetail(ListViewItem lvi)
    {
        if (lvi.Tag is not DevItem it) return;
        MessageBox.Show(this, it.DetailText, $"{it.Type} 详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetAllChecked(bool check)
    {
        foreach (ListViewItem lvi in _listView.Items)
        {
            if (check && lvi.Tag is DevItem { IsReportOnly: true }) continue; // 仅统计项不可勾选
            lvi.Checked = check;
        }
    }

    private IEnumerable<DevItem> SelectedItems()
    {
        foreach (ListViewItem lvi in _listView.CheckedItems)
            if (lvi.Tag is DevItem item)
                yield return item;
    }

    private async Task OnDeleteAsync()
    {
        if (_busy) return;
        var selected = SelectedItems().Where(i => !i.IsReportOnly && !i.IsFileTarget).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this,
                "请至少勾选一项可清理的条目。\n\n（「仅统计」类条目——工具链本体、虚拟磁盘、AI 工具运行时等——只展示占用与位置，本工具不提供删除。）",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 管理员权限处理（ProgramData 下的 Conda 环境）
        if (selected.Any(i => i.RequiresElevation) && !ElevationHelper.IsElevated())
        {
            var r = MessageBox.Show(this,
                "所选项目包含需要管理员权限的项（ProgramData 下的 Conda 环境）。\n\n" +
                "是否以管理员身份重启本程序？\n\n" +
                "点「否」则跳过这些项，仅删除无需管理员的项。",
                "需要管理员权限", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
            {
                if (ElevationHelper.TryRestartElevated(null, out string error))
                {
                    Application.Exit();
                    return;
                }
                MessageBox.Show(this, error + "\n\n将继续删除不需要管理员权限的项。",
                    "无法提权", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("提权未完成：" + error);
            }

            selected = selected.Where(i => !i.RequiresElevation).ToArray();
            if (selected.Length == 0)
            {
                AppendLog("已跳过所有需要管理员权限的项，未执行删除。");
                return;
            }
            AppendLog("已跳过需要管理员权限的项。");
        }

        long est = selected.Sum(i => i.Size);
        int envCount = selected.Count(i => i.Signature is DevSignature.PythonVenv or DevSignature.CondaEnv
            or DevSignature.NodeModules or DevSignature.ComposerVendor or DevSignature.CargoTarget);
        int aggCount = selected.Count(i => i.ExtraPaths.Count > 0);

        var confirm = MessageBox.Show(this,
            $"将删除 {selected.Length} 项，预计释放约 {SizeFormatter.Format(est)}。\n\n" +
            (envCount > 0
                ? $"其中 {envCount} 项为环境/依赖目录，将被整体删除，需重新创建环境并安装依赖。\n"
                : string.Empty) +
            (aggCount > 0
                ? $"其中 {aggCount} 项为聚合条目（如 Python 字节码缓存），只删除其列出的缓存子目录，不动工程本身。\n"
                : string.Empty) +
            "所有项目均经过安全校验（仅限识别出的开发产物目录）；缓存类删除后下次构建/安装会重新下载。\n" +
            "确认继续？",
            "确认删除（开发者环境）", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true, "删除中...", marquee: false, risk: BusyRisk.Interrupt);
        _cts = new CancellationTokenSource();
        try
        {
            var summary = await _scanner.DeleteItemsAsync(selected, ReportProgress,
                m => Ui(() => AppendLog(m)), _cts.Token);

            _lblStatus.Text = $"删除完成，释放 {SizeFormatter.Format(summary.FreedBytes)}。";
            AppendLog($"删除完成：释放 {SizeFormatter.Format(summary.FreedBytes)}，共 {summary.ItemsCount} 项。");
            AppendLog(summary.ErrorSummary());
            OperationLog.LogDevDelete(summary.ItemsCount, summary.FreedBytes, summary.Errors.Count);
            RefreshAfterDelete(selected);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "已取消删除。";
            AppendLog("删除已取消。");
            RefreshAfterDelete(selected);
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "删除失败。";
            AppendLog("删除失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>删除后刷新列表：目录已消失的项从列表移除，残留项（被占用）保留并提示。</summary>
    private void RefreshAfterDelete(IEnumerable<DevItem> selected)
    {
        int removed = 0;
        foreach (var item in selected)
        {
            if (!_lookup.TryGetValue(item.Id, out var lvi)) continue;

            if (item.ExtraPaths.Count > 0)
            {
                // 聚合项：移除已不存在的子目录并重算大小（工程根本身不参与统计）
                item.ExtraPaths.RemoveAll(p => !Directory.Exists(p));
                if (item.ExtraPaths.Count == 0)
                {
                    _listView.Items.Remove(lvi);
                    _lookup.Remove(item.Id);
                    removed++;
                    continue;
                }
                long sum = 0;
                foreach (var p in item.ExtraPaths)
                    sum += DirectoryHelper.GetDirectorySize(p);
                item.Size = sum;
                lvi.Checked = false;
                lvi.SubItems[4].Text = item.SizeText;
                continue;
            }

            if (!Directory.Exists(item.Path))
            {
                _listView.Items.Remove(lvi);
                _lookup.Remove(item.Id);
                removed++;
            }
            else
            {
                lvi.Checked = false;
                var entry = DirectoryHelper.MeasureDirectory(item.Path);
                item.Size = entry.Bytes;
                item.Denied = entry.Denied > 0;
                item.Truncated = entry.Truncated;
                lvi.SubItems[4].Text = item.SizeText;
            }
        }
        if (removed > 0)
            AppendLog($"列表中已移除 {removed} 项（对应目录已删除）。");

        var remaining = _lookup.Values.Select(v => v.Tag).OfType<DevItem>().ToList();
        long deletable = remaining.Where(i => !i.IsOverlapping && !i.IsReportOnly).Sum(i => i.Size);
        long reportOnly = remaining.Where(i => !i.IsOverlapping && i.IsReportOnly).Sum(i => i.Size);
        UpdateSummary($"可清理 {SizeFormatter.Format(deletable)}" +
                      $" · 仅统计 {SizeFormatter.Format(reportOnly)}（{remaining.Count(i => i.IsReportOnly)} 项）");
    }

    private void ReportProgress(int current, int total)
    {
        Ui(() =>
        {
            _progressBar.Maximum = Math.Max(total, 1);
            _progressBar.Value = Math.Min(current, _progressBar.Maximum);
            _lblStatus.Text = $"进行中 {current}/{total} ...";
            // 遮罩上的动画文案跟着走
            if (_busyLabel.Length > 0)
                _busyHost.Update($"{_busyLabel}　{current}/{total}");
        });
    }

    /// <summary>
    /// 统一设置"忙"状态并驱动处理中遮罩。
    ///
    /// 扫描/测算只读 → <see cref="BusyRisk.Safe"/>；「删除选中项」会真的删目录，
    /// 传 <see cref="BusyRisk.Interrupt"/>（可取消，关窗前先确认一次）。
    /// </summary>
    private void SetBusy(bool busy, string? status, bool marquee = false,
        BusyRisk risk = BusyRisk.Safe)
    {
        _busy = busy;
        _btnScan.Enabled = !busy;
        _btnAddRoot.Enabled = !busy;
        _btnSelectAll.Enabled = !busy;
        _btnSelectNone.Enabled = !busy;
        _btnDelete.Enabled = !busy;
        _chkSortBySize.Enabled = !busy;
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
