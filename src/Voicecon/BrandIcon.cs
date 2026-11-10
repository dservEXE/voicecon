using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

namespace Voicecon;

public static class BrandIcon
{
    public static Icon Create() => new(new MemoryStream(CreateFile()), new Size(64, 64));
    public static byte[] CreateFile()
    {
        var frames = new List<byte[]>(); var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var size in sizes)
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.ScaleTransform(size / 64f, size / 64f); Draw(g);
            }
            using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png); frames.Add(png.ToArray());
        }
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return output.ToArray();
    }
    private static void Draw(Graphics graphics) => BrandArt.Draw(graphics, new RectangleF(0, 0, 64, 64));
}

/// <summary>
/// The Voicecon mark in the neumorphic style: a deep night-blue tile, softly lit from the top left, with four raised
/// level bars in deep pastel tones (lavender, rose, periwinkle, sea blue), each casting its own shadow.
/// Shared by the window icon, the tray and the logo in the sidebar.
/// </summary>
public static class BrandArt
{
    private static readonly (Color Top, Color Bottom)[] Bars =
    [
        (Color.FromArgb(182, 164, 250), Color.FromArgb(100, 80, 200)),
        (Color.FromArgb(236, 158, 198), Color.FromArgb(170, 80, 134)),
        (Color.FromArgb(150, 176, 248), Color.FromArgb(70, 98, 200)),
        (Color.FromArgb(128, 202, 222), Color.FromArgb(50, 130, 172)),
    ];
    private static readonly float[] Heights = [17, 31, 41, 25];

    /// <summary>Draws the mark into <paramref name="r"/> (designed on a 64-unit square).</summary>
    public static void Draw(Graphics g, RectangleF r, bool tile = true)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(r.X, r.Y); g.ScaleTransform(r.Width / 64f, r.Height / 64f);
        if (tile)
        {
            var shape = new RectangleF(1, 1, 62, 62);
            using (var path = UiTheme.Rounded(shape, 17))
            {
                using (var fill = new LinearGradientBrush(new RectangleF(0, 0, 64, 64), Color.FromArgb(44, 58, 104), Color.FromArgb(14, 19, 38), 50f)) g.FillPath(fill, path);
                // Soft light pooled in the upper left of the tile.
                var state2 = g.Save(); g.SetClip(path, CombineMode.Intersect);
                using (var glowPath = new GraphicsPath())
                {
                    glowPath.AddEllipse(-22, -24, 70, 66);
                    using var glow = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(70, 140, 160, 255), SurroundColors = [Color.FromArgb(0, 140, 160, 255)] };
                    g.FillEllipse(glow, -22, -24, 70, 66);
                }
                g.Restore(state2);
                using var rimBrush = new LinearGradientBrush(new RectangleF(0, 0, 64, 64), Color.FromArgb(110, 190, 205, 255), Color.FromArgb(10, 190, 205, 255), 55f);
                using var rim = new Pen(rimBrush, 1.3f); g.DrawPath(rim, path);
            }
        }
        for (var i = 0; i < Bars.Length; i++)
        {
            var x = 16.8f + i * 10.1f; var h = Heights[i]; var bar = new RectangleF(x - 3.8f, 32 - h / 2, 7.6f, h);
            // Raised bar: a soft shadow to the bottom right, a faint light to the top left, then the pastel body.
            for (var k = 3; k >= 1; k--)
            {
                using var shadow = UiTheme.Rounded(new RectangleF(bar.X + 1.1f * k - 0.6f * k, bar.Y + 1.3f * k - 0.6f * k, bar.Width + 1.2f * k, bar.Height + 1.2f * k), 3.8f + 0.6f * k);
                using var dark = new SolidBrush(Color.FromArgb(46, 4, 6, 18)); g.FillPath(dark, shadow);
            }
            using (var light = UiTheme.Rounded(new RectangleF(bar.X - 1.1f, bar.Y - 1.1f, bar.Width, bar.Height), 3.8f)) using (var lit = new SolidBrush(Color.FromArgb(18, 200, 210, 255))) g.FillPath(lit, light);
            using var body = UiTheme.Rounded(bar, 3.8f);
            using (var paint = new LinearGradientBrush(RectangleF.Inflate(bar, 0, 1), Bars[i].Top, Bars[i].Bottom, 90f)) g.FillPath(paint, body);
            using var sheen = new LinearGradientBrush(RectangleF.Inflate(bar, 1, 0), Color.FromArgb(75, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f) { Blend = new Blend { Factors = [1f, 0.15f, 0f], Positions = [0f, 0.45f, 1f] } };
            g.FillPath(sheen, body);
        }
        g.Restore(state);
    }
}
