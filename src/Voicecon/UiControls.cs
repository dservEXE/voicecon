using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Voicecon;

/// <summary>Frosted glass card (default) or an opaque input well. Children see its material as their background.</summary>
public sealed class SurfacePanel : Panel, IGlassLayer
{
    [DefaultValue(false)] public bool Recessed { get; set; }
    /// <summary>Space around the card shape reserved for its soft shadow.</summary>
    [DefaultValue(6)] public int Gutter { get; set; } = 6;
    [DefaultValue(24)] public int Radius { get; set; } = 24;
    [DefaultValue(1f)] public float Elevation { get; set; } = 1;
    /// <summary>Optional colour wash over the glass (update banner, incoming call, drawer).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Wash { get; set; }
    public bool Frosted => !Recessed;
    public Color LayerTint
    {
        get
        {
            var t = UiTheme.Current;
            if (Recessed) return t.Field;
            var tint = Color.FromArgb(t.GlassAlpha, t.Surface);
            return Wash is { } wash ? Color.FromArgb(Math.Min(255, t.GlassAlpha + wash.A), UiTheme.Blend(t.Surface, wash, wash.A / (float)(t.GlassAlpha + wash.A))) : tint;
        }
    }
    public SurfacePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.GlassSolid; Margin = Padding.Empty;
    }
    private int GutterPixels => Recessed ? 0 : (int)Math.Round(Gutter * DeviceDpi / 96f);
    public override Rectangle DisplayRectangle
    {
        get { var r = base.DisplayRectangle; var g = GutterPixels; return new Rectangle(r.X + g, r.Y + g, Math.Max(0, r.Width - 2 * g), Math.Max(0, r.Height - 2 * g)); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var s = DeviceDpi / 96f; var g = GutterPixels; var graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var shape = new RectangleF(g + 0.5f, g + 0.5f, Width - 2 * g - 1, Height - 2 * g - 1);
        var radius = Math.Min(Radius * s, shape.Height / 2);
        if (Recessed) { Glass.Field(graphics, shape, radius, UiTheme.Current.Field, s); base.OnPaint(e); return; }
        if (Elevation > 0) Glass.CardShadow(graphics, shape, radius, s, Elevation);
        using (var path = UiTheme.Rounded(shape, radius))
        {
            var state = graphics.Save(); graphics.SetClip(path, CombineMode.Intersect);
            Backdrop.Paint(graphics, this, Rectangle.Ceiling(shape), frosted: true);
            using (var tint = new SolidBrush(LayerTint)) graphics.FillRectangle(tint, shape);
            graphics.Restore(state);
        }
        Glass.Edge(graphics, shape, radius, s);
        base.OnPaint(e);
    }
}

/// <summary>The update banner: frosted glass washed with the accent colour.</summary>
public sealed class UpdateBannerPanel : Panel, IGlassLayer
{
    public Color LayerTint => Color.FromArgb(60, UiTheme.Blend(UiTheme.Current.Surface, UiTheme.Current.Accent, 0.8f));
    public bool Frosted => true;
    public UpdateBannerPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill; Margin = Padding.Empty; Padding = new Padding(18, 10, 12, 10); BackColor = UiTheme.Current.GlassSolid;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var s = DeviceDpi / 96f; var shape = new RectangleF(3.5f, 3.5f, Width - 7, Height - 7); var radius = Math.Min(20 * s, shape.Height / 2);
        using (var path = UiTheme.Rounded(shape, radius))
        {
            var state = e.Graphics.Save(); e.Graphics.SetClip(path, CombineMode.Intersect);
            Backdrop.Paint(e.Graphics, this, Rectangle.Ceiling(shape), frosted: true);
            using (var tint = new SolidBrush(LayerTint)) e.Graphics.FillRectangle(tint, shape);
            e.Graphics.Restore(state);
        }
        Glass.Edge(e.Graphics, shape, radius, s);
        base.OnPaint(e);
    }
}

public enum ButtonTone { Secondary, Primary, Quiet, Danger, Live }

