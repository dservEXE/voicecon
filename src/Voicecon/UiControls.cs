using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Voicecon;

/// <summary>
/// Neumorphic card (raised out of the canvas) or an inset well for inputs. Its face is flat and every child paints that
/// same face, so there are never visible rectangles inside. The shadow lives in the gutter around the shape.
/// </summary>
public sealed class SurfacePanel : Panel, ISurface
{
    [DefaultValue(false)] public bool Recessed { get; set; }
    /// <summary>Space around the shape for its shadow; -1 picks exactly what the elevation needs.</summary>
    [DefaultValue(-1)] public int Gutter { get; set; } = -1;
    [DefaultValue(22)] public int Radius { get; set; } = 22;
    [DefaultValue(1f)] public float Elevation { get; set; } = 1;
    /// <summary>Depth of an inset well.</summary>
    [DefaultValue(0.8f)] public float InsetDepth { get; set; } = 0.8f;
    /// <summary>Optional colour mixed into the face (incoming call, update banner). Alpha is the strength.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Wash { get; set; }
    /// <summary>Explicit face colour (popups); null uses the theme surface.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? FaceColor { get; set; }
    public Color Face
    {
        get
        {
            var t = UiTheme.Current; var face = Recessed ? t.Field : FaceColor ?? t.Surface;
            return Wash is { } wash ? UiTheme.Blend(face, wash, wash.A / 255f) : face;
        }
    }
    public SurfacePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.Surface; Margin = Padding.Empty;
    }
    private float S => DeviceDpi / 96f;
    public int GutterPixels => Recessed ? 0 : Gutter >= 0 ? (int)Math.Round(Gutter * S) : Neu.Extent(Elevation, S);
    public override Rectangle DisplayRectangle
    {
        get { var r = base.DisplayRectangle; var g = GutterPixels; return new Rectangle(r.X + g, r.Y + g, Math.Max(0, r.Width - 2 * g), Math.Max(0, r.Height - 2 * g)); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var s = S; var g = GutterPixels; var graphics = e.Graphics;
        var shape = new RectangleF(g, g, Width - 2 * g, Height - 2 * g);
        var radius = Math.Min(Radius * s, Math.Min(shape.Width, shape.Height) / 2);
        if (Recessed) Neu.Inset(graphics, shape, radius, s, InsetDepth, Face);
        else Neu.Raised(graphics, shape, radius, s, Elevation, Face, rim: Elevation > 0 ? 1 : 0);
        base.OnPaint(e);
    }
}

public enum ButtonTone { Secondary, Primary, Quiet, Danger, Live }

