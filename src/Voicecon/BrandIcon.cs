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
    private static void Draw(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Rounded(new RectangleF(1, 1, 62, 62), 17);
        using var fill = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(130, 163, 255), Color.FromArgb(79, 101, 231), 70f); graphics.FillPath(fill, shape);
        using var outline = new Pen(Color.FromArgb(185, 207, 255), 1.5f); graphics.DrawPath(outline, shape);
        using var bars = new Pen(Color.FromArgb(242, 248, 255), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var heights = new[] { 14, 27, 38, 22 };
        for (var i = 0; i < heights.Length; i++) graphics.DrawLine(bars, 18 + i * 10, 32 - heights[i] / 2f, 18 + i * 10, 32 + heights[i] / 2f);
    }
}