/// <summary>Flat liquid-glass capsule button. Round icon-only variant via <see cref="Circle"/>.</summary>
public sealed class SoftButton : Button
{
    [DefaultValue(ButtonTone.Secondary)] public ButtonTone Tone { get; set; }
    [DefaultValue(UiIcon.None)] public UiIcon Glyph { get; set; }
    [DefaultValue(false)] public bool Selected { get; set; }
    [DefaultValue(false)] public bool Crossed { get; set; }
    [DefaultValue(false)] public bool Circle { get; set; }
    /// <summary>Corner radius; large values give a capsule.</summary>
    [DefaultValue(999)] public int Radius { get; set; } = 999;
    /// <summary>Small counter drawn on the button (viewers, unread).</summary>
    [DefaultValue("")] public string Badge { get; set; } = "";
    private float hoverAmount, pressAmount;
    private readonly object pressTrack = new();
    public static Color LiveColor => Color.FromArgb(255, 255, 178, 64);
    public SoftButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = UiTheme.Current.GlassSolid; Cursor = Cursors.Hand;
        Font = UiTheme.Strong; Margin = Padding.Empty; UseVisualStyleBackColor = false;
    }
    protected override void OnMouseEnter(EventArgs e) { AnimateHover(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { AnimateHover(0); AnimatePress(0); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { AnimatePress(1); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { AnimatePress(0); base.OnMouseUp(e); }
    private void AnimateHover(float target) { var start = hoverAmount; UiMotion.Run(this, this, 180, p => { hoverAmount = start + (target - start) * p; Invalidate(); }); }
    private void AnimatePress(float target) { var start = pressAmount; UiMotion.Run(pressTrack, this, 110, p => { pressAmount = start + (target - start) * p; Invalidate(); }); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var t = UiTheme.Current; var s = DeviceDpi / 96f; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var gutter = 2 * s;
        var side = Math.Min(Width, Height) - 2 * gutter;
        var shape = Circle ? new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side) : new RectangleF(gutter, gutter, Width - 2 * gutter, Height - 2 * gutter);
        // A gentle press: the shape eases inwards by a pixel, like pressing liquid.
        if (pressAmount > 0) shape = RectangleF.Inflate(shape, -pressAmount * 1.2f * s, -pressAmount * 1.2f * s);
        var radius = Circle ? shape.Width / 2 : Math.Min(Radius * s, shape.Height / 2);
        Color ink;
        if (!Enabled)
        {
            ink = UiTheme.WithAlpha(t.Muted, 120);
            if (Tone != ButtonTone.Quiet) Glass.Pill(g, shape, radius, s, Color.FromArgb(10, 255, 255, 255), 0, 0, 0.5f);
        }
        else if (Selected)
        {
            ink = UiTheme.Blend(t.Danger, Color.White, 0.25f);
            Glass.Pill(g, shape, radius, s, Color.FromArgb(70, t.Danger), hoverAmount, pressAmount);
        }
        else if (Tone is ButtonTone.Primary or ButtonTone.Danger or ButtonTone.Live)
        {
            var color = Tone switch { ButtonTone.Danger => t.Danger, ButtonTone.Live => LiveColor, _ => t.Accent };
            ink = Tone == ButtonTone.Primary ? t.AccentInk : Color.FromArgb(255, 34, 14, 10);
            Glass.Shadow(g, shape, radius, 0, 3 * s, 10 * s, Color.FromArgb(60, 0, 0, 0));
            Glass.Pill(g, shape, radius, s, color, hoverAmount, pressAmount);
        }
        else if (Tone == ButtonTone.Quiet)
        {
            ink = UiTheme.Blend(t.Muted, t.Text, 0.35f + hoverAmount * 0.65f);
            if (hoverAmount > 0.01f || pressAmount > 0.01f) Glass.Pill(g, shape, radius, s, Color.FromArgb((int)(16 * hoverAmount + 10 * pressAmount), 255, 255, 255), 0, pressAmount, hoverAmount * 0.8f);
        }
        else
        {
            ink = t.Text;
            Glass.Pill(g, shape, radius, s, Color.FromArgb(22, 255, 255, 255), hoverAmount, pressAmount);
        }
        if (Focused && ShowFocusCues) { using var path = UiTheme.Rounded(RectangleF.Inflate(shape, 2 * s, 2 * s), radius + 2 * s); using var focus = new Pen(UiTheme.WithAlpha(t.Accent, 170), 1.5f * s); g.DrawPath(focus, path); }
        var icon = Glyph != UiIcon.None; var text = Text.Length > 0 && !Circle;
        var iconSize = Circle ? Math.Min(shape.Width * 0.44f, 20 * s) : 16 * s; var gap = 8 * s;
        var textWidth = text ? TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
        var groupWidth = Math.Min(shape.Width - 16 * s, textWidth + (icon ? iconSize + (text ? gap : 0) : 0));
        var left = shape.X + (shape.Width - groupWidth) / 2; var center = shape.Y + shape.Height / 2;
        if (icon) { UiIcons.Draw(g, Glyph, new RectangleF(left, center - iconSize / 2, iconSize, iconSize), ink, Crossed); left += iconSize + gap; }
        if (text) TextRenderer.DrawText(g, Text, Font, new Rectangle((int)left, (int)(center - Height / 2f), Math.Max(1, (int)(shape.Right - left - 6 * s)), Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.SingleLine);
        if (Badge.Length > 0)
        {
            var size = 16 * s; var r = new RectangleF(shape.Right - size * 0.8f, shape.Y - size * 0.15f, size, size);
            using var b = new SolidBrush(t.Accent); g.FillEllipse(b, r);
            TextRenderer.DrawText(g, Badge, UiTheme.Caption, Rectangle.Round(r), t.AccentInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { UiMotion.Cancel(this); UiMotion.Cancel(pressTrack); } base.Dispose(disposing); }
}

/// <summary>Segmented control in the Apple style: a glass track with a sliding lighter capsule.</summary>
public sealed class SegmentedPicker : Control
{
    private int selected;
    private float thumb, hover = -1;
    private readonly object track = new();
    public sealed record Option(string Title, string Detail = "", string Tooltip = "");
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)] public List<Option> Options { get; } = [];
    public event EventHandler? SelectedIndexChanged;
    [DefaultValue(0)]
    public int SelectedIndex
    {
        get => selected;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, Options.Count - 1)); if (selected == value) return; var start = thumb; selected = value;
            UiMotion.Run(track, this, 280, p => { thumb = start + (selected - start) * p; Invalidate(); });
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty); AccessibilityNotifyClients(AccessibleEvents.Selection, selected);
        }
    }
    public void SetSilently(int index) { selected = Math.Clamp(index, 0, Math.Max(0, Options.Count - 1)); thumb = selected; Invalidate(); }
    public SegmentedPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.GlassSolid; Font = UiTheme.Strong; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PageTabList; Margin = Padding.Empty;
    }
    private RectangleF Segment(int index, float position = -1)
    {
        var s = DeviceDpi / 96f; var inner = new RectangleF(3 * s, 3 * s, Width - 6 * s, Height - 6 * s); var w = inner.Width / Math.Max(1, Options.Count);
        return new RectangleF(inner.X + (position < 0 ? index : position) * w, inner.Y, w, inner.Height);
    }
    private int HitTest(Point point) { for (var i = 0; i < Options.Count; i++) if (Segment(i).Contains(point)) return i; return -1; }
    protected override void OnMouseMove(MouseEventArgs e) { var hit = HitTest(e.Location); if (hit != (int)hover) { hover = hit; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); var hit = HitTest(e.Location); if (hit >= 0 && e.Button == MouseButtons.Left) SelectedIndex = hit; base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { SelectedIndex--; e.Handled = true; }
        if (e.KeyCode == Keys.Right) { SelectedIndex++; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 8 || Height < 8 || Options.Count == 0) return;
        var t = UiTheme.Current; var s = DeviceDpi / 96f; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var well = new RectangleF(1f, 1f, Width - 2, Height - 2); var radius = Math.Min(18 * s, well.Height / 2);
        using (var path = UiTheme.Rounded(well, radius)) using (var fill = new SolidBrush(Color.FromArgb(46, 0, 0, 0))) g.FillPath(fill, path);
        Glass.Edge(g, well, radius, s, 0.45f);
        var knob = RectangleF.Inflate(Segment(0, thumb), -0.5f * s, -0.5f * s); var knobRadius = Math.Min(15 * s, knob.Height / 2);
        Glass.Shadow(g, knob, knobRadius, 0, 2 * s, 8 * s, Color.FromArgb(70, 0, 0, 0));
        Glass.Pill(g, knob, knobRadius, s, Color.FromArgb(46, UiTheme.Blend(Color.White, t.Accent, 0.35f)));
        var twoLines = Options.Any(o => o.Detail.Length > 0) && Height >= 40 * s;
        for (var i = 0; i < Options.Count; i++)
        {
            var r = Segment(i); var weight = Math.Max(0, 1 - Math.Abs(thumb - i));
            var title = UiTheme.Blend(i == (int)hover ? UiTheme.Blend(t.Muted, t.Text, 0.6f) : t.Muted, t.Text, weight);
            var detail = UiTheme.Blend(UiTheme.WithAlpha(t.Muted, 255), t.Accent, weight);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
            if (twoLines)
            {
                TextRenderer.DrawText(g, Options[i].Title, Font, new Rectangle((int)r.X + 2, (int)(r.Y + r.Height * 0.1f), (int)r.Width - 4, (int)(r.Height * 0.5f)), title, flags);
                TextRenderer.DrawText(g, Options[i].Detail, UiTheme.Small, new Rectangle((int)r.X + 2, (int)(r.Y + r.Height * 0.54f), (int)r.Width - 4, (int)(r.Height * 0.36f)), detail, flags);
            }
            else TextRenderer.DrawText(g, Options[i].Title, Font, Rectangle.Round(r), title, flags);
        }
        if (Focused && ShowFocusCues) { using var path = UiTheme.Rounded(well, radius); using var pen = new Pen(UiTheme.WithAlpha(t.Accent, 150), 1.2f * s); g.DrawPath(pen, path); }
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(track); base.Dispose(disposing); }
}

