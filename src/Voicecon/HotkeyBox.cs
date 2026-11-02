using System.ComponentModel;
namespace Voicecon;
public sealed class HotkeyBox : Control
{
    private Keys chord, previous;
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 40 };
    private bool capturing; private long started;
    public event EventHandler? Captured, CaptureStarted, CaptureEnded;
    [DefaultValue(Keys.None)] public Keys Chord { get => chord; set { chord = value; Invalidate(); } }
    [Browsable(false)] public bool Capturing => capturing;
    public HotkeyBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand; TabStop = true; pulse.Tick += (_, _) => Invalidate();
        var menu = new ContextMenuStrip { Renderer = new ThemeMenuRenderer() };
        menu.Items.Add("Убрать назначение", null, (_, _) => { CancelCapture(); Chord = Keys.None; Captured?.Invoke(this, EventArgs.Empty); }); ContextMenuStrip = menu;
    }
    public static string Format(Keys key)
    {
        var code = key & Keys.KeyCode;
        var button = code switch { Keys.MButton => "MOUSE3", Keys.XButton1 => "MOUSE4", Keys.XButton2 => "MOUSE5", _ => null };
        return key == Keys.None ? "Не назначено" : button is null ? new KeysConverter().ConvertToString(key) ?? "" : (key.HasFlag(Keys.Control) ? "Ctrl + " : "") + (key.HasFlag(Keys.Alt) ? "Alt + " : "") + (key.HasFlag(Keys.Shift) ? "Shift + " : "") + button;
    }
    public static bool Valid(Keys key) => key == Keys.None || ((key & Keys.KeyCode) is not (Keys.None or Keys.LButton or Keys.RButton or Keys.Escape or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin) && (int)(key & Keys.KeyCode) <= 254);
    public void BeginCapture()
    {
        if (capturing) return; Focus(); previous = chord; capturing = true; started = Environment.TickCount64;
        CaptureStarted?.Invoke(this, EventArgs.Empty); if (UiMotion.Enabled) pulse.Start(); Invalidate();
    }
    public void CancelCapture() { if (!capturing) return; Chord = previous; EndCapture(); }
    private void EndCapture() { capturing = false; pulse.Stop(); Invalidate(); CaptureEnded?.Invoke(this, EventArgs.Empty); }
    private void Assign(Keys key) { if (!Valid(key) || key == Keys.None) return; Chord = key; Captured?.Invoke(this, EventArgs.Empty); EndCapture(); }
    protected override bool IsInputKey(Keys keyData) => true;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = e.Handled = true;
        if (!capturing) { if (e.KeyCode is Keys.Enter or Keys.Space) BeginCapture(); return; }
        if (e.KeyCode == Keys.Escape) CancelCapture(); else Assign(e.KeyData);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!capturing) { if (e.Button == MouseButtons.Left) BeginCapture(); return; }
        var key = e.Button switch { MouseButtons.Middle => Keys.MButton, MouseButtons.XButton1 => Keys.XButton1, MouseButtons.XButton2 => Keys.XButton2, _ => Keys.None };
        if (key != Keys.None) Assign(key | ModifierKeys);
    }
    protected override void OnLostFocus(EventArgs e) { CancelCapture(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var t = UiTheme.Current; UiPainting.Fill(e.Graphics, ClientRectangle, Parent?.BackColor ?? t.Surface);
        var p = capturing ? (float)(0.5 + 0.5 * Math.Sin((Environment.TickCount64 - started) / 180.0)) : 0;
        using var path = UiTheme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10);
        using var fill = new SolidBrush(capturing ? UiTheme.Blend(t.Field, t.Accent, 0.08f + p * 0.1f) : t.Field); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; e.Graphics.FillPath(fill, path);
        using var pen = new Pen(capturing ? UiTheme.Blend(t.Border, t.Accent, 0.5f + p * 0.5f) : Focused ? t.Accent : t.Border); e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, capturing ? "Нажмите клавишу · Esc отмена" : Format(chord), UiTheme.Small, new Rectangle(10, 0, Width - 16, Height), capturing ? t.Accent : t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    protected override void Dispose(bool disposing) { if (disposing) { pulse.Dispose(); ContextMenuStrip?.Dispose(); } base.Dispose(disposing); }
}
