using System.Drawing.Drawing2D;

namespace Voicecon;

public sealed record ThemePalette(string Id, string Name, Color Background, Color Surface, Color Field,
    Color Border, Color Text, Color Muted, Color Accent, Color AccentInk, Color Danger, bool Ornate = false);

public static class UiTheme
{
    private static Color Hex(int value) => Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
    public static readonly ThemePalette[] All =
    [
        new("graphite", "Графит", Hex(0x11151D), Hex(0x1A202B), Hex(0x131923), Hex(0x2C3442), Hex(0xF0F3FA), Hex(0x9CA9BD), Hex(0xB0C2FF), Hex(0x17213B), Hex(0xF1A3AD)),
        new("gold", "Чёрное золото", Hex(0x0B0A08), Hex(0x18150F), Hex(0x100E0A), Hex(0x755B27), Hex(0xFFF3D4), Hex(0xBAAA83), Hex(0xFFD15C), Hex(0x251900), Hex(0xF0A38B), true),
        new("sage", "Чёрный · шалфей", Hex(0x111814), Hex(0x1B261F), Hex(0x141D18), Hex(0x33483A), Hex(0xEDF5EF), Hex(0xA0B5A6), Hex(0xB3D8BA), Hex(0x192D20), Hex(0xE7A6A7)),
        new("rose", "Чёрный · гранат", Hex(0x1A1216), Hex(0x281D23), Hex(0x1D141A), Hex(0x4B343F), Hex(0xF8EDF1), Hex(0xBCA4AE), Hex(0xEDAFBA), Hex(0x391C25), Hex(0xF2A4AE)),
        new("porcelain", "Розовый фарфор", Hex(0xF8F2F4), Hex(0xFFFCFD), Hex(0xF5EBEF), Hex(0xE5D4DC), Hex(0x49323F), Hex(0x7E6371), Hex(0xA55B7E), Hex(0xFFFFFF), Hex(0xB95E73))
    ];
    public static ThemePalette Current { get; private set; } = All[0];
    public static void Select(string? id) => Current = All.FirstOrDefault(t => t.Id == id) ?? All[0];
    public static void Transition(ThemePalette start, ThemePalette end, float p) => Current = new(end.Id, end.Name,
        Blend(start.Background, end.Background, p), Blend(start.Surface, end.Surface, p), Blend(start.Field, end.Field, p),
        Blend(start.Border, end.Border, p), Blend(start.Text, end.Text, p), Blend(start.Muted, end.Muted, p), Blend(start.Accent, end.Accent, p),
        Blend(start.AccentInk, end.AccentInk, p), Blend(start.Danger, end.Danger, p), p < 0.5f ? start.Ornate : end.Ornate);
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Strong = new("Segoe UI Semibold", 10f);
    public static readonly Font Title = new("Segoe UI Semibold", 22f);
    public static readonly Font Code = new("Consolas", 9f);
    public static Color Blend(Color first, Color second, float amount) => Color.FromArgb(
        (int)(first.R * (1 - amount) + second.R * amount),
        (int)(first.G * (1 - amount) + second.G * amount),
        (int)(first.B * (1 - amount) + second.B * amount));
    public static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
}

public enum UiIcon { None, Microphone, Headphones, Copy, Plus, Arrow, Sliders, Refresh, Phone, Shield, Palette }

public static class UiIcons
{
    public static void Draw(Graphics graphics, UiIcon icon, RectangleF bounds, Color color, bool crossed = false)
    {
        var old = graphics.Save(); graphics.TranslateTransform(bounds.X, bounds.Y); graphics.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (icon)
        {
            case UiIcon.Microphone:
                using (var capsule = UiTheme.Rounded(new RectangleF(8, 3, 8, 12), 4)) graphics.DrawPath(pen, capsule);
                graphics.DrawArc(pen, 5, 7, 14, 12, 0, 180); graphics.DrawLine(pen, 12, 19, 12, 22); graphics.DrawLine(pen, 8, 22, 16, 22); break;
            case UiIcon.Headphones:
                graphics.DrawArc(pen, 4, 4, 16, 17, 180, 180); graphics.DrawLine(pen, 4, 12, 4, 19); graphics.DrawLine(pen, 20, 12, 20, 19);
                using (var left = UiTheme.Rounded(new RectangleF(3, 12, 5, 8), 2)) graphics.DrawPath(pen, left);
                using (var right = UiTheme.Rounded(new RectangleF(16, 12, 5, 8), 2)) graphics.DrawPath(pen, right); break;
            case UiIcon.Copy:
                using (var back = UiTheme.Rounded(new RectangleF(4, 3, 12, 14), 2)) graphics.DrawPath(pen, back);
                using (var front = UiTheme.Rounded(new RectangleF(8, 7, 12, 14), 2)) { using var fill = new SolidBrush(UiTheme.Current.Field); graphics.FillPath(fill, front); graphics.DrawPath(pen, front); } break;
            case UiIcon.Plus: graphics.DrawLine(pen, 12, 5, 12, 19); graphics.DrawLine(pen, 5, 12, 19, 12); break;
            case UiIcon.Arrow: graphics.DrawLine(pen, 5, 12, 19, 12); graphics.DrawLines(pen, [new(13, 6), new(19, 12), new(13, 18)]); break;
            case UiIcon.Sliders:
                graphics.DrawLine(pen, 4, 7, 20, 7); graphics.DrawLine(pen, 4, 17, 20, 17);
                graphics.DrawEllipse(pen, 7, 4, 6, 6); graphics.DrawEllipse(pen, 13, 14, 6, 6); break;
            case UiIcon.Refresh:
                graphics.DrawArc(pen, 5, 5, 14, 14, 35, 290); graphics.DrawLines(pen, [new(20, 4), new(20, 10), new(14, 10)]); break;
            case UiIcon.Phone:
                graphics.DrawArc(pen, 3, 9, 18, 12, 210, 120); graphics.DrawLines(pen, [new(4, 12), new(4, 17), new(8, 17), new(8, 13)]);
                graphics.DrawLines(pen, [new(16, 13), new(16, 17), new(20, 17), new(20, 12)]); break;
            case UiIcon.Shield:
                graphics.DrawLines(pen, [new(12, 3), new(20, 6), new(19, 15), new(16, 19), new(12, 22), new(8, 19), new(5, 15), new(4, 6), new(12, 3)]);
                graphics.DrawLines(pen, [new(8, 12), new(11, 15), new(16, 9)]); break;
            case UiIcon.Palette:
                graphics.DrawEllipse(pen, 4, 4, 16, 16); graphics.DrawEllipse(pen, 8, 7, 1, 1); graphics.DrawEllipse(pen, 14, 8, 1, 1); graphics.DrawEllipse(pen, 8, 13, 1, 1); break;
        }
        if (crossed) graphics.DrawLine(pen, 3, 3, 21, 21);
        graphics.Restore(old);
    }
}