/// <summary>iOS-style switch row.</summary>
public sealed class ToggleRow : CheckBox
{
    private float position;
    [DefaultValue("")] public string Description { get; set; } = "";
    public ToggleRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        AutoSize = false; BackColor = UiTheme.Current.GlassSolid; Cursor = Cursors.Hand; Margin = Padding.Empty; Font = UiTheme.Body;
    }
    protected override void OnCheckedChanged(EventArgs e)
    {
        var start = position; var end = Checked ? 1f : 0f;
        UiMotion.Run(this, this, 240, p => { position = start + (end - start) * p; Invalidate(); }); base.OnCheckedChanged(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(Width - 48 * s, 6 * s, 44 * s, 24 * s);
        using (var path = UiTheme.Rounded(track, track.Height / 2))
        {
            using var off = new SolidBrush(Color.FromArgb(Enabled ? 54 : 30, 120, 120, 128)); g.FillPath(off, path);
            if (position > 0) { using var on = new SolidBrush(Color.FromArgb((int)(255 * position), Enabled ? t.Online : t.Muted)); g.FillPath(on, path); }
        }
        Glass.Edge(g, track, track.Height / 2, s, 0.4f);
        var knob = new RectangleF(track.X + (2 + 20 * position) * s, track.Y + 2 * s, 20 * s, 20 * s);
        Glass.Knob(g, knob, s);
        var labelWidth = Math.Max(0, Width - (int)(58 * s)); var ink = Enabled ? t.Text : t.Muted;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        if (Description.Length == 0) TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, labelWidth, (int)(36 * s)), ink, flags | TextFormatFlags.VerticalCenter);
        else
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, (int)(5 * s), labelWidth, (int)(22 * s)), ink, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Description, UiTheme.Small, new Rectangle(0, (int)(28 * s), labelWidth, (int)(18 * s)), t.Muted, flags);
        }
        if (Focused && ShowFocusCues) { using var focus = new Pen(UiTheme.WithAlpha(t.Accent, 150)); using var path = UiTheme.Rounded(RectangleF.Inflate(track, 2 * s, 2 * s), track.Height / 2 + 2 * s); g.DrawPath(focus, path); }
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(this); base.Dispose(disposing); }
}

