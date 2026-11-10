namespace Voicecon;

/// <summary>Background helpers. Controls paint the shared backdrop slice; BackColor stays opaque for native children.</summary>
public static class UiPainting
{
    public static void Fill(Graphics graphics, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, bounds);
    }
    public static void Background(Graphics graphics, Control control, Rectangle bounds) => Backdrop.Paint(graphics, control, bounds);
    public static void ApplyBackgrounds(Control parent)
    {
        var t = UiTheme.Current;
        foreach (Control child in parent.Controls)
        {
            child.BackColor = child switch
            {
                TextBoxBase => t.Field,
                PictureBox => Color.Black,
                _ => Backdrop.Approximate(child)
            };
            ApplyBackgrounds(child);
        }
    }
}

public sealed class BufferedGrid : TableLayoutPanel
{
    public BufferedGrid()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}

public sealed class BufferedPeople : FlowLayoutPanel
{
    public BufferedPeople()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}
