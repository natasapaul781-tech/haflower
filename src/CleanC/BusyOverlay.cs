using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 「处理中」遮罩：盖住整个窗口，中间显示旋转的圆弧动画 + 当前操作说明。
///
/// 三重作用：
/// ① **可视化"程序还活着"** —— 长时间操作（分区收缩、卸载、扫描）期间有明确的动态反馈，
///    避免用户以为卡死而强行关窗口/断电；
/// ② **挡住操作** —— 遮罩浮在最上层，鼠标点不到下面的按钮，从物理上阻止"边跑边点别的"；
/// ③ **区分危险程度** —— 不可中断的危险操作会额外显示红色警示（不要关闭、不要断电），
///    可中断的操作则在遮罩上提供「取消」按钮。
///
/// 只负责显示；"不许关闭窗口"由 <see cref="OperationGuard"/> 负责。
/// </summary>
/// <remarks>
/// 风险分三档，见 <see cref="BusyRisk"/>：只读扫描给取消按钮、可直接关窗；
/// 会改文件但可安全中断的（清理、删缓存）取消前先确认关窗；
/// 分区/卸载这类不可安全中断的显示红色警示、不给取消、并拒绝关窗。
/// </remarks>
public sealed class BusyOverlay : Panel
{
    /// <summary>卡片高度（96 DPI 下的逻辑值）：带「取消」按钮时更高一些。实际使用时按 DPI 缩放。</summary>
    private const int CardHeightWithCancel = 196;
    private const int CardHeightPlain = 164;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly RoundedButton _btnCancel;

    // 字体只建一次：OnPaint 每 33ms 跑一次，在绘制里 new Font 会持续产生无法及时释放的 GDI 字体
    private readonly Font _fontMessage = UiStyle.Font(9.5f, bold: true);
    private readonly Font _fontSub = UiStyle.Font(8.5f);

    /// <summary>DPI 缩放系数（96 DPI = 1，150% = 1.5）；为 0 时退回 1。</summary>
    private int ScaleUnit
    {
        get
        {
            try { return Math.Max(1, DeviceDpi / 96); }
            catch (Exception) { return 1; }
        }
    }

    private Font MessageFont => _fontMessage;

    private Font SubFont => _fontSub;

    private float _angle;
    private string _message = string.Empty;
    private BusyRisk _risk;
    private bool _allowCancel;
    private bool _pendingShow;

    private bool Critical => _risk == BusyRisk.Critical;

    /// <summary>用户点了遮罩上的「取消」时执行（由宿主窗体设置）。</summary>
    public Action? CancelAction { get; set; }

