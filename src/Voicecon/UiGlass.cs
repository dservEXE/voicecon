using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Voicecon;

/// <summary>A container with an opaque face colour. Children paint exactly that colour behind themselves, so nothing shows seams.</summary>
public interface ISurface
{
    Color Face { get; }
}

/// <summary>
/// The window canvas: a matte base with very soft, deep pools of colour (violet, pink, red, blue, teal) that drift slowly.
/// Cards and controls on them have flat faces, so the colour shows only in the gaps and shadows around the cards.
/// The pools are a tiny low-resolution image scaled up while painting; nothing is cached per window size, and the drift
/// repaints only the canvas gaps a few times a second while the window is active.
/// </summary>
public static class Backdrop
{
    private static readonly ImageAttributes clamp = CreateClamp();
    private static ImageAttributes CreateClamp() { var a = new ImageAttributes(); a.SetWrapMode(WrapMode.TileFlipXY); return a; }
    private static Bitmap? pools;
    private static string poolsKey = "";
    /// <summary>Show the colour pools (user setting).</summary>
    public static bool Glow { get; set; } = true;
    /// <summary>Drift clock in seconds; advanced by the window while it is active.</summary>
    public static double Phase { get; set; }

    public static Color Behind(Control control)
    {
        for (var current = control.Parent; current is not null; current = current.Parent)
            if (current is ISurface surface) return surface.Face;
        return UiTheme.Current.Background;
    }
    /// <summary>True when the control sits directly on the window canvas (no card between it and the window).</summary>
    public static bool OnCanvas(Control control)
    {
        for (var current = control.Parent; current is not null; current = current.Parent) if (current is ISurface) return false;
        return true;
    }
    public static void Paint(Graphics graphics, Control control, Rectangle area)
    {
        if (!OnCanvas(control) || !Glow) { using var fill = new SolidBrush(Behind(control)); graphics.FillRectangle(fill, area); return; }
        // Position of the control inside its window: every control samples the same window-wide image, so there are no seams.
        Form? form = null; var x = 0; var y = 0;
        for (var current = control; current is not null; current = current.Parent)
        {
            if (current is Form f) { form = f; break; }
            // A page rendered off-screen samples the canvas as if it already stood where it will be shown.
            var place = current == renderRoot ? renderOrigin : current.Location;
            x += place.X; y += place.Y;
        }
        if (form is null || form.ClientSize.Width < 8 || form.ClientSize.Height < 8) { using var fill = new SolidBrush(UiTheme.Current.Background); graphics.FillRectangle(fill, area); return; }
        // The canvas is kept at half resolution (a soft gradient loses nothing); each pixel covers a 2×2 block.
        var image = CanvasFor(form);
        int left = (area.X + x) / 2, top = (area.Y + y) / 2, right = (area.Right + x + 1) / 2, bottom = (area.Bottom + y + 1) / 2;
        var source = Rectangle.FromLTRB(Math.Max(0, left), Math.Max(0, top), Math.Min(image.Width, right), Math.Min(image.Height, bottom));
        if (source.Width * 2 < area.Width || source.Height * 2 < area.Height) { using var fill = new SolidBrush(UiTheme.Current.Background); graphics.FillRectangle(fill, area); }
        if (source.Width <= 0 || source.Height <= 0) return;
        var state = graphics.Save();
        graphics.SetClip(area, CombineMode.Intersect);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half; graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(image, new Rectangle(source.X * 2 - x, source.Y * 2 - y, source.Width * 2, source.Height * 2), source, GraphicsUnit.Pixel);
        graphics.Restore(state);
    }
    [ThreadStatic] private static Control? renderRoot;
    [ThreadStatic] private static Point renderOrigin;
    /// <summary>Renders <paramref name="control"/> as it will look at <paramref name="location"/> in its parent, wherever it is now.</summary>
    public static T RenderAt<T>(Control control, Point location, Func<T> render)
    {
        var (root, origin) = (renderRoot, renderOrigin); renderRoot = control; renderOrigin = location;
        try { return render(); } finally { renderRoot = root; renderOrigin = origin; }
    }
    /// <summary>Opaque colour behind a control, for native children (text boxes, labels) that need a BackColor.</summary>
    public static Color Approximate(Control control) => control is ISurface surface ? surface.Face : Behind(control);
    public static void Invalidate(Form form) { }

