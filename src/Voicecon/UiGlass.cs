using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace Voicecon;

/// <summary>A container whose interior is tinted glass (or an opaque field). Children paint the same material behind them.</summary>
public interface IGlassLayer
{
    Color LayerTint { get; }
    /// <summary>True for glass: what is behind it is shown blurred and a little brighter (frosted), like Apple's materials.</summary>
    bool Frosted => false;
}

/// <summary>
/// The window "wallpaper": a deep base with soft pools of colour. Two versions are cached per form size and theme:
/// the clear one, and a frosted one (much blurrier, slightly brighter and more saturated) that glass shows.
/// Both are rendered at low resolution and upscaled, so they cost nothing while idle.
/// </summary>
public static class Backdrop
{
    private sealed class Entry { public Bitmap? Clear, Frosted; public string Key = ""; }
    private static readonly ConditionalWeakTable<Form, Entry> cache = new();

    public static void Paint(Graphics graphics, Control control, Rectangle area, bool? frosted = null)
    {
        var t = UiTheme.Current;
        var layers = new List<IGlassLayer>(4);
        Form? form = null; var x = 0; var y = 0;
        for (var current = control; current is not null; current = current.Parent)
        {
            if (current is Form f) { form = f; break; }
            if (current != control && current is IGlassLayer layer) layers.Add(layer);
            x += current.Left; y += current.Top;
        }
        var opaque = layers.FindIndex(l => l.LayerTint.A == 255);
        if (opaque >= 0)
        {
            using var fill = new SolidBrush(layers[opaque].LayerTint); graphics.FillRectangle(fill, area);
            for (var i = opaque - 1; i >= 0; i--) { using var tint = new SolidBrush(layers[i].LayerTint); graphics.FillRectangle(tint, area); }
            return;
        }
        using (var bg = new SolidBrush(t.Background)) graphics.FillRectangle(bg, area);
        if (form is not null && form.ClientSize.Width > 1 && form.ClientSize.Height > 1)
        {
            var useFrosted = frosted ?? layers.Any(l => l.Frosted);
            var image = For(form, useFrosted);
            var source = new Rectangle(area.X + x, area.Y + y, area.Width, area.Height);
            source.Intersect(new Rectangle(Point.Empty, image.Size));
            if (source.Width > 0 && source.Height > 0)
            {
                var target = new Rectangle(source.X - x, source.Y - y, source.Width, source.Height);
                var oldInterpolation = graphics.InterpolationMode;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.DrawImage(image, target, source, GraphicsUnit.Pixel);
                graphics.InterpolationMode = oldInterpolation;
            }
        }
        for (var i = layers.Count - 1; i >= 0; i--) { using var tint = new SolidBrush(layers[i].LayerTint); graphics.FillRectangle(tint, area); }
    }

    /// <summary>Colour approximately behind a control, for native children that need an opaque BackColor.</summary>
    public static Color Approximate(Control control)
    {
        var t = UiTheme.Current; var color = UiTheme.Blend(t.Background, t.Accent, 0.06f);
        var layers = new List<Color>();
        for (var current = control; current is not null and not Form; current = current.Parent) if (current is IGlassLayer layer) layers.Add(layer.LayerTint);
        for (var i = layers.Count - 1; i >= 0; i--) color = UiTheme.Blend(color, layers[i], layers[i].A / 255f);
        return color;
    }

    public static void Invalidate(Form form) { if (cache.TryGetValue(form, out var entry)) { entry.Clear?.Dispose(); entry.Frosted?.Dispose(); entry.Clear = entry.Frosted = null; entry.Key = ""; } }

    private static Bitmap For(Form form, bool frosted)
    {
        var entry = cache.GetOrCreateValue(form); var t = UiTheme.Current; var size = form.ClientSize;
        var key = $"{size.Width}x{size.Height}:{t.Background.ToArgb()}:{t.Accent.ToArgb()}:{t.Glow.ToArgb()}";
        if (entry.Key != key || entry.Clear is null || entry.Frosted is null)
        {
            entry.Clear?.Dispose(); entry.Frosted?.Dispose();
            entry.Clear = Render(size, t, false); entry.Frosted = Render(size, t, true); entry.Key = key;
        }
        return frosted ? entry.Frosted : entry.Clear;
    }

