using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Voicecon;

/// <summary>
/// Neumorphic palette. Background is the matte canvas; Surface is the face of raised cards and controls (the same tone,
/// so shapes read only through their shadows); Field is the slightly deeper face of inset wells and inputs.
/// </summary>
public sealed record ThemePalette(string Id, string Name, bool Dark, Color Background, Color Surface, Color Field,
    Color Border, Color Text, Color Muted, Color Accent, Color AccentInk, Color Danger, Color Glow, Color Online)
{
    /// <summary>Shadow cast to the bottom right of raised shapes.</summary>
    public Color ShadowDark => Dark ? Color.FromArgb(190, UiTheme.Blend(Background, Color.Black, 0.72f)) : Color.FromArgb(175, UiTheme.Blend(Background, Color.FromArgb(118, 130, 160), 0.85f));
    /// <summary>Soft light cast to the top left.</summary>
    public Color ShadowLight => Dark ? Color.FromArgb(60, UiTheme.Blend(Background, Color.White, 0.6f)) : Color.FromArgb(255, 255, 255, 255);
    public Color InsetDark => Dark ? Color.FromArgb(200, UiTheme.Blend(Field, Color.Black, 0.78f)) : Color.FromArgb(150, UiTheme.Blend(Field, Color.FromArgb(112, 124, 156), 0.82f));
    public Color InsetLight => Dark ? Color.FromArgb(40, UiTheme.Blend(Field, Color.White, 0.5f)) : Color.FromArgb(235, 255, 255, 255);
}

public enum MotionLevel { Full, Minimal, Off }

public static class UiTheme
{
    private static Color Hex(int value) => Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
    public static readonly ThemePalette[] All =
    [
        // Dark matte canvases, light enough for both shadows of the neumorphic pair to read.
        new("graphite", "Графит", true, Hex(0x2A2D34), Hex(0x2A2D34), Hex(0x24272D), Hex(0x3A3F4A), Hex(0xE9ECF3), Hex(0x959CAB), Hex(0x8FA7FF), Hex(0x111833), Hex(0xFF7D8C), Hex(0xB48CFF), Hex(0x5DD39E)),
        new("midnight", "Полночь", true, Hex(0x232A3A), Hex(0x232A3A), Hex(0x1E2432), Hex(0x343E55), Hex(0xE8F0FC), Hex(0x8E9BB4), Hex(0x6FD3FF), Hex(0x06202E), Hex(0xFF7F92), Hex(0x5B7CFF), Hex(0x5EE0B0)),
        new("amethyst", "Аметист", true, Hex(0x2D2738), Hex(0x2D2738), Hex(0x272130), Hex(0x403753), Hex(0xF1ECF9), Hex(0xA69BBA), Hex(0xC59BFF), Hex(0x221236), Hex(0xFF84A0), Hex(0xFF8AD8), Hex(0x6FDDB0)),
        new("sage", "Изумруд", true, Hex(0x27302B), Hex(0x27302B), Hex(0x212924), Hex(0x37443C), Hex(0xE9F3EB), Hex(0x9AAE9F), Hex(0x8FDCAA), Hex(0x0E2418), Hex(0xF09A9C), Hex(0x5FC6C1), Hex(0x7BE0A0)),
        new("gold", "Янтарь", true, Hex(0x2F2B23), Hex(0x2F2B23), Hex(0x28251E), Hex(0x463F31), Hex(0xFBF2DE), Hex(0xB5A88A), Hex(0xFFC857), Hex(0x241800), Hex(0xFF8F7A), Hex(0xFF8447), Hex(0x8BD48A)),
        new("rose", "Гранат", true, Hex(0x322930), Hex(0x322930), Hex(0x2B2329), Hex(0x4A3A45), Hex(0xF7EBF0), Hex(0xBAA2AD), Hex(0xF2A7BA), Hex(0x351A23), Hex(0xFF8C9C), Hex(0xA98BFF), Hex(0x74D6A4)),
        // The classic neumorphic light: a cool pearl grey with white light and blue-grey shadows.
        new("pearl", "Жемчуг", false, Hex(0xE3E7EE), Hex(0xE3E7EE), Hex(0xDCE1E9), Hex(0xC6CEDB), Hex(0x283043), Hex(0x6C7588), Hex(0x6E7CF2), Hex(0xFFFFFF), Hex(0xE2546C), Hex(0xB07CF0), Hex(0x29AD76))
    ];
    public static ThemePalette Current { get; private set; } = All[0];
    public static void Select(string? id) => Current = All.FirstOrDefault(t => t.Id == id) ?? All[0];
    public static void Transition(ThemePalette start, ThemePalette end, float p) => Current = new(end.Id, end.Name, p < 0.5f ? start.Dark : end.Dark,
        Blend(start.Background, end.Background, p), Blend(start.Surface, end.Surface, p), Blend(start.Field, end.Field, p),
        Blend(start.Border, end.Border, p), Blend(start.Text, end.Text, p), Blend(start.Muted, end.Muted, p), Blend(start.Accent, end.Accent, p),
        Blend(start.AccentInk, end.AccentInk, p), Blend(start.Danger, end.Danger, p), Blend(start.Glow, end.Glow, p), Blend(start.Online, end.Online, p));

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Strong = new("Segoe UI Semibold", 10f);
    public static readonly Font Heading = new("Segoe UI Semibold", 13f);
    public static readonly Font Title = new("Segoe UI Semibold", 18f);
    public static readonly Font Caption = new("Segoe UI Semibold", 8f);
    public static readonly Font Code = new("Consolas", 9f);