/// <summary>
/// Neumorphic button: raised at rest, pressed into the surface while held. Coloured tones (call, hang up, live) keep the
/// same soft shadows with a tinted face. Quiet buttons are flat until hovered. <see cref="Selected"/> stays pressed in.
/// </summary>
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
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = UiTheme.Current.Surface; Cursor = Cursors.Hand;
        Font = UiTheme.Strong; Margin = Padding.Empty; UseVisualStyleBackColor = false;
    }
    protected override void OnMouseEnter(EventArgs e) { AnimateHover(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { AnimateHover(0); AnimatePress(0); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) AnimatePress(1); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { AnimatePress(0); base.OnMouseUp(e); }
    private void AnimateHover(float target) { var start = hoverAmount; UiMotion.Run(this, this, 180, p => { hoverAmount = start + (target - start) * p; Invalidate(); }); }
    private void AnimatePress(float target) { var start = pressAmount; UiMotion.Run(pressTrack, this, 120, p => { pressAmount = start + (target - start) * p; Invalidate(); }); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    private Color ToneColor(ThemePalette t) => Tone switch { ButtonTone.Primary => t.Accent, ButtonTone.Danger => t.Danger, ButtonTone.Live => LiveColor, _ => t.Surface };
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var t = UiTheme.Current; var s = DeviceDpi / 96f; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var gutter = Neu.Extent(Neu.ButtonDepth, s);
        var side = Math.Min(Width, Height) - 2 * gutter;
        var shape = Circle ? new RectangleF((Width - side) / 2, (Height - side) / 2, side, side) : new RectangleF(gutter, gutter, Width - 2 * gutter, Height - 2 * gutter);
        if (shape.Width < 4 || shape.Height < 4) return;
        var radius = Circle ? shape.Width / 2 : Math.Min(Radius * s, shape.Height / 2);
        var colored = Tone is ButtonTone.Primary or ButtonTone.Danger or ButtonTone.Live;
        var face = ToneColor(t);
        Color ink;
        // Pressed (held, or a latched toggle such as a muted microphone) sinks into the surface; otherwise it is raised.
        var pressed = Selected ? 1 : pressAmount;
        if (!Enabled)
        {
            ink = UiTheme.WithAlpha(t.Muted, 130);
            if (Tone != ButtonTone.Quiet) Neu.Raised(g, shape, radius, s, Neu.ButtonDepth * 0.5f, colored ? UiTheme.Blend(t.Surface, face, 0.28f) : t.Surface, rim: 0.5f);
        }
        else if (pressed > 0.5f)
        {
            ink = Selected ? UiTheme.Blend(t.Danger, Color.White, 0.2f) : colored ? UiTheme.Blend(face, Color.White, 0.15f) : t.Accent;
            Neu.Inset(g, shape, radius, s, 0.7f, colored && !Selected ? UiTheme.Blend(t.Field, face, 0.22f) : t.Field);
        }
        else if (colored)
        {
            ink = Tone == ButtonTone.Primary ? t.AccentInk : Color.FromArgb(255, 34, 14, 10);
            var lift = UiTheme.Blend(face, Color.White, 0.08f * hoverAmount);
            Neu.Raised(g, shape, radius, s, Neu.ButtonDepth * (1 - pressed * 0.8f), lift, convex: 1, rim: 1.4f);
        }
        else if (Tone == ButtonTone.Quiet)
        {
            ink = UiTheme.Blend(t.Muted, t.Text, 0.3f + hoverAmount * 0.7f);
            if (hoverAmount > 0.02f || pressed > 0.02f) Neu.Raised(g, shape, radius, s, Neu.ButtonDepth * hoverAmount * (1 - pressed), t.Surface, convex: hoverAmount * 0.6f, rim: hoverAmount);
        }
        else
        {
            ink = UiTheme.Blend(t.Text, t.Accent, hoverAmount * 0.35f);
            Neu.Raised(g, shape, radius, s, Neu.ButtonDepth * (1 - pressed * 0.8f), t.Surface, convex: 0.6f + hoverAmount * 0.4f, rim: 1 + hoverAmount * 0.4f);
        }
        if (Focused && ShowFocusCues) Neu.Focus(g, shape, radius, s);
        var icon = Glyph != UiIcon.None; var text = Text.Length > 0 && !Circle;
        var iconSize = Circle ? Math.Min(shape.Width * 0.46f, 20 * s) : 16 * s; var gap = 8 * s;
        var textWidth = text ? TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
        var groupWidth = Math.Min(shape.Width - 16 * s, textWidth + (icon ? iconSize + (text ? gap : 0) : 0));
        var left = shape.X + (shape.Width - groupWidth) / 2; var center = shape.Y + shape.Height / 2;
        if (icon) { UiIcons.Draw(g, Glyph, new RectangleF(left, center - iconSize / 2, iconSize, iconSize), ink, Crossed); left += iconSize + gap; }
        if (text) TextRenderer.DrawText(g, Text, Font, new Rectangle((int)left, (int)(center - Height / 2f), Math.Max(1, (int)(shape.Right - left - 6 * s)), Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.SingleLine);
        if (Badge.Length > 0)
        {
            var size = 16 * s; var r = new RectangleF(Math.Min(shape.Right - size * 0.7f, Width - size - 1), Math.Max(1, shape.Y - size * 0.2f), size, size);
            using var b = new SolidBrush(t.Accent); g.FillEllipse(b, r);
            TextRenderer.DrawText(g, Badge, UiTheme.Caption, Rectangle.Round(r), t.AccentInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { UiMotion.Cancel(this); UiMotion.Cancel(pressTrack); } base.Dispose(disposing); }
}

/// <summary>Segmented control: an inset track with a raised capsule that slides to the chosen option.</summary>
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
        BackColor = UiTheme.Current.Surface; Font = UiTheme.Strong; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PageTabList; Margin = Padding.Empty;
    }
    private float S => DeviceDpi / 96f;
    private RectangleF Well => new(2 * S, 2 * S, Width - 4 * S, Height - 4 * S);
    private RectangleF Segment(int index, float position = -1)
    {
        var s = S; var inner = RectangleF.Inflate(Well, -5 * s, -5 * s); var w = inner.Width / Math.Max(1, Options.Count);
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
        if (Width < 16 || Height < 16 || Options.Count == 0) return;
        var t = UiTheme.Current; var s = S; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var well = Well; var radius = Math.Min(20 * s, well.Height / 2);
        Neu.Inset(g, well, radius, s, 0.9f);
        var knob = Segment(0, thumb); var knobRadius = Math.Min(16 * s, knob.Height / 2);
        Neu.Raised(g, knob, knobRadius, s, Neu.SmallDepth, t.Surface, convex: 0.7f);
        var twoLines = Options.Any(o => o.Detail.Length > 0) && Height >= 40 * s;
        for (var i = 0; i < Options.Count; i++)
        {
            var r = Segment(i); var weight = Math.Max(0, 1 - Math.Abs(thumb - i));
            var title = UiTheme.Blend(i == (int)hover ? UiTheme.Blend(t.Muted, t.Text, 0.6f) : t.Muted, t.Text, weight);
            var detail = UiTheme.Blend(t.Muted, t.Accent, weight);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
            if (twoLines)
            {
                TextRenderer.DrawText(g, Options[i].Title, Font, new Rectangle((int)r.X + 2, (int)(r.Y + r.Height * 0.06f), (int)r.Width - 4, (int)(r.Height * 0.52f)), title, flags);
                TextRenderer.DrawText(g, Options[i].Detail, UiTheme.Small, new Rectangle((int)r.X + 2, (int)(r.Y + r.Height * 0.54f), (int)r.Width - 4, (int)(r.Height * 0.38f)), detail, flags);
            }
            else TextRenderer.DrawText(g, Options[i].Title, Font, Rectangle.Round(r), title, flags);
        }
        if (Focused && ShowFocusCues) Neu.Focus(g, RectangleF.Inflate(well, -1.5f * s, -1.5f * s), radius, s);
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(track); base.Dispose(disposing); }
}

/// <summary>Switch row: an inset track that fills with green, and a raised knob that slides across.</summary>
public sealed class ToggleRow : CheckBox
{
    private float position;
    [DefaultValue("")] public string Description { get; set; } = "";
    public ToggleRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        AutoSize = false; BackColor = UiTheme.Current.Surface; Cursor = Cursors.Hand; Margin = Padding.Empty; Font = UiTheme.Body;
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
        var track = new RectangleF(Width - 56 * s, 7 * s, 48 * s, 26 * s);
        var on = Enabled ? t.Online : t.Muted;
        Neu.Inset(g, track, track.Height / 2, s, 0.8f, UiTheme.Blend(t.Field, UiTheme.Blend(on, t.Field, 0.25f), position));
        var knob = new RectangleF(track.X + (3 + 22 * position) * s, track.Y + 3 * s, 20 * s, 20 * s);
        Neu.Knob(g, knob, s);
        var labelWidth = Math.Max(0, Width - (int)(66 * s)); var ink = Enabled ? t.Text : t.Muted;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        if (Description.Length == 0) TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, labelWidth, (int)(40 * s)), ink, flags | TextFormatFlags.VerticalCenter);
        else
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, (int)(5 * s), labelWidth, (int)(22 * s)), ink, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Description, UiTheme.Small, new Rectangle(0, (int)(28 * s), labelWidth, (int)(18 * s)), t.Muted, flags);
        }
        if (Focused && ShowFocusCues) Neu.Focus(g, track, track.Height / 2, s);
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(this); base.Dispose(disposing); }
}