    public static Bitmap Render(Size size, ThemePalette t, bool frosted = false)
    {
        // Frosted glass = the same wallpaper seen through a much blurrier, brighter, more saturated lens.
        var divisor = frosted ? 14 : 4;
        var small = new Size(Math.Max(6, size.Width / divisor), Math.Max(6, size.Height / divisor));
        using var low = new Bitmap(small.Width, small.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(low))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var baseFill = new LinearGradientBrush(new Rectangle(Point.Empty, small), UiTheme.Blend(t.Background, Color.White, 0.03f), UiTheme.Blend(t.Background, Color.Black, 0.28f), 80f))
                g.FillRectangle(baseFill, 0, 0, small.Width, small.Height);
            var span = Math.Max(small.Width, small.Height);
            void Pool(float cx, float cy, float radius, Color color, int alpha)
            {
                var r = span * radius; var rect = new RectangleF(small.Width * cx - r, small.Height * cy - r, r * 2, r * 2);
                using var path = new GraphicsPath(); path.AddEllipse(rect);
                using var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb(alpha, color), SurroundColors = [Color.FromArgb(0, color)] };
                brush.Blend = new Blend { Factors = [0f, 0.3f, 0.72f, 1f], Positions = [0f, 0.4f, 0.75f, 1f] };
                g.FillEllipse(brush, rect);
            }
            Pool(0.04f, 0.02f, 0.62f, t.Accent, 95);
            Pool(0.98f, 0.98f, 0.70f, t.Glow, 88);
            Pool(0.62f, 0.12f, 0.36f, UiTheme.Blend(t.Accent, t.Glow, 0.5f), 52);
            Pool(0.18f, 1.02f, 0.44f, t.Glow, 46);
            Pool(0.84f, 0.46f, 0.24f, t.Accent, 30);
        }
        var image = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(image))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (frosted)
            {
                // Vibrancy: +25% saturation, a lift in brightness.
                const float s = 1.25f, lr = 0.2126f, lg = 0.7152f, lb = 0.0722f, lift = 0.05f;
                attributes.SetColorMatrix(new ColorMatrix(
                [
                    [lr * (1 - s) + s, lr * (1 - s), lr * (1 - s), 0, 0],
                    [lg * (1 - s), lg * (1 - s) + s, lg * (1 - s), 0, 0],
                    [lb * (1 - s), lb * (1 - s), lb * (1 - s) + s, 0, 0],
                    [0, 0, 0, 1, 0],
                    [lift, lift, lift, 0, 1],
                ]));
            }
            g.DrawImage(low, new Rectangle(Point.Empty, image.Size), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attributes);
        }
        return image;
    }
}