    public BusyOverlay()
    {
        BackColor = UiStyle.Bg;
        Visible = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // 只做显示，不应抢键盘焦点
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;

        _btnCancel = UiStyle.MakeButton("取消", ButtonVariant.Secondary, 96, () => CancelAction?.Invoke());
        _btnCancel.Visible = false;
        Controls.Add(_btnCancel);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };   // ≈30fps
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 10f) % 360f;
            Invalidate();
        };
    }

    /// <summary>当前是否正在显示。</summary>
    public bool IsBusy => Visible;

    /// <summary>当前风险等级（测试/断言用）。</summary>
    public BusyRisk Risk => _risk;

    /// <summary>当前是否提供「取消」按钮（测试用）。</summary>
    public bool CanCancel => _allowCancel;

    /// <summary>居中卡片的矩形（布局检查用；与实际绘制用同一份计算）。</summary>
    public Rectangle CardBounds => CardRect();

    /// <summary>「取消」按钮的矩形（布局检查用；未显示时为 <see cref="Rectangle.Empty"/>）。</summary>
    public Rectangle CancelButtonBounds => _allowCancel ? _btnCancel.Bounds : Rectangle.Empty;

    private Rectangle CardRect()
    {
        int s = ScaleUnit;
        int cardW = Math.Min(460 * s, Math.Max(300 * s, ClientSize.Width - 60 * s));
        int cardH = (_allowCancel ? CardHeightWithCancel : CardHeightPlain) * s;
        int cardX = (ClientSize.Width - cardW) / 2;
        int cardY = Math.Max(20 * s, (ClientSize.Height - cardH) / 2);
        return new Rectangle(cardX, cardY, cardW, cardH);
    }

    /// <summary>开始显示遮罩。</summary>
    /// <param name="message">主文案（正在做什么）。</param>
    /// <param name="risk">风险等级，见 <see cref="BusyRisk"/>。</param>
    /// <param name="allowCancel">是否提供「取消」按钮（只有可安全中断的操作才给）。</param>
    public void Begin(string message, BusyRisk risk, bool allowCancel)
    {
        _message = message;
        _risk = risk;
        _allowCancel = allowCancel && risk != BusyRisk.Critical;   // 危险操作一律不给取消入口
        _btnCancel.Visible = _allowCancel;

        // 宿主窗体还没创建句柄时（构造期、Show 之前就进入长任务），WinForms 会**直接忽略**
        // Visible = true——必须等窗体真正显示出来再补一次，否则遮罩永远不出现。
        if (Parent is { IsHandleCreated: false })
        {
            _pendingShow = true;
            Parent.HandleCreated -= OnParentReady;
            Parent.VisibleChanged -= OnParentReady;
            Parent.HandleCreated += OnParentReady;
            Parent.VisibleChanged += OnParentReady;
        }
        else
        {
            ShowOverlay();
        }
    }

    private void OnParentReady(object? sender, EventArgs e)
    {
        if (!_pendingShow)
        {
            DetachParentHandlers();
            return;
        }

        ShowOverlay();
        if (Visible)   // 真正显示出来了才收工
        {
            _pendingShow = false;
            DetachParentHandlers();
        }
    }

    private void DetachParentHandlers()
    {
        if (Parent is null) return;
        Parent.HandleCreated -= OnParentReady;
        Parent.VisibleChanged -= OnParentReady;
    }

    private void ShowOverlay()
    {
        Bounds = Parent?.ClientRectangle ?? Bounds;
        BringToFront();
        Visible = true;
        if (_allowCancel) LayoutCancelButton();
        _timer.Start();
        Invalidate();
    }

    /// <summary>更新主文案（进度变化时调用；遮罩未显示则忽略）。</summary>
    public void Update(string message)
    {
        if (!Visible) return;
        _message = message;
        Invalidate();
    }

    /// <summary>结束显示（停止动画，恢复下层可操作）。</summary>
    public void End()
    {
        _timer.Stop();
        Visible = false;
        _btnCancel.Visible = false;
        _btnCancel.Bounds = Rectangle.Empty;
        _message = string.Empty;
        _risk = BusyRisk.Safe;
        _allowCancel = false;
        _pendingShow = false;
        DetachParentHandlers();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        // 居中卡片
        var card = CardBounds;
        int cardW = card.Width;
        int cardH = card.Height;
        int cardX = card.X;
        int cardY = card.Y;
        // 卡片内的坐标必须跟着 DPI 缩放：字号按"磅"走、会随 DPI 变大，而写死的像素偏移不会，
        // 150% 缩放下主文案会撞上下面的副文案/取消按钮。
        int s = ScaleUnit;

        using (var path = UiStyle.Rounded(card, 10 * s / 1))
        {
            using var fill = new SolidBrush(UiStyle.Card);
            g.FillPath(fill, path);
            using var pen = new Pen(Critical ? UiStyle.Danger : UiStyle.Border, Critical ? 2f : 1f);
            g.DrawPath(pen, path);
        }

        DrawSpinner(g, new Point(cardX + cardW / 2, cardY + 40 * s), 16 * s,
            Critical ? UiStyle.Danger : UiStyle.Primary);

        // 主文案（可能很长，按卡片宽度居中折行显示为一行省略号）
        var msgRect = new Rectangle(card.X + 18 * s, cardY + 68 * s, cardW - 36 * s, 40 * s);
        TextRenderer.DrawText(g, _message, MessageFont, msgRect,
            UiStyle.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        string sub = Critical
            ? "⚠ 危险操作进行中：请不要关闭窗口、不要断电、不要强制关机"
            : _risk == BusyRisk.Interrupt
                ? "可以点「取消」停止；已开始的当前项不会被打断"
                : _allowCancel ? "可以点「取消」停止；已开始的当前项不会被打断" : "请稍候…";
        var subRect = new Rectangle(card.X + 18 * s, cardY + 112 * s, cardW - 36 * s, 36 * s);
        TextRenderer.DrawText(g, sub, SubFont, subRect,
            Critical ? UiStyle.Danger : UiStyle.SubText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);

        if (_allowCancel)
        {
            LayoutCancelButton();
        }
        else
        {
            _btnCancel.Bounds = Rectangle.Empty;   // 不显示时清掉，避免布局检查读到旧值
        }
    }

    /// <summary>把「取消」按钮摆到卡片底部居中（绘制与逻辑共用同一份计算）。</summary>
    private void LayoutCancelButton()
    {
        var card = CardRect();
        _btnCancel.Bounds = new Rectangle(
            card.X + (card.Width - _btnCancel.Width) / 2,
            card.Y + card.Height - _btnCancel.Height - 14,
            _btnCancel.Width,
            _btnCancel.Height);
    }

    /// <summary>
    /// 画一个旋转的环形指示器：12 根带渐变透明度的短弧，随时间旋转（纯 GDI+，无外部资源）。
    /// </summary>
    private void DrawSpinner(Graphics g, Point center, int radius, Color color)
    {
        const int segments = 12;
        for (int i = 0; i < segments; i++)
        {
            // 亮度随序号递减，形成"拖尾"效果
            int alpha = 40 + (int)(215.0 * i / (segments - 1));
            float angle = _angle + i * (360f / segments);
            using var pen = new Pen(Color.FromArgb(alpha, color), Math.Max(2f, 3f * ScaleUnit / 1f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            double rad = angle * Math.PI / 180.0;
            int x1 = center.X + (int)(Math.Cos(rad) * (radius - 6));
            int y1 = center.Y + (int)(Math.Sin(rad) * (radius - 6));
            int x2 = center.X + (int)(Math.Cos(rad) * radius);
            int y2 = center.Y + (int)(Math.Sin(rad) * radius);
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachParentHandlers();
            _timer.Dispose();
            _fontMessage.Dispose();
            _fontSub.Dispose();
        }
        base.Dispose(disposing);
    }
}
