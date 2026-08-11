using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Zvt2SumUp.Desktop;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(7, 10, 15), Sidebar = Color.FromArgb(11, 15, 22),
        Surface = Color.FromArgb(16, 22, 32), Raised = Color.FromArgb(23, 31, 44), Tint = Color.FromArgb(29, 39, 54),
        Border = Color.FromArgb(40, 54, 73), Ink = Color.FromArgb(240, 246, 252), Muted = Color.FromArgb(143, 159, 180),
        Blue = Color.FromArgb(8, 123, 234), BlueBright = Color.FromArgb(51, 163, 255), Green = Color.FromArgb(54, 211, 153),
        Amber = Color.FromArgb(255, 189, 46), Danger = Color.FromArgb(255, 95, 86), Dark = Color.FromArgb(5, 8, 13), DarkSurface = Color.FromArgb(13, 18, 27);
    public static readonly Font Body = new("Segoe UI", 9F), Mono = new("Consolas", 9F), HeadingFont = new("Bahnschrift SemiBold", 14F, FontStyle.Bold),
        Wordmark = new("Bahnschrift SemiBold", 18F, FontStyle.Bold), Tab = new("Bahnschrift SemiBold", 9F, FontStyle.Bold),
        TabCompact = new("Bahnschrift SemiBold", 6.75F, FontStyle.Bold);
    public static Button Button(string text, int width = 130, bool primary = false)
    {
        Button button = new()
        {
            Text = text,
            Width = width,
            Height = 35,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Blue : Raised,
            ForeColor = Ink,
            Font = primary ? new Font(Body, FontStyle.Bold) : Body,
            Cursor = Cursors.Hand,
            Margin = new Padding(3)
        };
        button.FlatAppearance.BorderColor = primary ? Blue : Border; button.FlatAppearance.MouseOverBackColor = primary ? BlueBright : Tint; return button;
    }
    public static Label Heading(string text, float size = 16) => new() { Text = text, AutoSize = true, ForeColor = Ink, Font = new Font("Bahnschrift SemiBold", size, FontStyle.Bold) };
    public static void Input(Control control) { control.BackColor = DarkSurface; control.ForeColor = Ink; if (control is TextBoxBase box) box.BorderStyle = BorderStyle.FixedSingle; if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat; }
    public static void DarkTitleBar(Form form)
    {
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, 4) != 0 &&
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, 4) != 0)
                return;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

internal sealed class BridgeLogo : Control
{
    public BridgeLogo()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(64, 64); BackColor = Color.Transparent;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); DrawLogo(e.Graphics, ClientRectangle);
    }

    public static void DrawLogo(Graphics graphics, Rectangle bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Min(bounds.Width, bounds.Height) / 64F; GraphicsState state = graphics.Save();
        graphics.TranslateTransform(bounds.Left + (bounds.Width - 64F * scale) / 2F, bounds.Top + (bounds.Height - 64F * scale) / 2F); graphics.ScaleTransform(scale, scale);
        using (GraphicsPath background = GraphicsExtensions.RoundedRectangle(new RectangleF(0, 0, 64, 64), 17F))
        using (SolidBrush dark = new(Theme.Dark)) graphics.FillPath(dark, background);
        PointF[] bridge = [new(9, 43), new(20, 27), new(44, 27), new(55, 43)];
        using (LinearGradientBrush gradient = new(new PointF(8, 6), new PointF(56, 58), Theme.BlueBright, Color.FromArgb(22, 94, 232)))
        using (Pen blue = new(gradient, 5F) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) graphics.DrawLines(blue, bridge);
        using (Pen light = new(Color.FromArgb(135, 199, 255), 3F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.DrawLine(light, new PointF(9, 43), new PointF(55, 43));
            graphics.DrawLine(light, new PointF(15, 35), new PointF(15, 47)); graphics.DrawLine(light, new PointF(49, 35), new PointF(49, 47));
        }
        using (SolidBrush green = new(Theme.Green)) graphics.FillEllipse(green, 51, 39, 7, 7); graphics.Restore(state);
    }
}

internal sealed class WordmarkControl : Control
{
    public WordmarkControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; Size = new Size(205, 52);
    }
    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (SolidBrush text = new(Theme.Ink)) eventArgs.Graphics.DrawString("ZVT2SumUp", Theme.Wordmark, text, new PointF(0, 1), StringFormat.GenericTypographic);
        Rectangle line = new(2, Height - 8, Math.Min(154, Width - 4), 3);
        using LinearGradientBrush gradient = new(line, Theme.Blue, Theme.Green, LinearGradientMode.Horizontal); eventArgs.Graphics.FillRectangle(gradient, line);
    }
}

internal sealed class BrandTabControl : TabControl
{
    public BrandTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.Tab; ItemSize = new Size(0, 38); SizeMode = TabSizeMode.Normal; Multiline = false; Padding = new Point(5, 6);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Theme.Background);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Background);
        Rectangle display = DisplayRectangle;
        using Pen divider = new(Theme.Border); e.Graphics.DrawLine(divider, 0, Math.Max(0, display.Top - 1), Width, Math.Max(0, display.Top - 1));
        for (int index = 0; index < TabCount; index++)
        {
            Rectangle bounds = GetTabRect(index); bool selected = index == SelectedIndex;
            using SolidBrush background = new(selected ? Theme.Surface : Theme.Sidebar); e.Graphics.FillRectangle(background, bounds);
            if (selected) { using SolidBrush accent = new(Theme.Blue); e.Graphics.FillRectangle(accent, bounds.Left, bounds.Bottom - 3, bounds.Width, 3); }
            TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds, selected ? Theme.Ink : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        using Pen border = new(Theme.Border); e.Graphics.DrawRectangle(border,
            display.Left - 1, display.Top - 1, Math.Max(0, display.Width + 1), Math.Max(0, display.Height + 1));
    }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnSizeChanged(EventArgs e)
    {
        bool compact = Width < 760;
        Font = compact ? Theme.TabCompact : Theme.Tab;
        Padding = compact ? new Point(1, 6) : new Point(5, 6);
        base.OnSizeChanged(e);
        Invalidate();
    }
}

internal sealed class ResponsiveCardPanel : FlowLayoutPanel
{
    private bool resizingCards;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!resizingCards)
        {
            resizingCards = true;
            try
            {
                int available = Math.Max(220, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
                int columns = available >= 780 ? 3 : available >= 500 ? 2 : 1;
                int width = Math.Max(210, available / columns - 16);
                foreach (Control card in Controls)
                    if (card.Width != width) card.Width = width;
            }
            finally { resizingCards = false; }
        }
        base.OnLayout(levent);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rect, float radius)
    {
        using GraphicsPath path = RoundedRectangle(rect, radius); graphics.FillPath(brush, path);
    }
    public static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    { GraphicsPath path = new(); float d = radius * 2; path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90); path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path; }
}
