using System.Drawing.Drawing2D;

namespace Voicecon;

/// <summary>How streams being watched sit in the main window.</summary>
public enum StageMode { Hidden, Mini, Full }

/// <summary>
/// Streams you watch, inside the main window: the focused one large (letterboxed in a dark well), others as thumbnails
/// along the bottom. Controls appear while the pointer is over it: collapse to a mini view above the chat (or expand
/// back), open in a separate window, full screen, stop watching. Double-click goes full screen; in the mini view a
/// click expands it.
/// </summary>
public sealed class StageView : Control
{
    private sealed class Stream(string key, string title)
    {
        public string Key { get; } = key; public string Title { get; } = title;
        public Bitmap? Frame; public string Info = "";
    }
    private enum StageAction { Collapse, Expand, PopOut, Fullscreen, Stop }
    private readonly List<Stream> streams = [];
    private string? focus;
    private StageMode mode = StageMode.Full;
    private bool hover;
    private StageAction? pressedAction, hotAction;
    private readonly ToolTip tips = new() { InitialDelay = 350 };
    private string tipText = "";

    public event Action<string>? PopOutRequested, FullscreenRequested, StopRequested;
    public event Action? ExpandRequested, CollapseRequested;

    public StageView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Current.Surface; AccessibleRole = AccessibleRole.Graphic; AccessibleName = "Трансляция";
    }
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public StageMode Mode { get => mode; set { if (mode == value) return; mode = value; Invalidate(); } }
    public int Count => streams.Count;
    public bool Contains(string key) => streams.Any(s => s.Key == key);
    public string? Focused => focus;
    public string? TitleOf(string key) => streams.FirstOrDefault(s => s.Key == key)?.Title;

    public void Add(string key, string title) { if (!Contains(key)) streams.Add(new Stream(key, title)); focus = key; Invalidate(); }
    public void FocusOn(string key) { if (Contains(key)) { focus = key; Invalidate(); } }
    public void Remove(string key)
    {
        var stream = streams.FirstOrDefault(s => s.Key == key); if (stream is null) return;
        streams.Remove(stream); stream.Frame?.Dispose();
        if (focus == key) focus = streams.LastOrDefault()?.Key;
        Invalidate();
    }
    public void Clear() { foreach (var s in streams) s.Frame?.Dispose(); streams.Clear(); focus = null; Invalidate(); }
    /// <summary>Takes ownership of the bitmap. Only the part showing it is repainted.</summary>
    public void ShowFrame(string key, Bitmap image)
    {
        var stream = streams.FirstOrDefault(s => s.Key == key);
        if (stream is null || IsDisposed) { image.Dispose(); return; }
        var old = stream.Frame; stream.Frame = image; old?.Dispose();
        stream.Info = $"{image.Width}×{image.Height}";
        if (key == focus) Invalidate(Rectangle.Ceiling(Well)); else Invalidate(Rectangle.Ceiling(ThumbStrip));
    }

    private float S => DeviceDpi / 96f;
    private RectangleF Well => new(0, 0, Width, Height);
    private bool ShowThumbs => mode == StageMode.Full && streams.Count > 1 && Height > 260 * S;
    private RectangleF ThumbStrip => ShowThumbs ? new RectangleF(0, Height - 92 * S, Width, 92 * S) : RectangleF.Empty;
    private RectangleF Thumb(int index)
    {
        var s = S; var h = 70 * s; var w = h * 16 / 9; var gap = 10 * s; var total = streams.Count * w + (streams.Count - 1) * gap;
        return new RectangleF((Width - total) / 2 + index * (w + gap), Height - h - 12 * s, w, h);
    }
    private IEnumerable<StageAction> Actions => mode == StageMode.Mini ? [StageAction.Expand, StageAction.PopOut, StageAction.Stop] : [StageAction.Collapse, StageAction.PopOut, StageAction.Fullscreen, StageAction.Stop];
    private RectangleF Button(int index, int count) { var s = S; var size = (mode == StageMode.Mini ? 32 : 38) * s; var gap = 8 * s; return new RectangleF(Width - 12 * s - (count - index) * (size + gap) + gap, 12 * s, size, size); }
    private StageAction? HitAction(Point p)
    {
        if (!hover || focus is null) return null;
        var list = Actions.ToList(); for (var i = 0; i < list.Count; i++) if (RectangleF.Inflate(Button(i, list.Count), 3, 3).Contains(p)) return list[i];
        return null;
    }
    private static (string Tip, UiIcon Icon) Describe(StageAction action) => action switch
    {
        StageAction.Collapse => ("Свернуть над чатом", UiIcon.Collapse), StageAction.Expand => ("Развернуть", UiIcon.Expand),
        StageAction.PopOut => ("Открыть в отдельном окне", UiIcon.Screen), StageAction.Fullscreen => ("Во весь экран · двойной щелчок", UiIcon.Expand),
        _ => ("Перестать смотреть", UiIcon.Close),
    };

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; hotAction = null; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!hover) { hover = true; Invalidate(); }
        var hit = HitAction(e.Location); if (hit != hotAction) { hotAction = hit; Invalidate(); }
        var tip = hit is { } a ? Describe(a).Tip : mode == StageMode.Mini && focus is not null ? "Развернуть" : "";
        if (tip != tipText) { tipText = tip; tips.SetToolTip(this, tip); }
        Cursor = hit is not null || mode == StageMode.Mini || ThumbAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }
    private int ThumbAt(Point p) { if (!ShowThumbs) return -1; for (var i = 0; i < streams.Count; i++) if (Thumb(i).Contains(p)) return i; return -1; }
    protected override void OnMouseDown(MouseEventArgs e) { pressedAction = e.Button == MouseButtons.Left ? HitAction(e.Location) : null; base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && focus is { } key)
        {
            var hit = HitAction(e.Location);
            if (hit is { } action && action == pressedAction)
            {
                switch (action)
                {
                    case StageAction.Collapse: CollapseRequested?.Invoke(); break;
                    case StageAction.Expand: ExpandRequested?.Invoke(); break;
                    case StageAction.PopOut: PopOutRequested?.Invoke(key); break;
                    case StageAction.Fullscreen: FullscreenRequested?.Invoke(key); break;
                    case StageAction.Stop: StopRequested?.Invoke(key); break;
                }
            }
            else if (hit is null && pressedAction is null)
            {
                var thumb = ThumbAt(e.Location);
                if (thumb >= 0) FocusOn(streams[thumb].Key);
                else if (mode == StageMode.Mini) ExpandRequested?.Invoke();
            }
        }
        pressedAction = null; base.OnMouseUp(e);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (mode == StageMode.Full && focus is { } key && HitAction(e.Location) is null && ThumbAt(e.Location) < 0) FullscreenRequested?.Invoke(key);
        base.OnMouseDoubleClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var t = UiTheme.Current; var s = S;
        Backdrop.Paint(g, this, ClientRectangle);
        if (Width < 20 || Height < 20) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var well = Well; var radius = (mode == StageMode.Mini ? 12 : 16) * s;
        using (var path = UiTheme.Rounded(well, radius))
        {
            using (var dark = new SolidBrush(Color.FromArgb(12, 14, 19))) g.FillPath(dark, path);
            var state = g.Save(); g.SetClip(path, CombineMode.Intersect);
            var stream = streams.FirstOrDefault(x => x.Key == focus);
            var video = ShowThumbs ? new RectangleF(well.X, well.Y, well.Width, well.Height - ThumbStrip.Height) : well;
            if (stream?.Frame is { } frame) DrawFit(g, frame, video, high: false);
            else
            {
                var text = streams.Count == 0 ? "Нет трансляций" : "Подключаем трансляцию… первый кадр появится через секунду";
                TextRenderer.DrawText(g, text, mode == StageMode.Mini ? UiTheme.Small : UiTheme.Body, Rectangle.Round(RectangleF.Inflate(video, -16 * s, -16 * s)), Color.FromArgb(170, 255, 255, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsClipping);
            }
            if (ShowThumbs)
            {
                using (var shade = new LinearGradientBrush(ThumbStrip, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 90f)) g.FillRectangle(shade, ThumbStrip);
                for (var i = 0; i < streams.Count; i++)
                {
                    var r = Thumb(i); using var tile = UiTheme.Rounded(r, 8 * s);
                    using (var back = new SolidBrush(Color.FromArgb(28, 31, 40))) g.FillPath(back, tile);
                    var inner = g.Save(); g.SetClip(tile, CombineMode.Intersect);
                    if (streams[i].Frame is { } small) DrawFit(g, small, r, high: true);
                    g.Restore(inner);
                    using var border = new Pen(streams[i].Key == focus ? t.Accent : Color.FromArgb(60, 255, 255, 255), (streams[i].Key == focus ? 2f : 1f) * s); g.DrawPath(border, tile);
                    TextRenderer.DrawText(g, streams[i].Title, UiTheme.Small, Rectangle.Round(new RectangleF(r.X + 6 * s, r.Bottom - 20 * s, r.Width - 12 * s, 18 * s)), Color.White, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                }
            }
            // Title pill and buttons while the pointer is over the stage (always in the mini view, it is small).
            if (stream is not null && (hover || mode == StageMode.Mini || stream.Frame is null))
            {
                var label = stream.Info.Length > 0 && mode == StageMode.Full ? stream.Title + "  ·  " + stream.Info : stream.Title;
                var font = mode == StageMode.Mini ? UiTheme.Small : UiTheme.Strong;
                var size = TextRenderer.MeasureText(label, font, Size.Empty, TextFormatFlags.NoPadding);
                var pill = new RectangleF(12 * s, 12 * s, Math.Min(size.Width + 24 * s, Width * 0.6f), (mode == StageMode.Mini ? 26 : 32) * s);
                using (var back = UiTheme.Rounded(pill, pill.Height / 2)) using (var brush = new SolidBrush(Color.FromArgb(150, 10, 12, 18))) g.FillPath(brush, back);
                TextRenderer.DrawText(g, label, font, Rectangle.Round(RectangleF.Inflate(pill, -12 * s, 0)), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
            }
            if (hover && stream is not null)
            {
                var list = Actions.ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    var r = Button(i, list.Count); var hot = hotAction == list[i];
                    using (var brush = new SolidBrush(hot ? (list[i] == StageAction.Stop ? t.Danger : t.Accent) : Color.FromArgb(160, 10, 12, 18))) g.FillEllipse(brush, r);
                    UiIcons.Draw(g, Describe(list[i]).Icon, RectangleF.Inflate(r, -r.Width * 0.28f, -r.Height * 0.28f), Color.White);
                }
            }
            g.Restore(state);
        }
    }
    /// <summary>Letterboxes the frame into the area. Large live frames use a fast filter; thumbnails a smooth one.</summary>
    private static void DrawFit(Graphics g, Image frame, RectangleF area, bool high)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;
        var scale = Math.Min(area.Width / frame.Width, area.Height / frame.Height);
        var w = frame.Width * scale; var h = frame.Height * scale;
        var target = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
        g.InterpolationMode = high || scale < 0.5f ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        g.DrawImage(frame, target);
        g.InterpolationMode = mode; g.PixelOffsetMode = offset;
    }
    protected override void Dispose(bool disposing) { if (disposing) { Clear(); tips.Dispose(); } base.Dispose(disposing); }
}