    /// <summary>Repaints only what shows the canvas: gaps of containers on it and the shadow rings of cards.</summary>
    public static void InvalidateCanvas(Control root)
    {
        if (!Glow || !root.Visible && root is not Form) return;
        if (root is SurfacePanel card) { card.InvalidateGutter(); return; }
        if (root is ISurface) return;
        root.Invalidate(false);
        foreach (Control child in root.Controls) if (child.Visible) InvalidateCanvas(child);
    }

    private static readonly (Color Color, double Speed, double Phase, double Radius)[] Colors =
    [
        (Color.FromArgb(124, 77, 255), 1 / 70.0, 0.0, 0.55),   // violet
        (Color.FromArgb(255, 79, 163), 1 / 85.0, 2.1, 0.48),   // pink
        (Color.FromArgb(255, 69, 84), 1 / 97.0, 4.0, 0.42),    // red
        (Color.FromArgb(63, 107, 255), 1 / 78.0, 5.3, 0.52),   // blue
        (Color.FromArgb(37, 194, 176), 1 / 110.0, 1.2, 0.40),  // teal
    ];
    private sealed class CanvasImage { public Bitmap? Image; public string Key = ""; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, CanvasImage> canvases = new();
    /// <summary>The pools scaled up to the window once per drift step; controls then copy their slice unscaled.</summary>
    private static Bitmap CanvasFor(Form form)
    {
        var small = Pools(); var size = new Size((form.ClientSize.Width + 1) / 2, (form.ClientSize.Height + 1) / 2); var entry = canvases.GetOrCreateValue(form);
        var key = poolsKey + ":" + size.Width + "x" + size.Height;
        if (entry.Image is not null && entry.Key == key) return entry.Image;
        if (entry.Image is null || entry.Image.Size != size) { entry.Image?.Dispose(); entry.Image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb); }
        using (var g = Graphics.FromImage(entry.Image))
        {
            g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(Point.Empty, size), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, clamp);
        }
        entry.Key = key; return entry.Image;
    }
    private static Bitmap Pools()
    {
        var t = UiTheme.Current; var step = (int)Math.Floor(Phase * 2);
        var key = $"{t.Background.ToArgb()}:{t.Dark}:{step}";
        if (pools is not null && poolsKey == key) return pools;
        const int width = 64, height = 42;
        var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(t.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
            var time = step / 2.0; var strength = t.Dark ? 46 : 60;
            foreach (var (color, speed, phase, radius) in Colors)
            {
                var angle = time * speed * Math.PI + phase;
                var cx = width * (0.5 + 0.46 * Math.Sin(angle)); var cy = height * (0.5 + 0.44 * Math.Cos(angle * 0.83 + phase));
                var r = (float)(Math.Max(width, height) * radius); var rect = new RectangleF((float)cx - r, (float)cy - r, r * 2, r * 2);
                using var path = new GraphicsPath(); path.AddEllipse(rect);
                using var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb(strength, color), SurroundColors = [Color.FromArgb(0, color)] };
                brush.Blend = new Blend { Factors = [0f, 0.35f, 0.75f, 1f], Positions = [0f, 0.45f, 0.8f, 1f] };
                g.FillEllipse(brush, rect);
            }
        }
        pools?.Dispose(); pools = image; poolsKey = key; return image;
    }
}

/// <summary>
/// Neumorphic primitives: raised shapes cast a dark shadow to the bottom right and a soft light one to the top left;
/// inset wells carry the same pair inside. Shadows are real blurs, rendered once per shape class and drawn as nine slices,
/// so painting costs a handful of bitmap copies and never re-blurs.
/// </summary>
public static class Neu
{
    private const float RaisedOffset = 4f, RaisedBlur = 10f, InsetOffset = 2.5f, InsetBlur = 6f;
    public const float CardDepth = 1f, ButtonDepth = 0.4f, ChipDepth = 0.45f, SmallDepth = 0.3f;

