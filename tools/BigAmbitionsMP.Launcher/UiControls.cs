using System.Drawing.Drawing2D;

namespace BigAmbitionsMP.Launcher;

internal sealed class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 12;
    public Color BorderColor { get; set; } = Color.FromArgb(46, 66, 92);

    public RoundedPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var clear = new SolidBrush(Parent?.BackColor ?? SystemColors.Control);
        e.Graphics.FillRectangle(clear, ClientRectangle);

        using var path = CreateRoundRect(ClientRectangle, CornerRadius);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(BorderColor);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Invalidate();
        Parent?.Invalidate(Bounds, invalidateChildren: false);
    }

    internal static GraphicsPath CreateRoundRect(Rectangle bounds, int radius)
    {
        int size = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        var rect = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        path.AddArc(rect.X, rect.Y, size, size, 180, 90);
        path.AddArc(rect.Right - size, rect.Y, size, size, 270, 90);
        path.AddArc(rect.Right - size, rect.Bottom - size, size, size, 0, 90);
        path.AddArc(rect.X, rect.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 8;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = Enabled
            ? (ClientRectangle.Contains(PointToClient(Cursor.Position)) ? Color.FromArgb(75, 111, 156) : BackColor)
            : Color.FromArgb(48, 58, 72);
        var fg = Enabled ? ForeColor : Color.FromArgb(150, 160, 174);

        using var path = RoundedPanel.CreateRoundRect(ClientRectangle, CornerRadius);
        using var fill = new SolidBrush(bg);
        using var border = new Pen(Color.FromArgb(105, 135, 170));
        using var textBrush = new SolidBrush(fg);

        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class RoundedProgressBar : Control
{
    private int _value;

    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 100);
            Invalidate();
        }
    }

    public RoundedProgressBar()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        Height = 16;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var clear = new SolidBrush(Parent?.BackColor ?? SystemColors.Control);
        e.Graphics.FillRectangle(clear, ClientRectangle);

        using var bgPath = RoundedPanel.CreateRoundRect(ClientRectangle, 8);
        using var bg = new SolidBrush(Color.FromArgb(8, 14, 24));
        using var border = new Pen(Color.FromArgb(55, 76, 102));
        e.Graphics.FillPath(bg, bgPath);
        e.Graphics.DrawPath(border, bgPath);

        int fillWidth = Math.Max(0, (Width * Value) / 100);
        if (fillWidth <= 0) return;

        var fillRect = new Rectangle(0, 0, fillWidth, Height);
        using var fillPath = RoundedPanel.CreateRoundRect(fillRect, 8);
        using var fill = new LinearGradientBrush(fillRect, Color.FromArgb(72, 177, 106), Color.FromArgb(85, 134, 205), LinearGradientMode.Horizontal);
        e.Graphics.FillPath(fill, fillPath);
    }
}

internal sealed class AlertIconButton : Button
{
    public AlertIconButton()
    {
        Width = 42;
        Height = 42;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);

        var triangle = new[]
        {
            new PointF(Width / 2f, 5f),
            new PointF(Width - 5f, Height - 6f),
            new PointF(5f, Height - 6f),
        };

        using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        e.Graphics.TranslateTransform(0, 2);
        e.Graphics.FillPolygon(shadow, triangle);
        e.Graphics.TranslateTransform(0, -2);

        using var fill = new SolidBrush(Enabled ? Color.FromArgb(248, 205, 58) : Color.FromArgb(110, 105, 88));
        using var border = new Pen(Color.FromArgb(70, 54, 18), 2f);
        e.Graphics.FillPolygon(fill, triangle);
        e.Graphics.DrawPolygon(border, triangle);

        using var mark = new SolidBrush(Enabled ? Color.FromArgb(207, 34, 46) : Color.FromArgb(112, 86, 90));
        using var font = new Font(Font.FontFamily, 20, FontStyle.Bold);
        TextRenderer.DrawText(
            e.Graphics,
            "!",
            font,
            new Rectangle(0, 8, Width, Height - 10),
            Color.FromArgb(207, 34, 46),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class ImageIconButton : Button
{
    public Image? IconImage { get; set; }
    public int CornerRadius { get; set; } = 10;

    public ImageIconButton()
    {
        Width = 48;
        Height = 48;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Parent is MainForm mainForm)
        {
            mainForm.PaintBackgroundSection(e.Graphics, ClientRectangle, Bounds);
        }
        else
        {
            using var clear = new SolidBrush(Parent?.BackColor ?? SystemColors.Control);
            e.Graphics.FillRectangle(clear, ClientRectangle);
        }

        if (IconImage == null)
        {
            TextRenderer.DrawText(
                e.Graphics,
                "!",
                new Font(Font.FontFamily, 18, FontStyle.Bold),
                ClientRectangle,
                Color.FromArgb(248, 205, 58),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var rect = new Rectangle(4, 4, Width - 8, Height - 8);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(IconImage, rect);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }
}