/// <summary>Slider: a thin inset groove filled with the accent and a raised knob.</summary>
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
        BackColor = UiTheme.Current.Surface; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.Slider; AccessibleName = "Громкость";
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
    private float Inset => 14 * DeviceDpi / 96f;
    private void SetFromX(int x) => Value = Minimum + (int)Math.Round((Maximum - Minimum) * Math.Clamp((x - Inset) / Math.Max(1, Width - 2 * Inset), 0, 1));
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 8 || Height < 8) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = Inset; var y = Height / 2f; var x = inset + (Width - 2 * inset) * (Value - Minimum) / Math.Max(1, Maximum - Minimum);
        var thickness = 10 * s;
        var groove = new RectangleF(inset - thickness / 2, y - thickness / 2, Width - 2 * inset + thickness, thickness);
        Neu.Inset(g, groove, thickness / 2, s, 0.55f);
        if (x > inset)
        {
            var filled = RectangleF.Inflate(new RectangleF(groove.X, groove.Y, x - groove.X + thickness / 2, groove.Height), -2 * s, -2 * s);
            using var path = UiTheme.Rounded(filled, filled.Height / 2);
            using var brush = new LinearGradientBrush(RectangleF.Inflate(filled, 1, 1), UiTheme.Blend(t.Accent, t.Field, 0.15f), UiTheme.Blend(t.Accent, t.Glow, 0.35f), 0f); g.FillPath(brush, path);
        }
        var knobSize = (20 + hover * 2) * s; var knob = new RectangleF(x - knobSize / 2, y - knobSize / 2, knobSize, knobSize);
        Neu.Knob(g, knob, s);
        using (var dot = new SolidBrush(UiTheme.WithAlpha(t.Accent, (int)(150 + 100 * hover)))) g.FillEllipse(dot, x - 3 * s, y - 3 * s, 6 * s, 6 * s);
        if (Focused && ShowFocusCues) Neu.Focus(g, knob, knobSize / 2, s);
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(this); base.Dispose(disposing); }
}

