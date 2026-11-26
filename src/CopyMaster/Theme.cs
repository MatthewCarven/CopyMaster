using Microsoft.Win32;

namespace CopyMaster;

/// <summary>
/// Colours for the parts WinForms' dark mode doesn't cover (cards, our own progress bar,
/// status text). Follows Windows' app mode, as <c>Application.SetColorMode(System)</c> does.
/// </summary>
internal static class Theme
{
    public static bool Dark => Application.IsDarkModeEnabled;

    public static Color Window => Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    public static Color Card => Dark ? Color.FromArgb(43, 43, 43) : Color.White;
    public static Color CardBorder => Dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(225, 225, 225);
    public static Color Text => Dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(26, 26, 26);
    public static Color Dim => Dark ? Color.FromArgb(166, 166, 166) : Color.FromArgb(96, 96, 96);
    public static Color Track => Dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(218, 218, 218);
    public static Color Good => Dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15);
    public static Color Warning => Dark ? Color.FromArgb(252, 225, 0) : Color.FromArgb(157, 93, 0);
    public static Color Bad => Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);

    /// <summary>
    /// The Windows accent colour, in the shade Windows itself uses for controls: lighter on
    /// dark, darker on light (AccentPalette entries 1 and 4).
    /// </summary>
    public static Color Accent => Dark ? AccentShades.Value.OnDark : AccentShades.Value.OnLight;

    private static readonly Lazy<(Color OnDark, Color OnLight)> AccentShades = new(ReadAccent);

    private static (Color, Color) ReadAccent()
    {
        var onDark = Color.FromArgb(76, 194, 255);  // Windows 11's default blue, light2
        var onLight = Color.FromArgb(0, 95, 184);   // and dark1
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
            {
                onDark = Color.FromArgb(palette[4], palette[5], palette[6]);
                onLight = Color.FromArgb(palette[16], palette[17], palette[18]);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The defaults will do.
        }
        return (onDark, onLight);
    }

    public static Font Bold(Font font) => new(font, FontStyle.Bold);
}

/// <summary>One line of text, cut short with an ellipsis (at the end, or in the middle of a path).</summary>
internal sealed class TextLine : Control
{
    private bool _pathEllipsis;

    public TextLine()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    public bool PathEllipsis
    {
        get => _pathEllipsis;
        set { _pathEllipsis = value; Invalidate(); }
    }

    public HorizontalAlignment Alignment { get; set; } = HorizontalAlignment.Left;

    /// <summary>
    /// Sets the text and colour, repainting only if they changed. A path is shortened in
    /// the middle (C:\Users\...\file.txt), anything else at the end.
    /// </summary>
    public void Show(string text, Color color, bool path = false)
    {
        if (text == Text && color == ForeColor && path == _pathEllipsis) return;
        Text = text;
        ForeColor = color;
        _pathEllipsis = path;
        Invalidate();
    }

    public int LineHeight => TextRenderer.MeasureText("Ag", Font).Height;

    public int TextWidth => TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                    TextFormatFlags.VerticalCenter |
                    (_pathEllipsis ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis) |
                    Alignment switch
                    {
                        HorizontalAlignment.Right => TextFormatFlags.Right,
                        HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                        _ => TextFormatFlags.Left,
                    };
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, BackColor, flags);
    }
}

/// <summary>A thin progress bar in the accent colour (WinForms' own stays light in dark mode).</summary>
internal sealed class ProgressStrip : Control
{
    private double _value;
    private Color _fill = Theme.Accent;

    public ProgressStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    /// <summary>Sets how far along (0 to 1) and the colour, repainting only if they changed.</summary>
    public void Show(double value, Color fill)
    {
        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (Math.Abs(value - _value) < 0.0005 && fill == _fill) return;
        _value = value;
        _fill = fill;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var radius = new Size(Height / 2, Height / 2);
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var brush = new SolidBrush(Theme.Track)) g.FillRoundedRectangle(brush, track, radius);
        int filled = (int)Math.Round(track.Width * _value);
        if (filled <= 0) return;
        using var fill = new SolidBrush(_fill);
        g.FillRoundedRectangle(fill, track with { Width = Math.Max(filled, Height) }, radius);
    }
}