/// <summary>iOS-style slider: thin capsule track, accent fill, white knob.</summary>
public sealed class SlimSlider : Control
{
    private int value;
    private float hover;
    [DefaultValue(0)] public int Minimum { get; set; }
    [DefaultValue(150)] public int Maximum { get; set; } = 150;
    [DefaultValue(5)] public int Step { get; set; } = 5;
    public event EventHandler? ValueChanged;
    [DefaultValue(0)] public int Value
    {
        get => value;
        set { var next = Math.Clamp(value, Minimum, Maximum); if (this.value == next) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }
    public SlimSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.GlassSolid; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.Slider; AccessibleName = "Громкость";
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Down) { Value -= Step; e.Handled = true; }
        if (e.KeyCode is Keys.Right or Keys.Up) { Value += Step; e.Handled = true; }
        if (e.KeyCode == Keys.Home) { Value = Minimum; e.Handled = true; }
        if (e.KeyCode == Keys.End) { Value = Maximum; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { var start = hover; UiMotion.Run(this, this, 160, p => { hover = start + (1 - start) * p; Invalidate(); }); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { var start = hover; UiMotion.Run(this, this, 160, p => { hover = start * (1 - p); Invalidate(); }); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Capture = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (Focused) Value += Math.Sign(e.Delta) * Step; base.OnMouseWheel(e); }
    private void SetFromX(int x)
    {
        var inset = 12 * DeviceDpi / 96f;
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * Math.Clamp((x - inset) / Math.Max(1, Width - 2 * inset), 0, 1));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 8 || Height < 8) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = 12 * s; var y = Height / 2f; var x = inset + (Width - 2 * inset) * (Value - Minimum) / Math.Max(1, Maximum - Minimum);
        var thickness = (4 + hover * 1.5f) * s;
        var groove = new RectangleF(inset - thickness / 2, y - thickness / 2, Width - 2 * inset + thickness, thickness);
        using (var path = UiTheme.Rounded(groove, thickness / 2)) using (var back = new SolidBrush(Color.FromArgb(60, 140, 140, 150))) g.FillPath(back, path);
        if (x > inset)
        {
            var filled = new RectangleF(groove.X, groove.Y, x - groove.X, groove.Height);
            using var path = UiTheme.Rounded(filled, filled.Height / 2);
            using var brush = new LinearGradientBrush(RectangleF.Inflate(filled, 1, 1), t.Accent, UiTheme.Blend(t.Accent, t.Glow, 0.45f), 0f); g.FillPath(brush, path);
        }
        var knobSize = (20 + hover * 2) * s; var knob = new RectangleF(x - knobSize / 2, y - knobSize / 2, knobSize, knobSize);
        Glass.Knob(g, knob, s);
        if (Focused && ShowFocusCues) { using var ring = new Pen(UiTheme.WithAlpha(t.Accent, 170), 1.3f * s); g.DrawEllipse(ring, RectangleF.Inflate(knob, 3 * s, 3 * s)); }
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(this); base.Dispose(disposing); }
}

