using System.Drawing.Drawing2D;

namespace CleanC;

/// <summary>按钮视觉变体。</summary>
public enum ButtonVariant
{
    /// <summary>主操作：蓝底白字。</summary>
    Primary,

    /// <summary>危险操作：红底白字。</summary>
    Danger,

    /// <summary>次级操作：白底蓝字带边框。</summary>
    Secondary,

    /// <summary>安静操作：透明底灰字（返回/取消/跳过）。</summary>
    Quiet,
}

/// <summary>
/// 全局 UI 样式：统一配色、字体与控件外观（简约扁平、圆角、无第三方资源，全部 GDI+ 自绘）。
/// </summary>
public static class UiStyle
{
    // ---------- 色板 ----------
    public static readonly Color Primary = Color.FromArgb(47, 110, 224);
    public static readonly Color PrimaryDark = Color.FromArgb(38, 92, 190);
    public static readonly Color PrimaryHover = Color.FromArgb(66, 124, 232);
    public static readonly Color Danger = Color.FromArgb(214, 69, 69);
    public static readonly Color DangerDark = Color.FromArgb(186, 55, 55);
    public static readonly Color DangerHover = Color.FromArgb(224, 84, 84);
    public static readonly Color Text = Color.FromArgb(45, 49, 55);
    public static readonly Color SubText = Color.FromArgb(108, 116, 128);
    public static readonly Color Bg = Color.FromArgb(246, 247, 249);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(226, 230, 235);
    public static readonly Color HoverBg = Color.FromArgb(239, 243, 250);
    public static readonly Color DisabledBg = Color.FromArgb(233, 236, 240);
    public static readonly Color LogBg = Color.FromArgb(250, 250, 252);
    public static readonly Color WarningBg = Color.FromArgb(253, 243, 243);
    public static readonly Color WarningBorder = Color.FromArgb(243, 208, 208);
    public static readonly Color WarningText = Color.FromArgb(166, 45, 45);
    public static readonly Color Success = Color.FromArgb(46, 158, 91);

    // ---------- 字体 ----------
    // 解析一次真正可用的字族，避免 "字体不存在 → 静默回退" 改变全部布局度量与字形
    // （Windows 10/11 都带 Microsoft YaHei UI / Consolas，但精简镜像或未装中日韩字体的
    //  系统上是会缺的，此时静默回退会把中文字形画成方块）。
    private static readonly string UiFamily = ResolveFamily(
        "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma");
    private static readonly string MonoFamily = ResolveFamily(
        "Consolas", "Cascadia Mono", "Lucida Console", "Courier New");

    private static string ResolveFamily(params string[] candidates)
    {
        foreach (var name in candidates)
        {
            try
            {
                using var f = new FontFamily(name);
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
            }
            catch (Exception) { /* 该字族不存在，试下一个 */ }
        }
        try { return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI"; }
        catch (Exception) { return "Segoe UI"; }
    }

    public static Font Font(float size, bool bold = false) =>
        new(UiFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);

    /// <summary>等宽日志字体。</summary>
    public static Font MonoFont(float size = 9f) => new(MonoFamily, size);

    // ---------- 窗口尺寸：按屏幕工作区裁剪 ----------

    /// <summary>
    /// 把窗体尺寸限制在**鼠标所在屏幕**的工作区内，并同步压低 <see cref="Form.MinimumSize"/>。
    ///
    /// 为什么必须做：WinForms 会让 <c>MinimumSize</c> 跟着显示器 DPI 放大（150% 时 880×520 会变成
    /// 1320×780），而小屏笔记本（1366×768）与远程桌面（1024×768）的工作区根本没这么大——
    /// 窗口会比自己能缩小到的极限还大，底部那一排按钮（全选/分析/清理/退出）永远点不到。
    /// 这里同时压低最小尺寸，保证窗口**总能**被缩进屏幕。
    /// </summary>
    public static void FitToWorkingArea(Form form, Size desiredClient, Size? minClient = null,
        int margin = 40)
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (wa.Width <= 0 || wa.Height <= 0) wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 900);

        int maxW = Math.Max(320, wa.Width - margin);
        int maxH = Math.Max(240, wa.Height - margin);

