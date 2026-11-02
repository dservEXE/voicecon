namespace Voicecon;

/// <summary>Paint flat backgrounds locally, without replaying the ancestor tree.</summary>
public static class UiPainting
{
    public static void Fill(Graphics graphics, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, bounds);
    }
    public static void ApplyBackgrounds(Control parent)
    {
        var t = UiTheme.Current;
        foreach (Control child in parent.Controls)
        {
            child.BackColor = child switch
            {
                SurfacePanel surface => surface.Recessed ? t.Field : t.Surface,
                TextBoxBase => t.Field,
                PictureBox => Color.Black,
                Panel line when line.Tag as string == "divider" => t.Border,
                _ => parent.BackColor
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
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
}

public sealed class BufferedPeople : FlowLayoutPanel
{
    public BufferedPeople()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
}