    /// <summary>Space a raised shape of this depth needs around it so that its shadow is never clipped.</summary>
    public static int Extent(float depth, float s) => depth <= 0 ? 0 : (int)Math.Ceiling(Math.Max(1, Math.Round(RaisedOffset * depth * s)) + Math.Max(2, Math.Round(RaisedBlur * depth * s))) + 1;

    /// <summary>A raised shape. <paramref name="convex"/> adds a gentle top-left light to leaf controls (never to containers: their children paint the flat face).</summary>
    public static void Raised(Graphics g, RectangleF r, float radius, float s, float depth = CardDepth, Color? face = null, float convex = 0, float rim = 1)
    {
        if (r.Width < 2 || r.Height < 2) return;
        var t = UiTheme.Current; var color = face ?? t.Surface; radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var offset = (int)Math.Max(1, Math.Round(RaisedOffset * depth * s)); var blur = (int)Math.Max(2, Math.Round(RaisedBlur * depth * s));
        if (depth > 0.01f && convex <= 0)
        {
            // Flat face: shadows, face and lit edge come from one cached template (a few bitmap copies per paint).
            Shadows.Raised(g, r, radius, blur, offset, t.ShadowDark, t.ShadowLight, color, rim);
            return;
        }
        if (depth > 0.01f) Shadows.Raised(g, r, radius, blur, offset, t.ShadowDark, t.ShadowLight, null, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(r, radius);
        if (convex > 0)
        {
            using var brush = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), UiTheme.Blend(color, Color.White, 0.07f * convex), UiTheme.Blend(color, Color.Black, 0.10f * convex), 45f);
            g.FillPath(brush, path);
        }
        else { using var brush = new SolidBrush(color); g.FillPath(brush, path); }
        if (rim > 0) Rim(g, r, radius, s, rim);
    }

    /// <summary>A pressed-in well: flat face with a dark inner shadow along the top left and a faint light one along the bottom right.</summary>
    public static void Inset(Graphics g, RectangleF r, float radius, float s, float depth = 1, Color? face = null)
    {
        if (r.Width < 2 || r.Height < 2) return;
        var t = UiTheme.Current; radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2); var color = face ?? t.Field;
        if (depth <= 0.01f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = UiTheme.Rounded(r, radius); using var brush = new SolidBrush(color); g.FillPath(brush, path); return;
        }
        var offset = (int)Math.Max(1, Math.Round(InsetOffset * depth * s)); var blur = (int)Math.Max(2, Math.Round(InsetBlur * depth * s));
        Shadows.Inset(g, r, radius, blur, offset, t.InsetDark, t.InsetLight, color);
    }

    /// <summary>The thin lit edge of a raised shape: bright along the top and left, gone along the bottom and right.</summary>
    public static void Rim(Graphics g, RectangleF r, float radius, float s, float strength = 1)
    {
        if (r.Width < 4 || r.Height < 4 || strength <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Shadows.StrokeRim(g, r, radius, Math.Max(1f, s), strength);
    }

    /// <summary>Accent focus ring just outside a shape.</summary>
    public static void Focus(Graphics g, RectangleF r, float radius, float s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(RectangleF.Inflate(r, 1.5f * s, 1.5f * s), radius + 1.5f * s);
        using var pen = new Pen(UiTheme.WithAlpha(UiTheme.Current.Accent, 170), 1.4f * s); g.DrawPath(pen, path);
    }

    /// <summary>Raised round knob for switches and sliders.</summary>
    public static void Knob(Graphics g, RectangleF r, float s, Color? face = null) =>
        Raised(g, r, r.Width / 2, s, SmallDepth, face ?? UiTheme.Blend(UiTheme.Current.Surface, Color.White, 0.10f), convex: 1);

    /// <summary>Avatar: a slightly raised coloured disc with initials or the group glyph.</summary>
    public static void Avatar(Graphics g, RectangleF r, string id, string name, bool group = false, float depth = SmallDepth)
    {
        var color = UiTheme.AvatarColor(id);
        Raised(g, r, r.Width / 2, 1, depth, color, convex: 0.9f, rim: 0.8f);
        var ink = Color.FromArgb(245, 255, 255, 255);
        if (group) UiIcons.Draw(g, UiIcon.Group, RectangleF.Inflate(r, -r.Width * 0.25f, -r.Height * 0.25f), ink);
        else
        {
            using var font = new Font("Segoe UI Semibold", Math.Max(6, r.Height * 0.38f), GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, UiTheme.Initials(name), font, Rectangle.Round(r), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
        }
    }

    /// <summary>Presence dot set into the face it sits on.</summary>
    public static void StatusDot(Graphics g, PointF center, float size, Color color, Color? ring = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var r = new SolidBrush(ring ?? UiTheme.Current.Surface)) g.FillEllipse(r, center.X - size / 2 - 2.5f, center.Y - size / 2 - 2.5f, size + 5, size + 5);
        using (var b = new SolidBrush(color)) g.FillEllipse(b, center.X - size / 2, center.Y - size / 2, size, size);
    }
}

