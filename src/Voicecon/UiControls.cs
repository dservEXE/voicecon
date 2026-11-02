using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Voicecon;

public sealed class SurfacePanel : Panel
{
    [DefaultValue(false)] public bool Recessed { get; set; }
    public SurfacePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.Surface; Margin = Padding.Empty;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        UiPainting.Fill(e.Graphics, ClientRectangle, Parent?.BackColor ?? UiTheme.Current.Background);
        if (Width < 2 || Height < 2) return;
        var theme = UiTheme.Current; var scale = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 16 * scale);
        using var brush = new SolidBrush(Recessed ? theme.Field : theme.Surface); using var border = new Pen(theme.Border);
        e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(border, path);
        if (theme.Ornate && !Recessed)
        {
            using var inner = UiTheme.Rounded(new RectangleF(4.5f, 4.5f, Width - 9, Height - 9), 12 * scale);
            using var gold = new Pen(UiTheme.Blend(theme.Border, theme.Accent, 0.4f)); e.Graphics.DrawPath(gold, inner);
            using var fill = new SolidBrush(theme.Accent);
            foreach (var x in new[] { 20f * scale, Width - 20f * scale })
                e.Graphics.FillPolygon(fill, [new PointF(x, 11 * scale), new(x + 3 * scale, 14 * scale), new(x, 17 * scale), new(x - 3 * scale, 14 * scale)]);
        }
        base.OnPaint(e);
    }
}

public enum ButtonTone { Secondary, Primary, Quiet, Danger }

public sealed class SoftButton : Button
{
    [DefaultValue(ButtonTone.Secondary)] public ButtonTone Tone { get; set; }
    [DefaultValue(UiIcon.None)] public UiIcon Glyph { get; set; }
    [DefaultValue(false)] public bool Selected { get; set; }
    [DefaultValue(false)] public bool Crossed { get; set; }
    private float hoverAmount, pressAmount;
    private readonly object pressTrack = new();
    public SoftButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = UiTheme.Current.Surface; Cursor = Cursors.Hand;
        Font = UiTheme.Strong; Margin = Padding.Empty; UseVisualStyleBackColor = false;
    }
    protected override void OnMouseEnter(EventArgs e) { AnimateHover(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { AnimateHover(0); AnimatePress(0); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { AnimatePress(1); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { AnimatePress(0); base.OnMouseUp(e); }
    private void AnimateHover(float target) { var start = hoverAmount; UiMotion.Run(this, this, 150, p => { hoverAmount = start + (target - start) * p; Invalidate(); }); }
    private void AnimatePress(float target) { var start = pressAmount; UiMotion.Run(pressTrack, this, 110, p => { pressAmount = start + (target - start) * p; Invalidate(); }); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
        if (Width < 2 || Height < 2) return;
        var t = UiTheme.Current; var scale = DeviceDpi / 96f; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = Tone switch { ButtonTone.Primary => t.Accent, ButtonTone.Danger => UiTheme.Blend(t.Surface, t.Danger, 0.12f), ButtonTone.Quiet => t.Surface, _ => t.Field };
        var ink = Tone switch { ButtonTone.Primary => t.AccentInk, ButtonTone.Danger => t.Danger, _ => t.Text };
        if (Selected) { fill = UiTheme.Blend(t.Surface, t.Danger, 0.15f); ink = t.Danger; }
        if (!Enabled) { fill = UiTheme.Blend(t.Surface, t.Field, 0.5f); ink = UiTheme.Blend(t.Muted, t.Surface, 0.35f); }
        else fill = UiTheme.Blend(fill, Tone == ButtonTone.Primary ? t.AccentInk : t.Accent, hoverAmount * 0.09f + pressAmount * 0.07f);
        var inset = 0.5f + pressAmount * 1.5f * scale;
        using var path = UiTheme.Rounded(new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset), 10 * scale);
        using var brush = new SolidBrush(fill); g.FillPath(brush, path);
        using var border = new Pen(Focused && ShowFocusCues ? t.Accent : t.Border); if (Tone != ButtonTone.Primary) g.DrawPath(border, path);
        if (t.Ornate && Tone == ButtonTone.Primary && Enabled)
        {
            using var gold = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), Color.FromArgb(255, 228, 149), t.Accent, 25f);
            g.FillPath(gold, path);
        }
        var icon = Glyph != UiIcon.None; var text = Text.Length > 0;
        var iconSize = 19 * scale; var gap = 8 * scale;
        var textWidth = text ? TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform).Width : 0;
        var groupWidth = Math.Min(Width - 16 * scale, textWidth + (icon ? iconSize + (text ? gap : 0) : 0));
        var left = (Width - groupWidth) / 2;
        if (icon) { UiIcons.Draw(g, Glyph, new RectangleF(left, (Height - iconSize) / 2, iconSize, iconSize), ink, Crossed); left += iconSize + gap; }
        if (text) TextRenderer.DrawText(g, Text, Font, new Rectangle((int)left, 0, Math.Max(1, Width - (int)left - (int)(8 * scale)), Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.SingleLine);
    }
    protected override void Dispose(bool disposing) { if (disposing) { UiMotion.Cancel(this); UiMotion.Cancel(pressTrack); } base.Dispose(disposing); }
}