/// <summary>Apple-style liquid glass primitives: flat translucent shapes, luminous edges, soft ambient shadows. No bevels, no gloss.</summary>
public static class Glass
{
    public static void Shadow(Graphics g, RectangleF r, float radius, float dx, float dy, float blur, Color color)
    {
        if (color.A == 0 || r.Width <= 0 || r.Height <= 0) return;
        var steps = Math.Clamp((int)(blur / 2.5f), 3, 6);
        for (var i = steps; i >= 1; i--)
        {
            var grow = blur * i / steps;
            var alpha = (int)(color.A * (1f - (i - 1f) / steps) / steps * 1.6f);
            var rect = new RectangleF(r.X + dx - grow / 2, r.Y + dy - grow / 2, r.Width + grow, r.Height + grow);
            using var path = UiTheme.Rounded(rect, radius + grow / 2);
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), color));
            g.FillPath(brush, path);
        }
    }

    /// <summary>The luminous liquid edge: a crisp hairline brighter at the top, an inner glow band, and a faint dark outline for separation.</summary>
    public static void Edge(Graphics g, RectangleF r, float radius, float s, float strength = 1)
    {
        if (r.Width < 4 || r.Height < 4 || strength <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int A(float a) => Math.Clamp((int)(a * strength), 0, 255);
        using (var outline = UiTheme.Rounded(RectangleF.Inflate(r, 0.5f, 0.5f), radius + 0.5f))
        using (var dark = new Pen(Color.FromArgb(A(55), 0, 0, 0), 1f)) g.DrawPath(dark, outline);
        using (var glowPath = UiTheme.Rounded(RectangleF.Inflate(r, -1.6f * s, -1.6f * s), Math.Max(1, radius - 1.6f * s)))
        using (var glowBrush = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb(A(34), 255, 255, 255), Color.FromArgb(A(4), 255, 255, 255), 65f))
        using (var glow = new Pen(glowBrush, 2.4f * s)) g.DrawPath(glow, glowPath);
        using (var hairPath = UiTheme.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Math.Max(1, radius - 0.5f)))
        using (var hairBrush = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb(A(105), 255, 255, 255), Color.FromArgb(A(16), 255, 255, 255), 80f))
        using (var hair = new Pen(hairBrush, Math.Max(1f, s))) g.DrawPath(hair, hairPath);
    }

    /// <summary>A glass card: frosted interior is painted by the caller; this adds the tint and the edge.</summary>
    public static void CardShadow(Graphics g, RectangleF r, float radius, float s, float elevation) =>
        Shadow(g, r, radius, 0, 8 * s * elevation, 26 * s * elevation, Color.FromArgb((int)(80 * elevation), 0, 0, 0));

    /// <summary>Flat capsule/circle: translucent glass (white alpha) or a solid tint, with a liquid edge. Press darkens, hover lifts.</summary>
    public static void Pill(Graphics g, RectangleF r, float radius, float s, Color fill, float hover = 0, float press = 0, float edge = 1)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(r, radius);
        var top = fill.A == 255 ? UiTheme.Blend(fill, Color.White, 0.08f + hover * 0.08f) : Color.FromArgb(Math.Min(255, fill.A + (int)(hover * 16)), fill);
        var bottom = fill.A == 255 ? UiTheme.Blend(fill, Color.Black, 0.04f) : Color.FromArgb(Math.Max(0, fill.A - 4 + (int)(hover * 10)), fill);
        using (var brush = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), top, bottom, 90f)) g.FillPath(brush, path);
        if (press > 0.01f) { using var dim = new SolidBrush(Color.FromArgb((int)(42 * press), 0, 0, 0)); g.FillPath(dim, path); }
        Edge(g, r, radius, s, edge * (fill.A == 255 ? 0.7f : 1f) * (1 + hover * 0.35f));
    }

    /// <summary>Input / list well: an opaque, slightly darker capsule (native text boxes sit inside it seamlessly).</summary>
    public static void Field(Graphics g, RectangleF r, float radius, Color fill, float s, float focus = 0)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(r, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        var state = g.Save(); g.SetClip(path, CombineMode.Intersect);
        using (var top = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, Math.Max(2, 8 * s)), Color.FromArgb(46, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
            g.FillRectangle(top, r.X, r.Y, r.Width, 8 * s);
        g.Restore(state);
        using (var edge = UiTheme.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Math.Max(1, radius - 0.5f)))
        using (var edgeBrush = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb(18, 255, 255, 255), Color.FromArgb(46, 255, 255, 255), 90f))
        using (var pen = new Pen(edgeBrush, Math.Max(1f, s))) g.DrawPath(pen, edge);
        if (focus > 0.01f)
        {
            using var ring = UiTheme.Rounded(RectangleF.Inflate(r, -0.5f, -0.5f), radius); using var pen = new Pen(UiTheme.WithAlpha(UiTheme.Current.Accent, (int)(170 * focus)), 1.4f * s);
            g.DrawPath(pen, ring);
        }
    }

    /// <summary>White iOS-style knob with a soft drop shadow.</summary>
    public static void Knob(Graphics g, RectangleF r, float s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Shadow(g, r, r.Width / 2, 0, 1.5f * s, 6 * s, Color.FromArgb(90, 0, 0, 0));
        using var fill = new SolidBrush(Color.FromArgb(250, 248, 249, 252)); g.FillEllipse(fill, r);
        using var rim = new Pen(Color.FromArgb(40, 0, 0, 0), 0.8f); g.DrawEllipse(rim, r);
    }

    /// <summary>Flat gradient avatar with initials (no gloss, no bevel).</summary>
    public static void Avatar(Graphics g, RectangleF r, string id, string name, bool group = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = UiTheme.AvatarColor(id);
        using (var fill = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), UiTheme.Blend(color, Color.White, 0.12f), UiTheme.Blend(color, Color.Black, 0.28f), 90f))
            g.FillEllipse(fill, r);
        using (var rim = new Pen(Color.FromArgb(46, 255, 255, 255), 1f)) g.DrawEllipse(rim, RectangleF.Inflate(r, -0.5f, -0.5f));
        var ink = Color.FromArgb(240, 255, 255, 255);
        if (group) UiIcons.Draw(g, UiIcon.Group, RectangleF.Inflate(r, -r.Width * 0.25f, -r.Height * 0.25f), ink);
        else
        {
            using var font = new Font("Segoe UI Semibold", Math.Max(6, r.Height * 0.38f), GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, UiTheme.Initials(name), font, Rectangle.Round(r), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
        }
    }

    public static void StatusDot(Graphics g, PointF center, float size, Color color, Color ring)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var r = new SolidBrush(ring)) g.FillEllipse(r, center.X - size / 2 - 2, center.Y - size / 2 - 2, size + 4, size + 4);
        using (var b = new SolidBrush(color)) g.FillEllipse(b, center.X - size / 2, center.Y - size / 2, size, size);
    }
}

/// <summary>Plain container that shows the backdrop (and glass of its ancestors) instead of a flat colour.</summary>
public class BackdropPanel : Panel
{
    public BackdropPanel()
    {
        DoubleBuffered = true; Margin = Padding.Empty;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}

/// <summary>Label that sits on glass: paints the backdrop slice behind itself, keeps an opaque BackColor for the framework.</summary>
public class UiLabel : Label
{
    public UiLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}
