using System.Drawing.Drawing2D;

namespace CleanC;

/// <summary>
/// 腾讯公益捐赠弹窗：启动时自动展示“小红花”二维码海报，
/// 按任意键、点击任意位置或点击 ✕ 即可关闭。
/// 无边框、双缓冲、全部 GDI+ 自绘；配色取自海报（暖米底 / 珊瑚红 / 粉色 / 暖金）。
/// </summary>
public sealed class DonateForm : Form
{
    // ---------- 与海报呼应的暖色板 ----------
    private static readonly Color BgTop = Color.FromArgb(253, 246, 236);         // 暖米
    private static readonly Color BgBottom = Color.FromArgb(248, 222, 203);      // 浅杏
    private static readonly Color CardBg = Color.FromArgb(255, 253, 249);        // 卡面
    private static readonly Color CardBorder = Color.FromArgb(240, 218, 197);    // 卡描边
    private static readonly Color CardShadow = Color.FromArgb(56, 178, 130, 90); // 淡影
    private static readonly Color FlowerRed = Color.FromArgb(232, 80, 58);       // 珊瑚红
    private static readonly Color FlowerRedDark = Color.FromArgb(199, 60, 40);
    private static readonly Color Pink = Color.FromArgb(247, 168, 154);          // 浅粉
    private static readonly Color Gold = Color.FromArgb(245, 199, 96);           // 暖金
    private static readonly Color GoldDark = Color.FromArgb(222, 168, 64);
    private static readonly Color TitleColor = Color.FromArgb(138, 70, 52);      // 暖棕
    private static readonly Color SubColor = Color.FromArgb(167, 120, 89);
    private static readonly Color HintColor = Color.FromArgb(176, 139, 114);
    private static readonly Color CloseFill = Color.FromArgb(243, 225, 208);
    private static readonly Color CloseFillHover = Color.FromArgb(239, 205, 180);
    private static readonly Color CloseGlyph = Color.FromArgb(176, 128, 96);

    private const string ResourceName = "CleanC.Resources.xiaohonghua.jpg";

    private readonly Image _qr;
    private readonly Font _fontTitle = UiStyle.Font(16.5f, bold: true);
    private readonly Font _fontSub = UiStyle.Font(10f);
    private readonly Font _fontHint = UiStyle.Font(9.5f);
    private Rectangle _closeRect;
    private bool _closeHover;

    public DonateForm(Image qrImage)
    {
        _qr = qrImage;
        Text = "腾讯公益 · 爱心捐赠";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        // 无边框窗口没法拖动也没法调整大小：尺寸必须自己限制在屏幕工作区内，
        // 否则 1366×768 这类小屏（尤其 125%/150% 缩放）上会超出屏幕且关不掉。
        UiStyle.FitToWorkingArea(this, new Size(560, 700), new Size(420, 420));
        BackColor = BgTop;
        KeyPreview = true;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        KeyDown += (_, _) => Close();               // 任意键关闭（KeyPreview 保证先到）
        MouseClick += (_, _) => Close();            // 点击任意位置关闭（含 ✕）
        MouseMove += OnHover;
    }

    /// <summary>
    /// 加载二维码图片：优先取嵌入程序集的资源，回退到 exe 旁/当前目录的 image\xiaohonghua.jpg。
    /// 完全失败返回 null（调用方跳过弹窗，不影响主流程）。
    /// </summary>
    public static Image? TryLoadImage()
    {
        try
        {
            var asm = typeof(DonateForm).Assembly;
            using (var s = asm.GetManifestResourceStream(ResourceName))
            {
                if (s != null)
                {
                    using var tmp = Image.FromStream(s);
                    return new Bitmap(tmp);   // 复制像素并关闭流，位图可独立存活
                }
            }

            foreach (var path in new[]
            {
                // 只用 exe 所在目录：不再回退到 Environment.CurrentDirectory——
                // 快捷方式的「起始位置」可以指向任意目录，那会显示一张无关的、甚至用户可写的图片。
                Path.Combine(AppContext.BaseDirectory, "image", "xiaohonghua.jpg"),
            })
            {
                if (File.Exists(path))
                {
                    using var fs = File.OpenRead(path);
                    using var tmp = Image.FromStream(fs);
                    return new Bitmap(tmp);
                }
            }
        }
        catch
        {
            // 资源缺失/损坏：按“未找到”处理，仅跳过本次弹窗
        }
        return null;
    }

