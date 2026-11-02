using System.Drawing.Imaging;

namespace Voicecon;

internal sealed class PageFade : Control
{
    private readonly Bitmap oldPage, newPage;
    private float progress;
    private PageFade(Bitmap oldPage, Bitmap newPage) { this.oldPage = oldPage; this.newPage = newPage; Dock = DockStyle.Fill; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true); }
    public static void Switch(Control host, Control? old, Control next, Form owner)
    {
        foreach (var transition in host.Controls.OfType<PageFade>().ToArray()) { UiMotion.Cancel(transition); transition.Dispose(); }
        Bitmap? before = null;
        if (UiMotion.Enabled && owner.Visible && host.Width > 0 && host.Height > 0 && old is not null)
        { before = new Bitmap(host.Width, host.Height); old.DrawToBitmap(before, new Rectangle(Point.Empty, before.Size)); }
        foreach (Control child in host.Controls) child.Visible = child == next;
        next.BringToFront();
        if (before is null) return;
        var after = new Bitmap(host.Width, host.Height); next.DrawToBitmap(after, new Rectangle(Point.Empty, after.Size));
        var overlay = new PageFade(before, after); host.Controls.Add(overlay); overlay.BringToFront();
        UiMotion.Run(overlay, owner, 180, p => { overlay.progress = p; overlay.Invalidate(); }, () => overlay.Dispose());
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImage(oldPage, ClientRectangle);
        using var attributes = new ImageAttributes(); var matrix = new ColorMatrix { Matrix33 = progress }; attributes.SetColorMatrix(matrix);
        e.Graphics.DrawImage(newPage, ClientRectangle, 0, 0, newPage.Width, newPage.Height, GraphicsUnit.Pixel, attributes);
    }
    protected override void Dispose(bool disposing) { if (disposing) { UiMotion.Cancel(this); oldPage.Dispose(); newPage.Dispose(); } base.Dispose(disposing); }
}
