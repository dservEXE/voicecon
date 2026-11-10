using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Voicecon;

public sealed class ModernScrollBar : Control
{
    private int value, maximum, page = 1;
    private bool dragging, hovered;
    private int dragOffset;
    public event EventHandler? ValueChanged;
    [DefaultValue(0)] public int Value { get => value; set { var next = Math.Clamp(value, 0, maximum); if (this.value == next) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public ModernScrollBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); Width = 13; TabStop = false; Cursor = Cursors.Hand; }
    public void SetRange(int extent, int viewport, int position)
    { maximum = Math.Max(0, extent - viewport); page = Math.Max(1, viewport); Value = position; Invalidate(); }
    private Rectangle Thumb
    {
        get
        {
            var track = Math.Max(1, Height - 8); var length = Math.Clamp((int)(track * page / (double)Math.Max(1, maximum + page)), Math.Min(track, 24 * DeviceDpi / 96), track);
            return new Rectangle(Width / 2 - 3, 4 + (int)((track - length) * Value / (double)Math.Max(1, maximum)), 6, length);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (maximum == 0) return; e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var t = UiTheme.Current; var narrow = dragging || hovered ? 0 : 1;
        // A narrow engraved channel with a softly lit thumb in it.
        var channel = new RectangleF(Width / 2f - 3, 4, 6, Math.Max(1, Height - 8));
        Neu.Inset(e.Graphics, channel, 3, 1, 0.35f);
        var thumbRect = Thumb; thumbRect.Inflate(-narrow, 0);
        using var brush = new SolidBrush(dragging || hovered ? UiTheme.WithAlpha(t.Accent, 230) : UiTheme.Blend(Backdrop.Behind(this), Color.White, 0.22f));
        using var thumb = UiTheme.Rounded(thumbRect, Math.Max(2, thumbRect.Width / 2f)); e.Graphics.FillPath(brush, thumb);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var thumb = Thumb; if (e.Y >= thumb.Top && e.Y < thumb.Bottom) { dragging = true; dragOffset = e.Y - thumb.Top; Capture = true; }
        else Value += e.Y < thumb.Top ? -page : page; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) { var available = Height - 8 - Thumb.Height; Value = available <= 0 ? 0 : (int)Math.Round((e.Y - 4 - dragOffset) * maximum / (double)available); }
        var now = e.Y >= Thumb.Top && e.Y < Thumb.Bottom; if (now != hovered) { hovered = now; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { Value -= Math.Sign(e.Delta) * Math.Max(1, page / 8); }
}

public class ThemedScrollHost : BackdropPanel, IMessageFilter
{
    private readonly BackdropPanel viewport = new() { Margin = Padding.Empty };
    private readonly ModernScrollBar bar = new();
    private Control? content;
    private TableLayoutPanel? rows;
    private bool updating, needsScroll;
    public ThemedScrollHost()
    {
        DoubleBuffered = true; AutoScroll = false; Margin = Padding.Empty;
        base.Controls.Add(viewport); base.Controls.Add(bar); bar.BringToFront(); bar.ValueChanged += (_, _) => { if (content is not null) content.Top = -bar.Value; };
        viewport.SizeChanged += (_, _) => UpdateRange(); MouseWheel += Wheel; viewport.MouseWheel += Wheel;
    }
    public void SetContent(Control control)
    {
        content = control; control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        if (control is TableLayoutPanel { AutoSize: true } table) { rows = table; table.AutoSize = false; }
        viewport.Controls.Add(control); control.SizeChanged += (_, _) => UpdateRange(); control.Layout += (_, _) => UpdateRange(); control.MouseWheel += Wheel; UpdateRange();
    }
    private void Wheel(object? sender, MouseEventArgs e)
    { bar.Value -= Math.Sign(e.Delta) * 54 * DeviceDpi / 96; if (e is HandledMouseEventArgs handled) handled.Handled = true; }
    public void ScrollToEnd() { UpdateRange(); bar.Value = int.MaxValue; }
    public void ScrollTo(Control child)
    {
        if (content is null) return; var offset = 0; Control? current = child;
        while (current is not null && current != content) { offset += current.Top; current = current.Parent; }
        if (current == content) { UpdateRange(); bar.Value = offset; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Application.AddMessageFilter(this); }
    protected override void OnHandleDestroyed(EventArgs e) { Application.RemoveMessageFilter(this); base.OnHandleDestroyed(e); }
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x020A || !Visible || !needsScroll) return false;
        var child = Control.FromHandle(m.HWnd);
        for (var current = child; current is not null; current = current.Parent)
        {
            if (current is ThemedComboBox) return false;
            if (current is ThemedScrollHost nested && nested != this && nested.needsScroll) return false;
            if (current != this) continue;
            var delta = (short)((long)m.WParam >> 16); bar.Value -= Math.Sign(delta) * 54 * DeviceDpi / 96; return true;
        }
        return false;
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); UpdateRange(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRange(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateRange(); }
    public void UpdateRange()
    {
        if (updating || content is null) return; updating = true;
        try
        {
            var width = Math.Max(1, ClientSize.Width); var height = Math.Max(1, ClientSize.Height);
            if (rows is not null) content.Height = Math.Max(1, (int)Math.Ceiling(rows.RowStyles.Cast<RowStyle>().Sum(r => r.Height)) + rows.Padding.Vertical);
            var barWidth = Math.Max(13, 13 * DeviceDpi / 96);
            var reserve = needsScroll ? barWidth : 0;
            viewport.Bounds = new Rectangle(0, 0, Math.Max(1, width - reserve), height);
            if (content.Width != viewport.Width) content.Width = viewport.Width;
            if (content.AutoSize) content.Height = content.GetPreferredSize(new Size(content.Width, 0)).Height;
            var needed = content.Height > height;
            if (needed != needsScroll)
            {
                needsScroll = needed; reserve = needed ? barWidth : 0;
                viewport.Width = Math.Max(1, width - reserve); content.Width = viewport.Width;
                if (content.AutoSize) content.Height = content.GetPreferredSize(new Size(content.Width, 0)).Height;
            }
            bar.Bounds = new Rectangle(width - reserve, 0, Math.Max(1, reserve), height);
            bar.SetRange(content.Height, height, bar.Value); content.Location = new Point(0, -bar.Value);
        }
        finally { updating = false; }
    }
}

public sealed class ThemedFlowList : ThemedScrollHost
{
    private readonly BufferedPeople flow = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
    public ThemedFlowList() { SetContent(flow); }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public new ControlCollection Controls => flow.Controls;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int ContentHeight => Math.Max(flow.Height, flow.GetPreferredSize(new Size(Math.Max(1, flow.Width), 0)).Height);
    [DefaultValue(true)] public bool WrapContents { get => flow.WrapContents; set => flow.WrapContents = value; }
}

public sealed class ThemedTextScroll : BackdropPanel
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr w, IntPtr l);
    private readonly RichTextBox editor;
    private readonly ModernScrollBar bar = new() { Dock = DockStyle.Right };
    private bool updating, pending;
    public ThemedTextScroll(RichTextBox editor)
    {
        this.editor = editor; Dock = DockStyle.Fill; editor.ScrollBars = RichTextBoxScrollBars.None; editor.Dock = DockStyle.Fill;
        Controls.Add(editor); Controls.Add(bar); bar.BringToFront(); editor.VScroll += (_, _) => QueueUpdate(); editor.TextChanged += (_, _) => QueueUpdate(); editor.SizeChanged += (_, _) => QueueUpdate();
        bar.ValueChanged += (_, _) =>
        {
            if (updating || !editor.IsHandleCreated) return; var first = (int)SendMessage(editor.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(editor.Handle, 0x00B6, IntPtr.Zero, new IntPtr(bar.Value - first));
        };
    }
    private void QueueUpdate()
    {
        if (pending || !IsHandleCreated) return; pending = true;
        BeginInvoke(new Action(() => { pending = false; if (!IsDisposed) UpdateRange(); }));
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); QueueUpdate(); }
    private void UpdateRange()
    {
        if (!editor.IsHandleCreated) return; updating = true;
        try
        {
            var first = (int)SendMessage(editor.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
            var total = editor.GetLineFromCharIndex(editor.TextLength) + 1;
            var visible = Math.Max(1, editor.ClientSize.Height / Math.Max(1, editor.Font.Height)); bar.SetRange(total, visible, first);
        }
        finally { updating = false; }
    }
}
