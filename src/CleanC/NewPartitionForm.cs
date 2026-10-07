using CleanC.Core;
using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 「用未分配空间新建分区」窗口。
///
/// 场景：磁盘上有**已经空着**的一段空间（磁盘本来就没分满；或者上一个操作只完成了一半——
/// 收缩成功但新建失败、删除分区成功但扩容失败）。这里把它建成一个新分区并格式化。
///
/// 与「分盘」「合盘」的根本区别：**不收缩、不删除、不移动任何现有分区**，
/// 只使用不属于任何分区的空间，所以它既是常规操作，也是半完成状态的修复路径。
///
/// 安全闸门（与合盘一致的思路，但风险等级更低）：
/// ① 只允许在探测到的未分配空间内建卷，起点按 1 MB 对齐（Windows 拒绝未对齐的偏移）；
/// ② 执行前**重新枚举该磁盘的分区**，确认那段空间此刻仍然空着（计划可能是几分钟前生成的）；
/// ③ 只格式化**刚创建、且 GUID + 偏移 + 大小三重匹配**的分区，绝不按盘符猜目标；
/// ④ 执行期间走「处理中遮罩 + 拒绝关闭窗口」（危险档），避免半完成状态。
/// </summary>
public sealed class NewPartitionForm : Form
{
    private readonly DiskProbeResult _probe;

    private ComboBox _cmbDisk = null!;
    private ComboBox _cmbExtent = null!;
    private Label _lblExtentCaption = null!;
    private ComboBox _cmbLetter = null!;
    private TextBox _txtLabel = null!;
    private TrackBar _slider = null!;
    private Label _lblExtentInfo = null!;
    private Label _lblSize = null!;
    private Label _lblVerdict = null!;
    private RoundedButton _btnBlocks = null!;
    private RoundedButton _btnFill = null!;
    private RoundedButton _btnRestart = null!;
    private ListView _lstSteps = null!;
    private CheckBox _chkAck = null!;
    private RoundedButton _btnCreate = null!;
    private RoundedButton _btnCancel = null!;
    private Panel _gatePanel = null!;
    private Panel _stepsPanel = null!;
    private UiBusyHost _busyHost = null!;

    private FreeExtent? _extent;
    private PartitionPlan? _plan;
    private IReadOnlyList<BlockReason> _blocks = Array.Empty<BlockReason>();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _createText = "新建并格式化";

    /// <summary>要新建的大小（字节）。点「用满整段」时是精确值，拖滑块时是整 GB。</summary>
    private long _sizeBytes;

    /// <summary>执行结果（成功关闭时有值，供调用方汇总）。</summary>
    public PartitionExecutionReport? Report { get; private set; }

    public NewPartitionForm(DiskProbeResult probe)
    {
        _probe = probe;

        Text = "用未分配空间新建分区";
        Font = UiStyle.Font(9f);
        BackColor = UiStyle.Bg;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        UiStyle.FitToWorkingArea(this, new Size(940, 700), new Size(720, 520), margin: 100);

        InitializeUi();
        ReloadDisks();
    }

    // ==================== 界面 ====================