/// <summary>
/// Cached neumorphic templates: a small, really blurred image of a whole shape (both shadows, face and lit edge), drawn as
/// nine slices: corners are copied, edges and middle are stretched from one pixel. A card of any size costs nine copies.
/// </summary>
internal static class Shadows
{
    private sealed record Key(bool Inset, int Width, int Height, int Radius, int Blur, int Offset, int Dark, int Light, int Face, int Rim);
    private static readonly Dictionary<Key, Bitmap> cache = [];
    private static readonly ImageAttributes clamp = CreateClamp();
    private static ImageAttributes CreateClamp() { var a = new ImageAttributes(); a.SetWrapMode(WrapMode.TileFlipXY); return a; }

    /// <summary>Outer shadow pair, optionally with the flat face and its rim baked in.</summary>
    public static void Raised(Graphics g, RectangleF shape, float radius, int blur, int offset, Color dark, Color light, Color? face, float rim)
    {
        var r = Rectangle.Round(shape); if (r.Width < 2 || r.Height < 2) return;
        var rad = (int)Math.Round(Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
        var nominal = 2 * (rad + blur + offset) + 3;
        int w = Math.Min(r.Width, nominal), h = Math.Min(r.Height, nominal);
        var image = Get(new Key(false, w, h, rad, blur, offset, dark.ToArgb(), light.ToArgb(), face?.ToArgb() ?? 0, face is null ? 0 : (int)Math.Round(rim * 100)));
        var extent = (image.Width - w) / 2;
        Draw(g, image, new Rectangle(r.X - extent, r.Y - extent, r.Width + 2 * extent, r.Height + 2 * extent), r.Width > w, r.Height > h, skipCenter: face is null);
    }

    /// <summary>Inset well: flat face with both inner shadows.</summary>
    public static void Inset(Graphics g, RectangleF shape, float radius, int blur, int offset, Color dark, Color light, Color face)
    {
        var r = Rectangle.Round(shape); if (r.Width < 2 || r.Height < 2) return;
        var rad = (int)Math.Round(Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
        var nominal = 2 * (rad + blur + offset) + 3;
        int w = Math.Min(r.Width, nominal), h = Math.Min(r.Height, nominal);
        var image = Get(new Key(true, w, h, rad, blur, offset, dark.ToArgb(), light.ToArgb(), face.ToArgb(), 0));
        Draw(g, image, r, r.Width > w, r.Height > h, skipCenter: false);
    }

    /// <summary>Lit edge as two strokes: one fading down from the top, one fading right from the left. Slices cleanly.</summary>
    public static void StrokeRim(Graphics g, RectangleF r, float radius, float width, float strength)
    {
        var edge = new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width);
        using var path = UiTheme.Rounded(edge, Math.Max(0.5f, radius - width / 2));
        var top = Color.FromArgb(Math.Clamp((int)(30 * strength), 0, 255), 255, 255, 255); var none = Color.FromArgb(0, 255, 255, 255);
        var bounds = RectangleF.Inflate(r, 0.5f, 0.5f);
        using (var vertical = new LinearGradientBrush(bounds, top, none, 90f) { Blend = new Blend { Factors = [0f, 0.85f, 1f], Positions = [0f, 0.35f, 1f] } })
        using (var pen = new Pen(vertical, width)) g.DrawPath(pen, path);
        using (var horizontal = new LinearGradientBrush(bounds, Color.FromArgb(top.A * 2 / 3, top), none, 0f) { Blend = new Blend { Factors = [0f, 0.85f, 1f], Positions = [0f, 0.35f, 1f] } })
        using (var pen = new Pen(horizontal, width)) g.DrawPath(pen, path);
    }

    private static void Draw(Graphics g, Bitmap image, Rectangle dest, bool sliceX, bool sliceY, bool skipCenter)
    {
        int width = image.Width, height = image.Height, cx = width / 2, cy = height / 2;
        (int Src, int Size, int Dst, int DstSize)[] xs = sliceX
            ? [(0, cx, dest.X, cx), (cx, 1, dest.X + cx, dest.Width - width + 1), (cx + 1, width - cx - 1, dest.Right - (width - cx - 1), width - cx - 1)]
            : [(0, width, dest.X, width)];
        (int Src, int Size, int Dst, int DstSize)[] ys = sliceY
            ? [(0, cy, dest.Y, cy), (cy, 1, dest.Y + cy, dest.Height - height + 1), (cy + 1, height - cy - 1, dest.Bottom - (height - cy - 1), height - cy - 1)]
            : [(0, height, dest.Y, height)];
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half; g.CompositingMode = CompositingMode.SourceOver; g.CompositingQuality = CompositingQuality.HighSpeed;
        var clip = g.ClipBounds;
        for (var i = 0; i < xs.Length; i++)
            for (var j = 0; j < ys.Length; j++)
            {
                var x = xs[i]; var y = ys[j]; if (x.DstSize <= 0 || y.DstSize <= 0) continue;
                var target = new Rectangle(x.Dst, y.Dst, x.DstSize, y.DstSize);
                if (!clip.IntersectsWith(target)) continue;
                if (sliceX && sliceY && i == 1 && j == 1)
                {
                    // The middle is uniform: fill it with that one colour (or skip it when it is empty / under the face).
                    if (skipCenter) continue;
                    var pixel = image.GetPixel(cx, cy);
                    if (pixel.A == 255) { using var brush = new SolidBrush(pixel); g.CompositingMode = CompositingMode.SourceCopy; g.FillRectangle(brush, target); g.CompositingMode = CompositingMode.SourceOver; continue; }
                }
                g.DrawImage(image, target, x.Src, y.Src, x.Size, y.Size, GraphicsUnit.Pixel, clamp);
            }
        g.Restore(state);
    }

    private static Bitmap Get(Key key)
    {
        if (cache.TryGetValue(key, out var image)) return image;
        if (cache.Count > 400) { foreach (var old in cache.Values) old.Dispose(); cache.Clear(); }
        image = key.Inset ? BuildInset(key) : BuildRaised(key);
        cache[key] = image; return image;
    }

    private static Bitmap BuildRaised(Key k)
    {
        var extent = k.Blur + k.Offset + 1;
        int width = k.Width + 2 * extent, height = k.Height + 2 * extent;
        var dark = Mask(width, height, new RectangleF(extent + k.Offset, extent + k.Offset, k.Width, k.Height), k.Radius); Blur(dark, width, height, k.Blur);
        var light = Mask(width, height, new RectangleF(extent - k.Offset, extent - k.Offset, k.Width, k.Height), k.Radius); Blur(light, width, height, k.Blur);
        var bitmap = Compose(width, height, null, [(dark, Color.FromArgb(k.Dark)), (light, Color.FromArgb(k.Light))]);
        if (k.Face != 0)
        {
            using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
            var shape = new RectangleF(extent, extent, k.Width, k.Height);
            using (var path = UiTheme.Rounded(shape, k.Radius)) using (var brush = new SolidBrush(Color.FromArgb(k.Face))) g.FillPath(brush, path);
            if (k.Rim > 0) StrokeRim(g, shape, k.Radius, 1, k.Rim / 100f);
        }
        return bitmap;
    }

    private static Bitmap BuildInset(Key k)
    {
        var pad = k.Blur + k.Offset + 2;
        int width = k.Width + 2 * pad, height = k.Height + 2 * pad;
        int[] Inner(int dx, int dy)
        {
            var shifted = Mask(width, height, new RectangleF(pad + dx, pad + dy, k.Width, k.Height), k.Radius);
            for (var i = 0; i < shifted.Length; i++) shifted[i] = 255 - shifted[i];
            Blur(shifted, width, height, k.Blur); return Crop(shifted, width, pad, k.Width, k.Height);
        }
        var shape = Crop(Mask(width, height, new RectangleF(pad, pad, k.Width, k.Height), k.Radius), width, pad, k.Width, k.Height);
        var dark = Inner(k.Offset, k.Offset); var light = Inner(-k.Offset, -k.Offset);
        for (var i = 0; i < shape.Length; i++) { dark[i] = dark[i] * shape[i] / 255; light[i] = light[i] * shape[i] / 255; }
        return Compose(k.Width, k.Height, (shape, Color.FromArgb(k.Face)), [(dark, Color.FromArgb(k.Dark)), (light, Color.FromArgb(k.Light))]);
    }

    private static int[] Crop(int[] source, int stride, int pad, int width, int height)
    {
        var result = new int[width * height];
        for (var y = 0; y < height; y++) Array.Copy(source, (y + pad) * stride + pad, result, y * width, width);
        return result;
    }

    private static int[] Mask(int width, int height, RectangleF shape, float radius)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = UiTheme.Rounded(shape, radius); g.FillPath(Brushes.White, path);
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var mask = new int[width * height];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) mask[y * width + x] = bytes[y * data.Stride + x * 4 + 3];
            return mask;
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>Three box passes per axis: a close, cheap approximation of a gaussian whose reach is <paramref name="blur"/> pixels.</summary>
    private static void Blur(int[] values, int width, int height, int blur)
    {
        var radius = Math.Max(1, (int)Math.Round(blur / 3.0));
        var buffer = new int[Math.Max(width, height)];
        for (var pass = 0; pass < 3; pass++)
        {
            for (var y = 0; y < height; y++) BoxLine(values, y * width, 1, width, radius, buffer);
            for (var x = 0; x < width; x++) BoxLine(values, x, width, height, radius, buffer);
        }
    }
    private static void BoxLine(int[] values, int start, int step, int length, int radius, int[] buffer)
    {
        for (var i = 0; i < length; i++) buffer[i] = values[start + i * step];
        var window = 2 * radius + 1; var sum = 0;
        for (var i = -radius; i <= radius; i++) sum += i >= 0 && i < length ? buffer[i] : 0;
        for (var i = 0; i < length; i++)
        {
            values[start + i * step] = sum / window;
            var add = i + radius + 1; var remove = i - radius;
            if (add < length) sum += buffer[add];
            if (remove >= 0) sum -= buffer[remove];
        }
    }