        var min = minClient ?? new Size(Math.Min(desiredClient.Width, 640), Math.Min(desiredClient.Height, 480));
        form.MinimumSize = new Size(Math.Min(min.Width, maxW), Math.Min(min.Height, maxH));
        form.ClientSize = new Size(Math.Min(desiredClient.Width, maxW), Math.Min(desiredClient.Height, maxH));
    }

    // ---------- 工厂 ----------

    public static RoundedButton MakeButton(string text, ButtonVariant variant, int width, Action onClick,
        int height = 32)
    {
        var b = new RoundedButton(text, variant, width, height);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static RoundedButton MakeSmallButton(string text, ButtonVariant variant, int width, Action onClick)
    {
        var b = new RoundedButton(text, variant, width, 27);
        b.Font = Font(8.5f);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------- 控件样式 ----------

    public static void StyleListView(ListView lv)
    {
        lv.BackColor = Card;
        lv.ForeColor = Text;
        lv.Font = Font(9f);
        lv.BorderStyle = BorderStyle.None;
        lv.FullRowSelect = true;
        lv.HideSelection = false;
        lv.MultiSelect = false;
        lv.UseCompatibleStateImageBehavior = false;
        lv.ShowItemToolTips = true;
    }

    public static void StyleLog(RichTextBox log)
    {
        log.BackColor = LogBg;
        log.ForeColor = SubText;
        log.Font = MonoFont();
        log.BorderStyle = BorderStyle.None;
        log.ReadOnly = true;
        log.WordWrap = false;
        log.DetectUrls = false;
    }

    public static void StyleProgressBar(ProgressBar pb)
    {
        pb.Height = 18;
        pb.Style = ProgressBarStyle.Continuous;
    }

    /// <summary>1px 细分隔线。</summary>
    public static Panel Separator()
    {
        return new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Border };
    }

    /// <summary>双缓冲容器（横幅/卡片用，避免子控件移动时留残影）。</summary>
    public static Panel CardPanel(DockStyle dock)
    {
        var p = new Panel
        {
            Dock = dock,
            BackColor = Card
        };
        typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(p, true);
        return p;
    }

    /// <summary>生成圆角路径。</summary>
    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int r = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
        if (r <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        int d = r * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>圆角按钮：自绘填充/描边/悬停/按下/禁用，无外部资源。</summary>
public sealed class RoundedButton : Button
{
    private ButtonVariant _variant;
    private bool _hover;
    private bool _down;

    public RoundedButton(string text, ButtonVariant variant, int width, int height)
    {
        _variant = variant;
        Text = text;
        Size = new Size(width, height);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = UiStyle.Font(9f);
        BackColor = Color.Transparent;   // 圆角外区域透明，避免露出系统按钮底纹
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { _down = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _down = false; Invalidate(); base.OnMouseUp(mevent); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiStyle.Rounded(rc, 6);

        Color fill, line, fore;
        if (!Enabled)
        {
            fill = UiStyle.DisabledBg; line = Color.Transparent; fore = UiStyle.SubText;
        }
        else
        {
            switch (_variant)
            {
                case ButtonVariant.Primary:
                    fill = _hover ? UiStyle.PrimaryHover : (_down ? UiStyle.PrimaryDark : UiStyle.Primary);
                    line = fill; fore = Color.White;
                    break;
                case ButtonVariant.Danger:
                    fill = _hover ? UiStyle.DangerHover : (_down ? UiStyle.DangerDark : UiStyle.Danger);
                    line = fill; fore = Color.White;
                    break;
                case ButtonVariant.Secondary:
                    fill = _hover ? UiStyle.HoverBg : UiStyle.Card;
                    line = _hover ? UiStyle.Primary : UiStyle.Border;
                    fore = UiStyle.Primary;
                    break;
                default: // Quiet
                    fill = _hover ? UiStyle.HoverBg : UiStyle.Card;
                    line = Color.Transparent;
                    fore = _hover ? UiStyle.Text : UiStyle.SubText;
                    break;
            }
        }

        // 先整块清除为**父容器底色**：防止系统按钮底纹/边框（阴影感）透过圆角露出。
        // 不能写死白色——放到非白色父容器（如处理中遮罩 #F6F7F9）上会露出一个白方块。
        g.Clear(Parent?.BackColor ?? UiStyle.Card);

        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        if (line != Color.Transparent)
            using (var pen = new Pen(line))
                g.DrawPath(pen, path);

        TextRenderer.DrawText(g, Text, Font, rc, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    /// <summary>运行时切换视觉变体（用于分段标签激活态）。</summary>
    public void SetVariant(ButtonVariant variant)
    {
        if (_variant == variant) return;
        _variant = variant;
        Invalidate();
    }

    public ButtonVariant Variant => _variant;
}

/// <summary>圆角胶囊标签（用于“发现 N 项”等汇总状态）。透明圆角 + 双缓冲，避免残影。</summary>
public sealed class ChipLabel : Label
{
    private readonly Color _bg;
    private readonly Color _fg;

    public ChipLabel(string text, Color bg, Color fg)
    {
        _bg = bg;
        _fg = fg;
        BackColor = Color.Transparent;
        Font = UiStyle.Font(9f);
        AutoSize = false;
        SetText(text);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
    }

    public void SetText(string text)
    {
        Text = text;
        int w = TextRenderer.MeasureText(text, Font).Width + 26;
        Width = Math.Max(w, 60);
        Height = 26;
        // 宽度变化后立即让父容器重新布局并重绘，避免右侧停靠出现残影
        Parent?.PerformLayout();
        Parent?.Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiStyle.Rounded(rc, 13);
        using var brush = new SolidBrush(_bg);
        g.FillPath(brush, path);
        TextRenderer.DrawText(g, Text, Font, rc, _fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