public sealed class ThemedComboBox : Control
{
    private int selectedIndex = -1;
    private ContextMenuStrip? menu;
    private int lastMouseDown, dismissedAt = int.MinValue;
    private float hover;
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)] public List<object> Items { get; } = [];
    public event EventHandler? SelectedIndexChanged;
    [DefaultValue(-1)] public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (value < -1 || value >= Items.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (selectedIndex == value) { Text = SelectedItem?.ToString() ?? ""; Invalidate(); return; }
            selectedIndex = value; Text = SelectedItem?.ToString() ?? ""; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem => selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : null;
    public ThemedComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.GlassSolid; Font = UiTheme.Body; Size = new Size(150, 36); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.ComboBox;
    }
    protected override void OnClick(EventArgs e)
    {
        Focus();
        if (dismissedAt == lastMouseDown) dismissedAt = int.MinValue;
        else OpenMenu();
        base.OnClick(e);
    }
    protected override void OnMouseEnter(EventArgs e) { UiMotion.Run(this, this, 150, p => { hover = p; Invalidate(); }); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { var start = hover; UiMotion.Run(this, this, 150, p => { hover = start * (1 - p); Invalidate(); }); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { lastMouseDown = GetMessageTime(); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Items.Count > 0)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) { OpenMenu(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { SelectedIndex = Math.Min(Items.Count - 1, SelectedIndex + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { SelectedIndex = Math.Max(0, SelectedIndex - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { SelectedIndex = 0; e.Handled = true; }
            else if (e.KeyCode == Keys.End) { SelectedIndex = Items.Count - 1; e.Handled = true; }
        }
        base.OnKeyDown(e);
    }
    private void OpenMenu()
    {
        if (!Enabled || Items.Count == 0) return;
        if (menu?.Visible == true) { menu.Close(); return; }
        dismissedAt = int.MinValue;
        menu?.Dispose(); var t = UiTheme.Current;
        menu = ThemeMenuRenderer.CreateMenu(Font);
        var currentMenu = menu;
        menu.Closed += (_, e) =>
        {
            UiMotion.Cancel(currentMenu);
            dismissedAt = e.CloseReason == ToolStripDropDownCloseReason.AppClicked && RectangleToScreen(ClientRectangle).Contains(Cursor.Position) ? GetMessageTime() : int.MinValue;
        };
        for (var index = 0; index < Items.Count; index++)
        {
            var choice = index;
            var item = new ToolStripMenuItem(Items[index].ToString()) { AutoSize = false, Width = Math.Max(Width - 8, 150), Height = (int)(34 * DeviceDpi / 96f), ForeColor = index == selectedIndex ? t.Accent : t.Text };
            item.Click += (_, _) => SelectedIndex = choice; menu.Items.Add(item);
        }
        menu.Opacity = UiMotion.Enabled ? 0.02 : 1;
        menu.Show(this, new Point(0, Height + 3));
        UiMotion.Run(currentMenu, this, 160, p => { if (!currentMenu.IsDisposed) currentMenu.Opacity = Math.Max(0.02, p); });
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var shape = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
        Glass.Pill(g, shape, Math.Min(14 * s, shape.Height / 2), s, Color.FromArgb((int)(18 + hover * 10), 255, 255, 255), 0, 0, 0.8f);
        if (Focused && ShowFocusCues) { using var path = UiTheme.Rounded(shape, Math.Min(14 * s, shape.Height / 2)); using var pen = new Pen(UiTheme.WithAlpha(t.Accent, 160), 1.2f * s); g.DrawPath(pen, path); }
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, new Rectangle((int)(13 * s), 0, Math.Max(0, Width - (int)(42 * s)), Height), Enabled ? t.Text : t.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        UiIcons.Draw(g, UiIcon.Chevron, new RectangleF(Width - 28 * s, Height / 2f - 6 * s, 12 * s, 12 * s), UiTheme.Blend(t.Muted, t.Text, hover));
    }
    protected override void Dispose(bool disposing) { if (disposing) { menu?.Dispose(); UiMotion.Cancel(this); } base.Dispose(disposing); }
}

public sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemeMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
    public static Color MenuBack => UiTheme.Blend(UiTheme.Current.Background, UiTheme.Current.Surface, 0.07f);
    public static ContextMenuStrip CreateMenu(Font? font = null)
    {
        var t = UiTheme.Current;
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false, BackColor = MenuBack, ForeColor = t.Text, Font = font ?? UiTheme.Body, Renderer = new ThemeMenuRenderer(), Padding = new Padding(6), DropShadowEnabled = false };
        menu.HandleCreated += (_, _) => WindowTheme.RoundPopup(menu.Handle);
        return menu;
    }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MenuBack); e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var t = UiTheme.Current; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(MenuBack)) g.FillRectangle(back, new Rectangle(Point.Empty, e.Item.Size));
        if (!e.Item.Selected || !e.Item.Enabled) return;
        using var path = UiTheme.Rounded(new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 8);
        using var brush = new SolidBrush(UiTheme.WithAlpha(t.Accent, 40)); g.FillPath(brush, path);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? UiTheme.Current.Muted : e.Item.ForeColor == SystemColors.ControlText ? UiTheme.Current.Text : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(UiTheme.WithAlpha(UiTheme.Current.Border, 200)); var y = e.Item.Height / 2; e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    { using var border = new Pen(UiTheme.Current.Border); e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBack;
        public override Color ImageMarginGradientBegin => MenuBack;
        public override Color ImageMarginGradientMiddle => MenuBack;
        public override Color ImageMarginGradientEnd => MenuBack;
        public override Color MenuBorder => UiTheme.Current.Border;
        public override Color MenuItemBorder => Color.Transparent;
    }
}

/// <summary>App mark: a flat continuous-corner tile with the accent gradient and white level bars.</summary>
public sealed class BrandMark : Control
{
    public BrandMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.GlassSolid; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 6 || Height < 6) return;
        var t = UiTheme.Current; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var s = DeviceDpi / 96f;
        var side = Math.Min(Width, Height) - 6 * s; var r = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
        using var path = UiTheme.Rounded(r, r.Width * 0.32f);
        using (var fill = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), t.Accent, t.Glow, 50f)) g.FillPath(fill, path);
        Glass.Edge(g, r, r.Width * 0.32f, s, 0.8f);
        using var pen = new Pen(Color.FromArgb(245, 255, 255, 255), r.Width * 0.085f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var heights = new[] { 0.24f, 0.48f, 0.66f, 0.36f };
        for (var i = 0; i < 4; i++) { var x = r.X + r.Width * (0.27f + i * 0.155f); g.DrawLine(pen, x, r.Y + r.Height * (1 - heights[i]) / 2, x, r.Y + r.Height * (1 + heights[i]) / 2); }
    }
}