    private void OnHover(object? sender, MouseEventArgs e)
    {
        bool now = _closeRect.Contains(e.Location);
        if (now != _closeHover)
        {
            _closeHover = now;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int w = ClientSize.Width, h = ClientSize.Height;
        _closeRect = new Rectangle(w - 58, 24, 36, 36);

        // ---- 背景渐变 ----
        using (var bg = new LinearGradientBrush(ClientRectangle, BgTop, BgBottom, LinearGradientMode.Vertical))
            g.FillRectangle(bg, ClientRectangle);

        // ---- 装饰 ----
        DrawFlower(g, new PointF(56, 2), 46);                     // 左上大红花（顶部自然裁切）
        DrawSparkle(g, new PointF(w - 96f, 30), 9, Gold);         // 右上小金芒
        DrawHeart(g, new PointF(w - 30f, h - 26f), 34, Pink);     // 右下粉心
        DrawFlower(g, new PointF(28, h - 22), 24);                // 左下小红花

        // ---- 标题 / 副题 ----
        const TextFormatFlags center = TextFormatFlags.HorizontalCenter |
                                       TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, "种一朵小红花，守护病患儿童", _fontTitle,
            new Rectangle(0, 34, w, 44), TitleColor, center);
        TextRenderer.DrawText(g, "腾讯公益 · 微信扫码，为病患儿童献一份爱心", _fontSub,
            new Rectangle(0, 80, w, 32), SubColor, center);

        // ---- 白色圆角卡片（含淡投影） ----
        var card = new Rectangle(60, 130, w - 120, 420);
        using (var shadow = new Pen(CardShadow, 10f))
            g.DrawPath(shadow, UiStyle.Rounded(new Rectangle(card.X + 4, card.Y + 9, card.Width, card.Height), 26));
        using (var cardPath = UiStyle.Rounded(card, 26))
        {
            using var fill = new SolidBrush(CardBg);
            g.FillPath(fill, cardPath);
            using var border = new Pen(CardBorder, 1.4f);
            g.DrawPath(border, cardPath);
        }

        // ---- 二维码（等比缩放居中，四周留白如海报卡面） ----
        if (_qr != null)
        {
            const int pad = 16;
            var area = new Rectangle(card.X + pad, card.Y + pad, card.Width - pad * 2, card.Height - pad * 2);
            float scale = MathF.Min(area.Width / (float)_qr.Width, area.Height / (float)_qr.Height);
            int dw = (int)MathF.Round(_qr.Width * scale);
            int dh = (int)MathF.Round(_qr.Height * scale);
            int dx = area.X + (area.Width - dw) / 2;
            int dy = area.Y + (area.Height - dh) / 2;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_qr, new Rectangle(dx, dy, dw, dh));
            g.InterpolationMode = InterpolationMode.Default;
        }
        else
        {
            TextRenderer.DrawText(g, "二维码图片加载失败", _fontSub, card, SubColor, center);
        }

        // ---- 花・心・花 ----
        int mid = w / 2;
        DrawFlower(g, new PointF(mid - 72f, h - 112f), 14);
        DrawHeart(g, new PointF(mid, h - 112f), 18, Pink);
        DrawFlower(g, new PointF(mid + 72f, h - 112f), 14);

        // ---- 底部提示 ----
        TextRenderer.DrawText(g, "按下任意键 / 点击任意位置 即可关闭", _fontHint,
            new Rectangle(0, h - 86, w, 34), HintColor, center);

        // ---- 右上角 ✕ ----
        using (var fill = new SolidBrush(_closeHover ? CloseFillHover : CloseFill))
            g.FillEllipse(fill, _closeRect);
        using (var pen = new Pen(CloseGlyph, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = _closeRect.Left + _closeRect.Width / 2f;
            float cy = _closeRect.Top + _closeRect.Height / 2f;
            float d = 9f;
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
        }
    }

    // ---------- GDI+ 小装饰 ----------

    /// <summary>五瓣小红花：椭圆花瓣 + 金色花心。</summary>
    private static void DrawFlower(Graphics g, PointF c, float r)
    {
        var state = g.Save();
        for (int i = 0; i < 5; i++)
        {
            float ang = (float)(i * (2 * Math.PI / 5) - Math.PI / 2);
            g.TranslateTransform(c.X + MathF.Cos(ang) * r * 0.52f,
                                 c.Y + MathF.Sin(ang) * r * 0.52f);
            g.RotateTransform(ang * 180f / MathF.PI + 90f);
            var size = new SizeF(r * 0.88f, r * 0.7f);
            using (var petal = new SolidBrush(FlowerRed))
                g.FillEllipse(petal, -size.Width / 2, -size.Height / 2, size.Width, size.Height);
            using (var pen = new Pen(FlowerRedDark, MathF.Max(1.2f, r * 0.04f)))
                g.DrawEllipse(pen, -size.Width / 2, -size.Height / 2, size.Width, size.Height);
            g.ResetTransform();
        }
        using (var center = new SolidBrush(Gold))
            g.FillEllipse(center, c.X - r * 0.26f, c.Y - r * 0.26f, r * 0.52f, r * 0.52f);
        using (var ring = new Pen(GoldDark, MathF.Max(1.2f, r * 0.04f)))
            g.DrawEllipse(ring, c.X - r * 0.26f, c.Y - r * 0.26f, r * 0.52f, r * 0.52f);
        g.Restore(state);
    }

    /// <summary>心形曲线采样点（经典心形参数方程），size 为横向半宽。</summary>
    private static PointF[] HeartPoints(PointF c, float size)
    {
        var pts = new PointF[64];
        for (int i = 0; i < pts.Length; i++)
        {
            float t = (float)(i * 2 * Math.PI / pts.Length);
            float x = 16f * MathF.Pow(MathF.Sin(t), 3);
            float y = 13f * MathF.Cos(t) - 5f * MathF.Cos(2 * t) - 2f * MathF.Cos(3 * t) - MathF.Cos(4 * t);
            pts[i] = new PointF(c.X + x * size / 16f, c.Y - y * size / 16f);
        }
        return pts;
    }

    private static void DrawHeart(Graphics g, PointF c, float size, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, HeartPoints(c, size));
    }

    /// <summary>四芒星光点（菱形挖角星形）。</summary>
    private static void DrawSparkle(Graphics g, PointF c, float r, Color color)
    {
        var pts = new PointF[8];
        for (int i = 0; i < 8; i++)
        {
            float ang = (float)(i * Math.PI / 4 - Math.PI / 2);
            float rr = (i % 2 == 0) ? r : r * 0.3f;
            pts[i] = new PointF(c.X + MathF.Cos(ang) * rr, c.Y + MathF.Sin(ang) * rr);
        }
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontTitle.Dispose();
            _fontSub.Dispose();
            _fontHint.Dispose();
            _qr.Dispose();
        }
        base.Dispose(disposing);
    }
}