/// <summary>Drop-down: a raised capsule with the chosen item; the list opens as a rounded themed menu.</summary>
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
        BackColor = UiTheme.Current.Surface; Font = UiTheme.Body; Size = new Size(150, 40); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.ComboBox;
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
        menu.Show(this, new Point(0, Height));
        UiMotion.Run(currentMenu, this, 160, p => { if (!currentMenu.IsDisposed) currentMenu.Opacity = Math.Max(0.02, p); });
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4 || Height < 4) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var gutter = Neu.Extent(Neu.ButtonDepth, s);
        var shape = new RectangleF(gutter, gutter, Width - 2 * gutter, Height - 2 * gutter); var radius = Math.Min(14 * s, shape.Height / 2);
        if (shape.Width < 4 || shape.Height < 4) return;
        Neu.Raised(g, shape, radius, s, Neu.ButtonDepth, t.Surface, convex: 0.5f + hover * 0.4f, rim: 1 + hover * 0.3f);
        if (Focused && ShowFocusCues) Neu.Focus(g, shape, radius, s);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, new Rectangle((int)(shape.X + 13 * s), 0, Math.Max(0, (int)(shape.Width - 42 * s)), Height), Enabled ? t.Text : t.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        UiIcons.Draw(g, UiIcon.Chevron, new RectangleF(shape.Right - 26 * s, Height / 2f - 6 * s, 12 * s, 12 * s), UiTheme.Blend(t.Muted, t.Accent, hover));
    }
    protected override void Dispose(bool disposing) { if (disposing) { menu?.Dispose(); UiMotion.Cancel(this); } base.Dispose(disposing); }
}

public sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemeMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
    /// <summary>Popups are a touch lighter than the canvas so they read as floating above it.</summary>
    public static Color MenuBack => UiTheme.Blend(UiTheme.Current.Surface, Color.White, 0.035f);
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
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(MenuBack)) g.FillRectangle(back, new Rectangle(Point.Empty, e.Item.Size));
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Neu.Inset(g, new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 10, 1, 0.55f, UiTheme.Blend(MenuBack, Color.Black, 0.12f));
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? UiTheme.Current.Muted : e.Item.ForeColor == SystemColors.ControlText ? UiTheme.Current.Text : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var dark = new Pen(UiTheme.Blend(MenuBack, Color.Black, 0.3f)); e.Graphics.DrawLine(dark, 8, y, e.Item.Width - 8, y);
        using var light = new Pen(UiTheme.Blend(MenuBack, Color.White, 0.06f)); e.Graphics.DrawLine(light, 8, y + 1, e.Item.Width - 8, y + 1);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    { using var border = new Pen(UiTheme.Blend(MenuBack, Color.White, 0.06f)); e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBack;
        public override Color ImageMarginGradientBegin => MenuBack;
        public override Color ImageMarginGradientMiddle => MenuBack;
        public override Color ImageMarginGradientEnd => MenuBack;
        public override Color MenuBorder => MenuBack;
        public override Color MenuItemBorder => Color.Transparent;
    }
}

/// <summary>App mark: a raised accent tile with white level bars.</summary>
public sealed class BrandMark : Control
{
    public BrandMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 6 || Height < 6) return;
        var t = UiTheme.Current; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var s = DeviceDpi / 96f;
        var side = Math.Min(Width, Height) - 2 * Neu.Extent(Neu.SmallDepth, s); var r = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
        if (side < 6) return;
        Neu.Raised(g, r, r.Width * 0.32f, s, Neu.SmallDepth, t.Accent, convex: 1, rim: 1.3f);
        using (var path = UiTheme.Rounded(r, r.Width * 0.32f)) using (var glow = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb(0, t.Glow), Color.FromArgb(150, t.Glow), 45f)) g.FillPath(glow, path);
        using var pen = new Pen(Color.FromArgb(245, 255, 255, 255), r.Width * 0.085f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var heights = new[] { 0.24f, 0.48f, 0.66f, 0.36f };
        for (var i = 0; i < 4; i++) { var x = r.X + r.Width * (0.27f + i * 0.155f); g.DrawLine(pen, x, r.Y + r.Height * (1 - heights[i]) / 2, x, r.Y + r.Height * (1 + heights[i]) / 2); }
    }
}