    private void InitializeUi()
    {
        // ---- 顶部说明 ----
        var banner = UiStyle.CardPanel(DockStyle.Top);
        banner.Height = 62;
        var title = new Label
        {
            AutoSize = false,
            Location = new Point(16, 10),
            Size = new Size(800, 22),
            Text = "用磁盘上的「未分配」空间新建一个分区",
            Font = UiStyle.Font(12.5f, bold: true),
            ForeColor = UiStyle.Text,
            AutoEllipsis = true,
        };
        var sub = new Label
        {
            AutoSize = false,
            Location = new Point(16, 35),
            Size = new Size(800, 18),
            Text = "只使用不属于任何分区的空间：不收缩、不删除、不移动任何现有分区；新分区会被格式化为 NTFS（里面本来是空的）。",
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };
        banner.Controls.AddRange(new Control[] { title, sub });
        banner.Resize += (_, _) =>
        {
            title.Width = Math.Max(200, banner.ClientSize.Width - 32);
            sub.Width = Math.Max(200, banner.ClientSize.Width - 32);
        };

        // ---- 选项区（固定 Y，只有宽度自适应） ----
        var form = new Panel { Dock = DockStyle.Top, Height = 180, BackColor = UiStyle.Bg };

        var lblDisk = new Label { AutoSize = true, Location = new Point(12, 18), Text = "磁盘：", ForeColor = UiStyle.Text };
        _cmbDisk = new ComboBox
        {
            Location = new Point(76, 14),
            Width = 300,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbDisk.SelectedIndexChanged += (_, _) => ReloadExtents();

        _lblExtentCaption = new Label { AutoSize = true, Location = new Point(396, 18), Text = "未分配空间：", ForeColor = UiStyle.Text };
        _cmbExtent = new ComboBox
        {
            Location = new Point(484, 14),
            Width = 260,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbExtent.SelectedIndexChanged += (_, _) => OnExtentChanged();

        _btnFill = UiStyle.MakeButton("用满整段", ButtonVariant.Secondary, 96, OnFill);
        _btnFill.Location = new Point(756, 13);

        _slider = new TrackBar
        {
            Location = new Point(10, 50),
            Width = 700,
            Height = 45,          // TrackBar 实际就是 45 高（设 40 也没用），下面的标签要按 45 让位
            TickStyle = TickStyle.BottomRight,
            SmallChange = 1,
            LargeChange = 10,
            Enabled = false,
        };
        _slider.Scroll += (_, _) => OnSliderChanged();

        _lblSize = new Label
        {
            AutoSize = false,
            Location = new Point(12, 98),
            Size = new Size(700, 20),
            Font = UiStyle.Font(9.5f, bold: true),
            ForeColor = UiStyle.Primary,
        };
        _lblExtentInfo = new Label
        {
            AutoSize = false,
            Location = new Point(12, 120),
            Size = new Size(880, 18),
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };

        var lblLetter = new Label { AutoSize = true, Location = new Point(12, 150), Text = "盘符：", ForeColor = UiStyle.Text };
        _cmbLetter = new ComboBox
        {
            Location = new Point(76, 146),
            Width = 70,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiStyle.Font(9f),
        };
        _cmbLetter.SelectedIndexChanged += (_, _) => RebuildPlan();
        var lblLabel = new Label { AutoSize = true, Location = new Point(166, 150), Text = "卷标：", ForeColor = UiStyle.Text };
        _txtLabel = new TextBox
        {
            Location = new Point(212, 146),
            Width = 180,
            Font = UiStyle.Font(9f),
            Text = PartitionPlanBuilder.DefaultCreateVolumeLabel,
        };
        _txtLabel.TextChanged += (_, _) => RebuildPlan();

        _lblVerdict = new Label
        {
            AutoSize = false,
            Location = new Point(420, 150),
            Size = new Size(360, 20),
            Font = UiStyle.Font(9f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };
        _btnBlocks = UiStyle.MakeButton("查看校验…", ButtonVariant.Secondary, 100, ShowBlocks, height: 26);
        _btnBlocks.Location = new Point(790, 146);
        _btnBlocks.Enabled = false;

        form.Controls.AddRange(new Control[]
        {
            lblDisk, _cmbDisk, _lblExtentCaption, _cmbExtent, _btnFill,
            _slider, _lblSize, _lblExtentInfo,
            lblLetter, _cmbLetter, lblLabel, _txtLabel, _lblVerdict, _btnBlocks,
        });
        form.Resize += (_, _) => LayoutOptions(form);

        // ---- 计划步骤 ----
        _stepsPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiStyle.Bg, Padding = new Padding(12, 0, 12, 0) };
        _lstSteps = new ListView { Dock = DockStyle.Fill };
        UiStyle.StyleListView(_lstSteps);
        _lstSteps.View = View.Details;
        _lstSteps.Columns.Add("将要执行的步骤", 700);
        _lstSteps.Columns.Add("标记", 120);
        _lstSteps.Resize += (_, _) =>
        {
            if (_lstSteps.Columns.Count < 2) return;
            _lstSteps.Columns[0].Width = Math.Max(240, _lstSteps.ClientSize.Width - 138);
            _lstSteps.Columns[1].Width = 118;
        };
        _lstSteps.DoubleClick += (_, _) => ShowStepDetail();
        _stepsPanel.Controls.Add(_lstSteps);

        // ---- 底部闸门 ----
        _gatePanel = new Panel { Dock = DockStyle.Bottom, Height = 104, BackColor = UiStyle.Card };
        _chkAck = new CheckBox
        {
            AutoSize = false,
            Location = new Point(16, 8),
            Size = new Size(880, 22),
            Text = "我确认：在这段未分配空间上新建分区，并把新分区格式化为 NTFS（不会影响任何现有分区）",
            Font = UiStyle.Font(9.5f, bold: true),
            ForeColor = UiStyle.Text,
        };
        _chkAck.CheckedChanged += (_, _) => UpdateGate();

        _btnCancel = UiStyle.MakeButton("取消", ButtonVariant.Primary, 96, () =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        });
        _btnCreate = UiStyle.MakeButton("新建并格式化", ButtonVariant.Danger, 156, async () => await ExecuteAsync());
        _btnCreate.Enabled = false;

        _btnRestart = UiStyle.MakeButton("以管理员身份重新启动", ButtonVariant.Danger, 168, OnElevate);
        _btnRestart.Visible = false;

        var hint = new Label
        {
            AutoSize = false,
            Location = new Point(16, 74),
            Size = new Size(880, 18),
            Text = "提示：分两步——先建分区，再格式化；每一步都会在执行前复验身份，任一步失败会立即停下并给出恢复指引。",
            Font = UiStyle.Font(8.5f),
            ForeColor = UiStyle.SubText,
            AutoEllipsis = true,
        };
        _gatePanel.Controls.AddRange(new Control[] { _chkAck, _btnCancel, _btnCreate, _btnRestart, hint });
        _gatePanel.Resize += (_, _) =>
        {
            _btnCancel.Location = new Point(16, 36);
            _btnCreate.Location = new Point(_gatePanel.ClientSize.Width - _btnCreate.Width - 16, 36);
            _btnRestart.Location = new Point(_gatePanel.ClientSize.Width - _btnRestart.Width - 16, 36);
            _chkAck.Width = Math.Max(240, _gatePanel.ClientSize.Width - 32);
            hint.Width = Math.Max(240, _gatePanel.ClientSize.Width - 32);
        };

        AcceptButton = _btnCancel;   // 回车 = 取消，不会误触发新建
        CancelButton = _btnCancel;

        Controls.Add(_stepsPanel);
        Controls.Add(_gatePanel);
        Controls.Add(form);
        Controls.Add(banner);

        _busyHost = new UiBusyHost(this);
    }

    private void LayoutOptions(Panel form)
    {
        int w = form.ClientSize.Width;
        // 第一行：磁盘 + 未分配空间 + 用满整段（左右分栏，靠宽度避让；标签宽度按 80 预留）
        _cmbDisk.Width = Math.Max(180, Math.Min(320, w / 3));
        _btnFill.Left = Math.Max(560, w - _btnFill.Width - 16);
        _lblExtentCaption.Left = _cmbDisk.Right + 16;
        _cmbExtent.Left = _lblExtentCaption.Right + 8;
        _cmbExtent.Width = Math.Max(140, _btnFill.Left - _cmbExtent.Left - 12);

        _slider.Width = Math.Max(300, w - 40);
        _lblSize.Width = Math.Max(200, w - 40);
        _lblExtentInfo.Width = Math.Max(200, w - 40);

        _btnBlocks.Left = Math.Max(430, w - _btnBlocks.Width - 16);
        _lblVerdict.Left = _txtLabel.Right + 12;
        _lblVerdict.Width = Math.Max(160, _btnBlocks.Left - _lblVerdict.Left - 10);
    }

    // ==================== 数据加载 ====================

    private void ReloadDisks()
    {
        _cmbDisk.Items.Clear();
        foreach (var d in _probe.Layout.Disks.OrderBy(d => d.Number))
        {
            var free = _probe.Layout.FreeExtentsOfDisk(d.Number);
            string suffix = free.Count == 0
                ? "无未分配空间"
                : $"未分配 {SizeFormatter.Format((long)free.Sum(f => (long)f.SizeBytes))}";
            _cmbDisk.Items.Add(new DiskChoice(d.Number, $"磁盘 {d.Number}　{d.FriendlyName}　{SizeFormatter.Format((long)d.SizeBytes)}　{suffix}", free.Count > 0));
        }

        // 默认选中"有未分配空间且空间最大"的那块盘——用户多半就是为它来的
        int best = -1;
        long bestFree = 0;
        for (int i = 0; i < _cmbDisk.Items.Count; i++)
        {
            if (_cmbDisk.Items[i] is not DiskChoice dc || !dc.HasFree) continue;
            long free = _probe.Layout.FreeExtentsOfDisk(dc.Number).Sum(f => (long)f.SizeBytes);
            if (free > bestFree) { bestFree = free; best = i; }
        }

        if (best >= 0) _cmbDisk.SelectedIndex = best;
        else if (_cmbDisk.Items.Count > 0) _cmbDisk.SelectedIndex = 0;

        ReloadExtents();
    }

    private void ReloadExtents()
    {
        _cmbExtent.Items.Clear();
        _extent = null;
        _plan = null;

        if (SelectedDisk() is not { } dc)
        {
            RefreshState();
            return;
        }

        var extents = _probe.Layout.FreeExtentsOfDisk(dc.Number);
        foreach (var e in extents) _cmbExtent.Items.Add(e);
        if (_cmbExtent.Items.Count > 0)
        {
            // 默认选**最大**那段：磁盘上有多段空闲时，用户多半就是要用最大的那段
            // （按偏移取第一个会选中开头的小缝隙）。
            int largest = 0;
            for (int i = 1; i < extents.Count; i++)
                if (extents[i].SizeBytes > extents[largest].SizeBytes) largest = i;
            _cmbExtent.SelectedIndex = largest;
        }

        FillLetters();
        OnExtentChanged();
    }

    private void FillLetters()
    {
        _cmbLetter.Items.Clear();
        var used = new HashSet<char>(_probe.Layout.UsedDriveLetters);
        for (char c = 'D'; c <= 'Z'; c++)
        {
            if (used.Contains(c)) continue;
            _cmbLetter.Items.Add(c.ToString());
        }

        if (_cmbLetter.Items.Count == 0) return;

        // 默认给 D:（数据盘的惯例）；被占用时退回探测器建议的第一个可用字母
        char? wanted = used.Contains('D')
            ? DiskLayoutService.SuggestFreeDriveLetter(_probe.Layout.UsedDriveLetters, _probe.Layout.SystemDriveLetter)
            : 'D';
        int index = wanted is { } w ? _cmbLetter.Items.IndexOf(w.ToString()) : -1;
        _cmbLetter.SelectedIndex = index >= 0 ? index : 0;
    }

    private DiskChoice? SelectedDisk() => _cmbDisk.SelectedItem as DiskChoice;

    private void OnExtentChanged()
    {
        _extent = _cmbExtent.SelectedItem as FreeExtent;
        _plan = null;

        if (_extent is null)
        {
            _sizeBytes = 0;
            _slider.Enabled = false;
            RefreshState();
            return;
        }

        // 默认用满整段（含 1 MB 对齐后的精确字节数）
        _sizeBytes = (long)_extent.AlignedSizeBytes;

        long maxGb = Math.Max(1, _sizeBytes / (1024L * 1024 * 1024));
        _slider.Minimum = 1;
        _slider.Maximum = (int)Math.Min(int.MaxValue, maxGb);
        _slider.Value = (int)Math.Min(maxGb, Math.Max(1, _slider.Maximum));
        _slider.Enabled = true;

        _lblExtentInfo.Text =
            $"该段空间：{_extent.SizeText}（起始 {SizeFormatter.Format((long)_extent.StartBytes)}，结束 {SizeFormatter.Format((long)_extent.EndBytes)}）" +
            (_extent.AlignmentWasteBytes > 0
                ? $"；为使 Windows 接受，起点按 1 MB 对齐，可用 {SizeFormatter.Format((long)_extent.AlignedSizeBytes)}"
                : "；起点已按 1 MB 对齐");

        RebuildPlan();
    }

    private void OnFill()
    {
        if (_extent is null) return;
        _sizeBytes = (long)_extent.AlignedSizeBytes;
        long gb = Math.Max(1, _sizeBytes / (1024L * 1024 * 1024));
        _slider.Value = (int)Math.Min(_slider.Maximum, gb);
        RebuildPlan();
    }

    private void OnSliderChanged()
    {
        if (_extent is null) return;
        long gb = _slider.Value;
        long bytes = gb * 1024L * 1024 * 1024;
        // 滑块最大就是整段空间，但整段的对齐后字节数可能比"整 GB"略大
        _sizeBytes = Math.Min(bytes, (long)_extent.AlignedSizeBytes);
        RebuildPlan();
    }

    // ==================== 计划与校验 ====================

    private void RebuildPlan()
    {
        _plan = null;

        char? letter = _cmbLetter.SelectedItem is string s && s.Length == 1 ? s[0] : null;
        _blocks = PartitionPlanValidator.ValidateCreate(_probe, _extent, _sizeBytes, letter, _txtLabel.Text);

        bool canExecute = _extent is not null && letter is not null && !_blocks.HasBlocking();

        if (_extent is not null && letter is { } l && _sizeBytes > 0)
        {
            _plan = PartitionPlanBuilder.BuildCreate(_probe, _extent, _sizeBytes, l, _txtLabel.Text, dryRun: true);
            FillSteps(_plan);
        }
        else
        {
            _lstSteps.Items.Clear();
        }

        // 底部按钮文案随选择变化
        _createText = letter is { } l2 ? $"新建并格式化 {l2}:" : "新建并格式化";

        RefreshState(canExecute);
    }

    private void FillSteps(PartitionPlan plan)
    {
        _lstSteps.BeginUpdate();
        _lstSteps.Items.Clear();
        foreach (var s in plan.Steps)
        {
            var lvi = new ListViewItem(s.Title);
            lvi.SubItems.Add(s.IsDestructive ? "不可逆" : s.IsInterruptible ? "可中断" : "执行中不中断");
            lvi.Tag = s;
            _lstSteps.Items.Add(lvi);
        }
        _lstSteps.EndUpdate();
    }

    private void RefreshState(bool? canExecute = null)
    {
        if (_extent is null)
        {
            _lblSize.Text = "该磁盘上没有 ≥ 1 MB 的未分配空间";
            _lblSize.ForeColor = UiStyle.Danger;
        }
        else
        {
            _lblSize.Text = $"新分区大小：{SizeFormatter.Format(_sizeBytes)}" +
                            (_sizeBytes >= (long)_extent.AlignedSizeBytes
                                ? "（用满整段）"
                                : $"　·　建完后剩余未分配约 {SizeFormatter.Format((long)_extent.AlignedSizeBytes - _sizeBytes)}");
            _lblSize.ForeColor = UiStyle.Primary;
        }

        var blocking = _blocks.Blocking();
        _btnBlocks.Enabled = _blocks.Count > 0;
        if (!_probe.Layout.IsElevated)
        {
            _lblVerdict.Text = "未以管理员身份运行，无法操作磁盘";
            _lblVerdict.ForeColor = UiStyle.Danger;
            _btnRestart.Visible = true;
        }
        else if (blocking.Count > 0)
        {
            _lblVerdict.Text = $"✘ {blocking.Count} 项会阻止执行：{blocking[0].Title}";
            _lblVerdict.ForeColor = UiStyle.Danger;
            _btnRestart.Visible = false;
        }
        else if (_blocks.Count > 0)
        {
            _lblVerdict.Text = $"✔ 可以执行（{_blocks.Count} 条提示）";
            _lblVerdict.ForeColor = UiStyle.Success;
            _btnRestart.Visible = false;
        }
        else
        {
            _lblVerdict.Text = "✔ 可以执行";
            _lblVerdict.ForeColor = UiStyle.Success;
            _btnRestart.Visible = false;
        }

        _chkAck.Enabled = canExecute ?? (_extent is not null && !_blocks.HasBlocking());
        UpdateGate();
    }

    private void UpdateGate()
    {
        bool ready = !_busy && _extent is not null && _plan is not null && !_blocks.HasBlocking() && _chkAck.Checked;
        _btnCreate.Enabled = ready;

        // 只差勾选时明确提示，其它情况保持正常文案（避免"明明被阻断却让你去勾选"）
        bool canAck = _extent is not null && _plan is not null && !_blocks.HasBlocking();
        _btnCreate.Text = canAck && !_chkAck.Checked ? "请先勾选上面的确认" : _createText;
    }

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

    // ==================== 执行 ====================

    private async Task ExecuteAsync()
    {
        if (_busy || _plan is null || _extent is null || !_chkAck.Checked) return;

        var plan = _plan;
        SetBusy(true, $"正在新建 {plan.NewDriveLetter}:（{SizeFormatter.Format(plan.FreedBytes)}）...");

        _cts = new CancellationTokenSource();
        try
        {
            var report = await PartitionExecutor.ExecuteCreateAsync(
                _probe, plan,
                (i, total, msg) => Ui(() => _busyHost.Update($"第 {i}/{total} 步：{msg}")),
                msg => Ui(() => OperationLog.Log("分区-新建", msg)),
                _cts.Token);

            Report = report;
            SetBusy(false, null);

            using var tv = new TextViewForm(
                report.Succeeded ? $"新建分区完成：{plan.NewDriveLetter}:" : "新建分区未完成",
                report.ToText(),
                report.Succeeded
                    ? "新分区已创建并格式化为 NTFS，可以在「此电脑」里使用了。"
                    : "按下面的提示处理；磁盘上可能留下一段未分配空间，重新打开本窗口即可继续建卷。");
            tv.ShowDialog(this);

            if (report.Succeeded)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                ReloadExtents();   // 失败后重新读一次布局，让用户看到当前真实状态
            }
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, null);
            RefreshState();
        }
        catch (Exception ex)
        {
            SetBusy(false, null);
            OperationLog.Log("分区-新建", "异常：" + ex.Message);
            MessageBox.Show(this, "新建分区失败：\n\n" + ex.Message, "失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            ReloadExtents();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        _cmbDisk.Enabled = !busy;
        _cmbExtent.Enabled = !busy;
        _cmbLetter.Enabled = !busy;
        _txtLabel.Enabled = !busy;
        _slider.Enabled = !busy && _extent is not null;
        _btnFill.Enabled = !busy;
        _btnCancel.Enabled = !busy;
        if (busy)
        {
            // 危险档：期间拒绝关闭窗口（改分区表不能中途退出）
            _busyHost.Begin(status ?? "处理中…", BusyRisk.Critical, null);
            _chkAck.Enabled = false;
            _btnCreate.Enabled = false;
        }
        else
        {
            _busyHost.End();
            _chkAck.Enabled = _extent is not null && !_blocks.HasBlocking();
            UpdateGate();
        }
    }

    // ==================== 弹窗 ====================

    private void ShowBlocks()
    {
        using var tv = new TextViewForm("新建分区校验结果", BlockersForm.Summarize(_blocks),
            $"阻断 {_blocks.Blocking().Count} 项　提示 {_blocks.Count - _blocks.Blocking().Count} 项");
        tv.ShowDialog(this);
    }

    private void ShowStepDetail()
    {
        if (_lstSteps.SelectedItems.Count == 0) return;
        if (_lstSteps.SelectedItems[0].Tag is not PartitionStep step) return;

        string text =
            $"步骤 {step.Index}：{step.Title}\n\n" +
            $"【做什么】\n{step.Action}\n\n" +
            $"【预期结果】\n{step.Expected}\n\n" +
            $"【出错了怎么办】\n{step.OnFailure}\n\n" +
            $"【可中断】{(step.IsInterruptible ? "可以（在步骤边界）" : "不可以（一旦发出必须等它结束）")}\n" +
            $"【破坏性】{(step.IsDestructive ? "不可逆" : "否")}\n\n" +
            $"【等价命令】\n{step.PowerShell}";

        using var tv = new TextViewForm($"步骤 {step.Index} 详情", text, "可直接复制上面的命令到管理员 PowerShell 里手工执行。");
        tv.ShowDialog(this);
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    /// <summary>磁盘下拉项：显示名称与"有没有未分配空间"。</summary>
    private sealed record DiskChoice(int Number, string Text, bool HasFree)
    {
        public override string ToString() => Text;
    }
}