    public static Color Blend(Color first, Color second, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(first.R * (1 - amount) + second.R * amount),
            (int)(first.G * (1 - amount) + second.G * amount),
            (int)(first.B * (1 - amount) + second.B * amount));
    }
    public static Color WithAlpha(Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);
    public static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0.5f) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    /// <summary>Stable avatar colour for an id: hue from a hash, tuned to the theme.</summary>
    public static Color AvatarColor(string id)
    {
        var hash = 0u; foreach (var c in id) hash = hash * 31 + c;
        var hue = hash % 360 / 360.0; var t = Current;
        return FromHsl(hue, t.Dark ? 0.55 : 0.45, t.Dark ? 0.62 : 0.58);
    }
    public static Color FromHsl(double h, double s, double l)
    {
        double Channel(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return Color.FromArgb((int)(Channel(h + 1 / 3.0) * 255), (int)(Channel(h) * 255), (int)(Channel(h - 1 / 3.0) * 255));
    }
    public static string Initials(string name)
    {
        var parts = name.Split([' ', '·', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]).ToString();
        return parts.Length > 1 && char.IsLetterOrDigit(parts[1][0]) ? first + char.ToUpperInvariant(parts[1][0]) : first;
    }
}

public enum UiIcon
{
    None, Microphone, Headphones, Copy, Plus, Arrow, Sliders, Refresh, Phone, Shield, Palette,
    Gear, HangUp, Screen, Camera, Video, Group, Person, Send, Smile, Eye, Stop, Close, Expand, Collapse, Chevron, Volume, Chat, Info, Folder, Trash, Edit, PersonAdd
}

/// <summary>Crisp Windows icon glyphs (Segoe MDL2 Assets, present on Windows 10 and 11), drawn as anti-aliased paths.</summary>
public static class UiIcons
{
    private static readonly FontFamily? family = LoadFamily();
    private static readonly Dictionary<(UiIcon, int), GraphicsPath> cache = [];
    private static FontFamily? LoadFamily()
    {
        using var installed = new InstalledFontCollection();
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            if (installed.Families.Any(f => f.Name == name)) return new FontFamily(name);
        return null;
    }
    private static char Glyph(UiIcon icon) => icon switch
    {
        UiIcon.Microphone => '', UiIcon.Headphones => '', UiIcon.Copy => '', UiIcon.Plus => '',
        UiIcon.Arrow => '', UiIcon.Sliders => '', UiIcon.Refresh => '', UiIcon.Phone => '',
        UiIcon.Shield => '', UiIcon.Palette => '', UiIcon.Gear => '', UiIcon.HangUp => '',
        UiIcon.Screen => '', UiIcon.Camera => '', UiIcon.Video => '', UiIcon.Group => '',
        UiIcon.Person => '', UiIcon.Send => '', UiIcon.Smile => '', UiIcon.Eye => '',
        UiIcon.Stop => '', UiIcon.Close => '', UiIcon.Expand => '', UiIcon.Collapse => '',
        UiIcon.Chevron => '', UiIcon.Volume => '', UiIcon.Chat => '', UiIcon.Info => '',
        UiIcon.Folder => '', UiIcon.Trash => '', UiIcon.Edit => '', UiIcon.PersonAdd => '',
        _ => ' '
    };
    public static void Draw(Graphics graphics, UiIcon icon, RectangleF bounds, Color color, bool crossed = false)
    {
        if (icon == UiIcon.None || bounds.Width < 2) return;
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var size = Math.Max(4, (int)Math.Round(Math.Min(bounds.Width, bounds.Height)));
        using var brush = new SolidBrush(color);
        if (family is not null)
        {
            if (!cache.TryGetValue((icon, size), out var path))
            {
                path = new GraphicsPath();
                path.AddString(Glyph(icon).ToString(), family, 0, size * 0.86f, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                using var move = new Matrix(); move.Translate(-b.X - b.Width / 2, -b.Y - b.Height / 2); path.Transform(move);
                if (cache.Count > 400) { foreach (var old in cache.Values) old.Dispose(); cache.Clear(); }
                cache[(icon, size)] = path;
            }
            graphics.TranslateTransform(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            graphics.FillPath(brush, path);
            graphics.ResetTransform(); graphics.Restore(state); state = graphics.Save();
        }
        else
        {
            using var pen = new Pen(color, Math.Max(1.4f, size / 14f));
            graphics.DrawEllipse(pen, bounds.X + size * 0.25f, bounds.Y + size * 0.25f, size * 0.5f, size * 0.5f);
        }
        if (crossed)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bg = UiTheme.Current.Dark ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(220, 255, 255, 255);
            using var gap = new Pen(bg, Math.Max(2.5f, size / 6.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var line = new Pen(color, Math.Max(1.6f, size / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var a = new PointF(bounds.X + bounds.Width * 0.14f, bounds.Y + bounds.Height * 0.14f); var z = new PointF(bounds.Right - bounds.Width * 0.14f, bounds.Bottom - bounds.Height * 0.14f);
            graphics.DrawLine(gap, a, z); graphics.DrawLine(line, a, z);
        }
        graphics.Restore(state);
    }
}