public sealed class ToggleRow : CheckBox
{
    private float position;
    [DefaultValue("")] public string Description { get; set; } = "";
    public ToggleRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.Surface; Cursor = Cursors.Hand; Margin = Padding.Empty; Font = UiTheme.Body;
    }
    protected override void OnCheckedChanged(EventArgs e)
    {
        var start = position; var end = Checked ? 1f : 0f;
        UiMotion.Run(this, this, 160, p => { position = start + (end - start) * p; Invalidate(); }); base.OnCheckedChanged(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
        if (Width < 2 || Height < 2) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(Width - 38 * s, (Height - 20 * s) / 2, 36 * s, 20 * s);
        using var path = UiTheme.Rounded(track, 10 * s); using var brush = new SolidBrush(UiTheme.Blend(t.Border, t.Accent, position)); g.FillPath(brush, path);
        using var knob = new SolidBrush(UiTheme.Blend(t.Muted, t.AccentInk, position)); g.FillEllipse(knob, track.X + (3 + 16 * position) * s, track.Y + 3 * s, 14 * s, 14 * s);
        var labelWidth = Math.Max(0, Width - (int)(52 * s));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, (int)(5 * s), labelWidth, (int)(22 * s)), t.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        TextRenderer.DrawText(g, Description, UiTheme.Small, new Rectangle(0, (int)(28 * s), labelWidth, (int)(18 * s)), t.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        if (Focused && ShowFocusCues) { using var focus = new Pen(t.Accent); g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1); }
    }
    protected override void Dispose(bool disposing) { if (disposing) UiMotion.Cancel(this); base.Dispose(disposing); }
}