    /// <summary>Stacks coverage masks with source-over, bottom first, into a premultiplied bitmap.</summary>
    private static Bitmap Compose(int width, int height, (int[] Mask, Color Color)? bottom, (int[] Mask, Color Color)[] layers)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[data.Stride * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x; float r = 0, g = 0, b = 0, a = 0;
                    void Over((int[] Mask, Color Color) layer)
                    {
                        var alpha = layer.Mask[index] / 255f * layer.Color.A / 255f; if (alpha <= 0) return;
                        r = layer.Color.R * alpha + r * (1 - alpha); g = layer.Color.G * alpha + g * (1 - alpha); b = layer.Color.B * alpha + b * (1 - alpha); a = alpha + a * (1 - alpha);
                    }
                    if (bottom is { } face) Over(face);
                    foreach (var layer in layers) Over(layer);
                    var i = y * data.Stride + x * 4;
                    bytes[i] = (byte)Math.Clamp(b + 0.5f, 0, 255); bytes[i + 1] = (byte)Math.Clamp(g + 0.5f, 0, 255); bytes[i + 2] = (byte)Math.Clamp(r + 0.5f, 0, 255); bytes[i + 3] = (byte)Math.Clamp(a * 255 + 0.5f, 0, 255);
                }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}

/// <summary>Plain container that shows the face of the surface it sits on.</summary>
public class BackdropPanel : Panel
{
    public BackdropPanel()
    {
        DoubleBuffered = true; Margin = Padding.Empty;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}

/// <summary>Label on a surface: paints the face behind itself, keeps an opaque BackColor for the framework.</summary>
public class UiLabel : Label
{
    public UiLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}