/// <summary>
/// The middle of the main window: the chat alone; a mini stream above the chat; or the stream large with the chat as a
/// column on the right (on a narrow window the chat goes below the stream instead).
/// </summary>
public sealed class CenterLayout : BackdropPanel
{
    private readonly Control stage, chat;
    private StageMode mode = StageMode.Hidden;
    public CenterLayout(Control stage, Control chat)
    {
        this.stage = stage; this.chat = chat; Margin = Padding.Empty;
        stage.Dock = DockStyle.None; chat.Dock = DockStyle.None; stage.Visible = false;
        Controls.Add(stage); Controls.Add(chat);
    }
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public StageMode Mode { get => mode; set { if (mode == value) return; mode = value; stage.Visible = value != StageMode.Hidden; PerformLayout(); Invalidate(); } }
    /// <summary>True when the window is too narrow to put the chat beside the stream.</summary>
    public bool Narrow => Width < 820 * DeviceDpi / 96f;
    public int ChatColumnWidth => (int)Math.Clamp(Width * 0.34f, 320 * DeviceDpi / 96f, 400 * DeviceDpi / 96f);
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var area = ClientRectangle; if (area.Width < 10 || area.Height < 10) return;
        var s = DeviceDpi / 96f;
        switch (mode)
        {
            case StageMode.Hidden:
                chat.Bounds = area; break;
            case StageMode.Mini:
            {
                // A 16:9 strip across the top, about a quarter of the height.
                var h = (int)Math.Clamp(area.Height * 0.3f, 150 * s, 230 * s);
                stage.Bounds = new Rectangle(area.X, area.Y, area.Width, h);
                chat.Bounds = new Rectangle(area.X, area.Y + h, area.Width, area.Height - h); break;
            }
            default:
                if (Narrow)
                {
                    var h = (int)Math.Min(area.Height * 0.62f, area.Width * 9f / 16 + 40 * s);
                    stage.Bounds = new Rectangle(area.X, area.Y, area.Width, h);
                    chat.Bounds = new Rectangle(area.X, area.Y + h, area.Width, area.Height - h);
                }
                else
                {
                    var w = ChatColumnWidth;
                    stage.Bounds = new Rectangle(area.X, area.Y, area.Width - w, area.Height);
                    chat.Bounds = new Rectangle(area.Right - w, area.Y, w, area.Height);
                }
                break;
        }
    }
}