public sealed class SlimSlider : Control
{
    private int value;
    [DefaultValue(0)] public int Minimum { get; set; }
    [DefaultValue(150)] public int Maximum { get; set; } = 150;
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
        if (e.KeyCode is Keys.Left or Keys.Down) { Value -= 5; e.Handled = true; }
        if (e.KeyCode is Keys.Right or Keys.Up) { Value += 5; e.Handled = true; }
        if (e.KeyCode == Keys.Home) { Value = Minimum; e.Handled = true; }
        if (e.KeyCode == Keys.End) { Value = Maximum; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Capture = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
    private void SetFromX(int x)
    {
        var inset = 8 * DeviceDpi / 96f;
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * Math.Clamp((x - inset) / Math.Max(1, Width - 2 * inset), 0, 1));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
        if (Width < 2 || Height < 2) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var x = 8 * s + (Width - 16 * s) * (Value - Minimum) / Math.Max(1, Maximum - Minimum); var y = Height / 2f;
        using var rail = new Pen(t.Border, 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(rail, 8 * s, y, Width - 8 * s, y);
        using var filled = new Pen(t.Accent, 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round }; if (x > 8 * s) g.DrawLine(filled, 8 * s, y, x, y);
        using var thumb = new SolidBrush(t.Accent); g.FillEllipse(thumb, x - 6 * s, y - 6 * s, 12 * s, 12 * s);
        if (Focused && ShowFocusCues) { using var ring = new Pen(t.Accent); g.DrawEllipse(ring, x - 9 * s, y - 9 * s, 18 * s, 18 * s); }
    }
}

public sealed class ThemedComboBox : Control
{
    private int selectedIndex = -1;
    private ContextMenuStrip? menu;
    private int lastMouseDown, dismissedAt = int.MinValue;
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
        BackColor = UiTheme.Current.Surface; Font = UiTheme.Body; Size = new Size(150, 34); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.ComboBox;
    }
    protected override void OnClick(EventArgs e)
    {
        Focus();
        if (dismissedAt == lastMouseDown) dismissedAt = int.MinValue;
        else OpenMenu();
        base.OnClick(e);
    }
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
        menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false, BackColor = t.Field, ForeColor = t.Text, Font = Font, Renderer = new ThemeMenuRenderer(), Padding = new Padding(4) };
        var currentMenu = menu;
        menu.Closed += (_, e) =>
        {
            UiMotion.Cancel(currentMenu);
            dismissedAt = e.CloseReason == ToolStripDropDownCloseReason.AppClicked && RectangleToScreen(ClientRectangle).Contains(Cursor.Position) ? GetMessageTime() : int.MinValue;
        };
        for (var index = 0; index < Items.Count; index++)
        {
            var choice = index;
            var item = new ToolStripMenuItem(Items[index].ToString()) { AutoSize = false, Width = Math.Max(Width - 8, 150), Height = (int)(34 * DeviceDpi / 96f), ForeColor = t.Text };
            item.Click += (_, _) => SelectedIndex = choice; menu.Items.Add(item);
        }
        menu.Opacity = UiMotion.Enabled ? 0.02 : 1;
        menu.Show(this, new Point(0, Height + 3));
        UiMotion.Run(currentMenu, this, 150, p => { if (!currentMenu.IsDisposed) currentMenu.Opacity = Math.Max(0.02, p); });
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
        if (Width < 2 || Height < 2) return;
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9 * s);
        using var fill = new SolidBrush(t.Field); g.FillPath(fill, path);
        using var border = new Pen(Focused && ShowFocusCues ? t.Accent : t.Border); g.DrawPath(border, path);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, new Rectangle((int)(11 * s), 0, Math.Max(0, Width - (int)(40 * s)), Height), Enabled ? t.Text : t.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        using var arrow = new Pen(t.Muted, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = Width - 18 * s; var y = Height / 2f;
        g.DrawLines(arrow, [new PointF(x - 4 * s, y - 2 * s), new(x, y + 2 * s), new(x + 4 * s, y - 2 * s)]);
    }
    protected override void Dispose(bool disposing) { if (disposing) menu?.Dispose(); base.Dispose(disposing); }
}

public sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var t = UiTheme.Current; using var brush = new SolidBrush(e.Item.Selected ? UiTheme.Blend(t.Field, t.Accent, 0.14f) : t.Field);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    { using var border = new Pen(UiTheme.Current.Border); e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
}

public sealed class BrandMark : Control
{
    public BrandMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        UiPainting.Fill(e.Graphics, ClientRectangle, BackColor);
        if (Width < 2 || Height < 2) return;
        var t = UiTheme.Current; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Width * 0.28f);
        using var fill = new SolidBrush(t.Accent); g.FillPath(fill, path);
        using var pen = new Pen(t.AccentInk, Width * 0.073f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var heights = new[] { 0.24f, 0.48f, 0.68f, 0.38f };
        for (var i = 0; i < 4; i++) { var x = Width * (0.27f + i * 0.155f); g.DrawLine(pen, x, Height * (1 - heights[i]) / 2, x, Height * (1 + heights[i]) / 2); }
    }
}
