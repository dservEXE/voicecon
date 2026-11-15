using System.Drawing.Drawing2D;

namespace Voicecon;

/// <summary>
/// Chat history drawn directly on glass: rounded bubbles (yours on the right in the accent colour, others on the left),
/// day separators, sender and time headers. Messages sit at the bottom like a messenger. Your messages carry ticks:
/// one (sent), two (delivered), two coloured (read), or a red mark (not sent). Pressing on a message's text opens it for
/// selection with the mouse, so any part can be copied; right-click copies a message or the whole conversation.
/// Lives inside a <see cref="ThemedScrollHost"/>; only visible bubbles are painted.
/// </summary>
public sealed class ChatView : Control
{
    private sealed record Item(bool Day, string Text, RectangleF Bounds, RectangleF TextBounds, ChatRecord? Record, string Header, RectangleF HeaderBounds);
    private readonly List<ChatRecord> records = [];
    private readonly List<Item> items = [];
    private float contentHeight;
    private int laidOutWidth = -1;
    private Control? viewport;
    private ChatRecord? focused;
    private string emptyText = "";
    /// <summary>State of a message by id (ticks for your own messages).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, MessageState>? StateOf { get; set; }
    /// <summary>Raised when "send again" is chosen for a message that failed.</summary>
    public event Action<ChatRecord>? ResendRequested;
    /// <summary>A borderless read-only box laid exactly over a message's text, so part of it can be selected.</summary>
    private readonly TextBox selector = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, WordWrap = true, Visible = false, TabStop = false, ScrollBars = ScrollBars.None, Cursor = Cursors.IBeam, ShortcutsEnabled = true };
    private Item? selecting;
    private readonly ToolTip tips = new() { InitialDelay = 400, ReshowDelay = 100 };
    private string tipText = "";
    private static readonly Color ReadTicks = Color.FromArgb(120, 226, 255);
    public static readonly Font MessageFont = new("Segoe UI", 10.5f);
    private static readonly Font HeaderFont = new("Segoe UI Semibold", 8.5f);
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

    public ChatView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = UiTheme.Current.Surface; TabStop = true; AccessibleRole = AccessibleRole.List; AccessibleName = "История чата";
        selector.Font = MessageFont; Controls.Add(selector);
        selector.LostFocus += (_, _) => { if (selector.ContextMenuStrip is not { Visible: true }) BeginInvoke(new Action(() => { if (!selector.Focused) HideSelector(); })); };
        selector.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { HideSelector(); Focus(); e.Handled = true; }
            // Nothing selected: Ctrl+C copies the whole message, as before.
            else if (e.KeyData == (Keys.Control | Keys.C) && selector.SelectionLength == 0 && selecting?.Record is { } record) { Clipboard.SetText(record.Text); e.Handled = true; }
        };
        var menu = ThemeMenuRenderer.CreateMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear(); var record = selecting?.Record;
            var copy = menu.Items.Add("Копировать", null, (_, _) => { if (selector.SelectionLength > 0) Clipboard.SetText(selector.SelectedText); }); copy.Enabled = selector.SelectionLength > 0;
            menu.Items.Add("Выделить всё", null, (_, _) => selector.SelectAll());
            if (record is not null) menu.Items.Add("Копировать сообщение", null, (_, _) => Clipboard.SetText(record.Text));
        };
        selector.ContextMenuStrip = menu;
    }
    private const int LeftButtonDown = 0x0201;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    /// <summary>Lays the selection box over a message's text and passes it the press, so dragging selects at once.</summary>
    private void BeginSelect(Item item, Point press)
    {
        var t = UiTheme.Current; var record = item.Record!; var offset = Offset;
        selecting = item;
        var bounds = Rectangle.Round(new RectangleF(item.TextBounds.X, item.TextBounds.Y + offset, item.TextBounds.Width, item.TextBounds.Height));
        selector.BackColor = Face(record); selector.ForeColor = record.Mine ? t.AccentInk : t.Text;
        var text = item.Text.Replace("\n", Environment.NewLine); if (selector.Text != text) selector.Text = text;
        selector.Bounds = bounds; selector.Visible = true; selector.BringToFront();
        SendMessage(selector.Handle, 0x00D3, (IntPtr)3, IntPtr.Zero); // EM_SETMARGINS: none, so the text sits exactly where it was drawn
        selector.Focus();
        var local = new Point(press.X - bounds.X, press.Y - bounds.Y);
        SendMessage(selector.Handle, LeftButtonDown, (IntPtr)1, (IntPtr)((local.Y << 16) | (local.X & 0xFFFF)));
        Invalidate(Rectangle.Ceiling(new RectangleF(item.Bounds.X, item.Bounds.Y + offset, item.Bounds.Width, item.Bounds.Height)));
    }
    private void HideSelector()
    {
        if (selecting is null && !selector.Visible) return;
        selector.Visible = false; selector.SelectionLength = 0; selecting = null; Invalidate();
    }
    private Color Face(ChatRecord record) => record.Mine ? UiTheme.Current.Accent : UiTheme.Blend(UiTheme.Current.Surface, Color.White, 0.025f);
    public int Count => records.Count;
    public IReadOnlyList<ChatRecord> Records => records;
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get => emptyText; set { if (emptyText == value) return; emptyText = value; if (records.Count == 0) Invalidate(); } }
    public void SetRecords(IEnumerable<ChatRecord> list) { HideSelector(); records.Clear(); records.AddRange(list); focused = null; Relayout(); }
    public void Add(ChatRecord record) { records.Add(record); Relayout(); }

    private float S => DeviceDpi / 96f;
    private float Offset => Math.Max(0, Height - contentHeight);

    protected override void OnParentChanged(EventArgs e)
    {
        if (viewport is not null) viewport.SizeChanged -= OnViewport;
        viewport = Parent; if (viewport is not null) viewport.SizeChanged += OnViewport;
        base.OnParentChanged(e); Relayout();
    }
    private void OnViewport(object? sender, EventArgs e) => FitHeight();
    protected override void OnSizeChanged(EventArgs e) { if (Width != laidOutWidth) Relayout(); base.OnSizeChanged(e); }

    private void FitHeight()
    {
        var height = (int)Math.Ceiling(Math.Max(contentHeight, viewport?.Height ?? 0));
        if (Height != height) Height = height;
        Invalidate();
    }

    private void Relayout()
    {
        HideSelector(); items.Clear(); laidOutWidth = Width;
        if (Width < 40) { contentHeight = 0; FitHeight(); return; }
        var s = S; var side = 10 * s; var maxBubble = Math.Max(120 * s, (Width - 2 * side) * 0.74f);
        var padX = 14 * s; var padY = 8 * s; var y = 10 * s;
        DateTime? day = null; ChatRecord? previous = null;
        foreach (var record in records)
        {
            var local = record.Time.LocalDateTime;
            if (day != local.Date)
            {
                day = local.Date; previous = null;
                var label = local.Date == DateTime.Today ? "Сегодня" : local.Date == DateTime.Today.AddDays(-1) ? "Вчера" : local.ToString("d MMMM");
                var w = TextRenderer.MeasureText(label, UiTheme.Caption, Size.Empty, TextFormatFlags.NoPadding).Width + 24 * s;
                var bounds = new RectangleF((Width - w) / 2, y + 6 * s, w, 22 * s);
                items.Add(new Item(true, label, bounds, bounds, null, "", RectangleF.Empty)); y += 40 * s;
            }
            var grouped = previous is not null && previous.Mine == record.Mine && previous.Name == record.Name && (record.Time - previous.Time).TotalMinutes < 5;
            var header = grouped ? "" : (record.Mine ? local.ToString("HH:mm") : record.Name + "  ·  " + local.ToString("HH:mm"));
            RectangleF headerBounds = RectangleF.Empty;
            if (!grouped)
            {
                y += previous is null ? 0 : 8 * s;
                headerBounds = new RectangleF(side + 6 * s, y, Width - 2 * side - 12 * s, 18 * s); y += 20 * s;
            }
            var text = record.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            var measured = TextRenderer.MeasureText(text.Length == 0 ? " " : text, MessageFont, new Size((int)(maxBubble - 2 * padX), int.MaxValue), Wrap);
            var ticks = record.Mine ? 24 * s : 0;
            var bubbleW = Math.Max(44 * s, measured.Width + 2 * padX + 2 + ticks); var bubbleH = measured.Height + 2 * padY;
            var x = record.Mine ? Width - side - bubbleW : side;
            var bubble = new RectangleF(x, y, bubbleW, bubbleH);
            items.Add(new Item(false, text, bubble, new RectangleF(x + padX, y + padY, bubbleW - 2 * padX + 2, measured.Height + 2), record, header, headerBounds));
            y += bubbleH + 8 * s; previous = record;
        }
        contentHeight = y + 10 * s;
        FitHeight();
    }

    private Item? Hit(Point p)
    {
        var offset = Offset;
        return items.FirstOrDefault(i => !i.Day && new RectangleF(i.Bounds.X, i.Bounds.Y + offset, i.Bounds.Width, i.Bounds.Height).Contains(p));
    }
    private RectangleF TickArea(Item item, float offset) { var s = S; return new RectangleF(item.Bounds.Right - 30 * s, item.Bounds.Bottom + offset - 22 * s, 24 * s, 16 * s); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var offset = Offset; var tip = "";
        var item = Hit(e.Location);
        if (item?.Record is { Mine: true } mine && RectangleF.Inflate(TickArea(item, offset), 4, 4).Contains(e.Location))
            tip = (StateOf?.Invoke(mine.Id) ?? MessageState.None) switch
            {
                MessageState.Sending => "Отправляется", MessageState.Sent => "Отправлено, ещё не доставлено", MessageState.Delivered => "Доставлено",
                MessageState.Read => "Прочитано", MessageState.Failed => "Не отправлено · правый клик — отправить снова", _ => "",
            };
        if (tip != tipText) { tipText = tip; tips.SetToolTip(this, tip); }
        Cursor = item is not null && item.Record is not null && new RectangleF(item.TextBounds.X, item.TextBounds.Y + offset, item.TextBounds.Width, item.TextBounds.Height).Contains(e.Location) ? Cursors.IBeam : Cursors.Default;
        base.OnMouseMove(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus(); var hit = Hit(e.Location); focused = hit?.Record; Invalidate();
        // Pressing on the text starts a selection right away (drag to select part of it, double-click for a word).
        if (e.Button == MouseButtons.Left && hit?.Record is not null && new RectangleF(hit.TextBounds.X, hit.TextBounds.Y + Offset, hit.TextBounds.Width, hit.TextBounds.Height).Contains(e.Location))
        { BeginSelect(hit, e.Location); base.OnMouseDown(e); return; }
        if (e.Button == MouseButtons.Right && hit?.Record is { } record)
        {
            var menu = ThemeMenuRenderer.CreateMenu();
            if (record.Mine && StateOf?.Invoke(record.Id) == MessageState.Failed) menu.Items.Add("Отправить снова", null, (_, _) => ResendRequested?.Invoke(record));
            menu.Items.Add("Копировать сообщение", null, (_, _) => Clipboard.SetText(record.Text));
            if (records.Count > 1) menu.Items.Add("Копировать всю переписку", null, (_, _) => Clipboard.SetText(string.Join(Environment.NewLine, records.Select(r => $"[{r.Time.LocalDateTime:dd.MM HH:mm}] {(r.Mine ? "Вы" : r.Name)}: {r.Text}"))));
            menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose)); menu.Show(this, e.Location);
        }
        base.OnMouseDown(e);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C) && focused is { } record) { Clipboard.SetText(record.Text); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnLostFocus(EventArgs e) { focused = null; Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var t = UiTheme.Current; var g = e.Graphics; var s = S; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (records.Count == 0)
        {
            var box = new Rectangle((int)(30 * s), 0, Math.Max(1, Width - (int)(60 * s)), Math.Max(1, viewport?.Height ?? Height));
            TextRenderer.DrawText(g, emptyText, UiTheme.Body, box, t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        var offset = Offset; var clip = e.ClipRectangle;
        foreach (var item in items)
        {
            var bounds = new RectangleF(item.Bounds.X, item.Bounds.Y + offset, item.Bounds.Width, item.Bounds.Height);
            var top = item.HeaderBounds.IsEmpty ? bounds.Top : item.HeaderBounds.Top + offset;
            if (bounds.Bottom < clip.Top || top > clip.Bottom) continue;
            if (item.Day)
            {
                Neu.Inset(g, bounds, bounds.Height / 2, s, 0.5f);
                TextRenderer.DrawText(g, item.Text, UiTheme.Caption, Rectangle.Round(bounds), t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                continue;
            }
            var record = item.Record!;
            if (item.Header.Length > 0)
            {
                var headerRect = Rectangle.Round(new RectangleF(item.HeaderBounds.X, item.HeaderBounds.Y + offset, item.HeaderBounds.Width, item.HeaderBounds.Height));
                var align = record.Mine ? TextFormatFlags.Right : TextFormatFlags.Left;
                var color = record.Mine ? t.Muted : UiTheme.Blend(UiTheme.AvatarColor(record.Name), t.Text, 0.25f);
                TextRenderer.DrawText(g, item.Header, HeaderFont, headerRect, color, align | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
            }
            var radius = Math.Min(18 * s, bounds.Height / 2);
            // Bubbles are softly raised: yours in the accent colour, others in the surface tone. Under the selection
            // box the face is flat, so the box blends in.
            var selected = selecting == item;
            if (record.Mine) Neu.Raised(g, bounds, radius, s, Neu.SmallDepth, record == focused && !selected ? UiTheme.Blend(t.Accent, Color.White, 0.1f) : t.Accent, convex: selected ? 0 : 0.6f, rim: 1.2f);
            else Neu.Raised(g, bounds, radius, s, Neu.SmallDepth, record == focused && !selected ? UiTheme.Blend(t.Surface, Color.White, 0.05f) : Face(record), rim: 1);
            if (record.Mine) DrawTicks(g, TickArea(item, offset), StateOf?.Invoke(record.Id) ?? MessageState.None, t, s);
            var textRect = Rectangle.Round(new RectangleF(item.TextBounds.X, item.TextBounds.Y + offset, item.TextBounds.Width, item.TextBounds.Height));
            TextRenderer.DrawText(g, item.Text, MessageFont, textRect, record.Mine ? t.AccentInk : t.Text, Wrap);
        }
    }
    /// <summary>Clock while sending, one tick sent, two ticks delivered, two coloured ticks read, a red mark if not sent.</summary>
    private static void DrawTicks(Graphics g, RectangleF area, MessageState state, ThemePalette t, float s)
    {
        if (state == MessageState.None) return;
        var ink = UiTheme.WithAlpha(t.AccentInk, 170);
        if (state == MessageState.Failed)
        {
            var dot = new RectangleF(area.Right - 15 * s, area.Y + 1 * s, 14 * s, 14 * s);
            using (var red = new SolidBrush(t.Danger)) g.FillEllipse(red, dot);
            TextRenderer.DrawText(g, "!", UiTheme.Small, Rectangle.Round(dot), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
            return;
        }
        if (state == MessageState.Sending)
        {
            var clock = new RectangleF(area.Right - 14 * s, area.Y + 2 * s, 11 * s, 11 * s);
            using var pen = new Pen(ink, 1.2f * s); g.DrawEllipse(pen, clock);
            var c = new PointF(clock.X + clock.Width / 2, clock.Y + clock.Height / 2); g.DrawLine(pen, c, new PointF(c.X, clock.Y + 2.5f * s)); g.DrawLine(pen, c, new PointF(c.X + 2.5f * s, c.Y));
            return;
        }
        var color = state == MessageState.Read ? ReadTicks : ink;
        using var tick = new Pen(color, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Check(float x)
        {
            var y = area.Y + 8 * s;
            g.DrawLines(tick, [new PointF(x, y), new PointF(x + 3.2f * s, y + 3.4f * s), new PointF(x + 9 * s, y - 3.6f * s)]);
        }
        var right = area.Right - 4 * s;
        if (state is MessageState.Delivered or MessageState.Read) { Check(right - 15 * s); Check(right - 10 * s); }
        else Check(right - 10 * s);
    }
    protected override void Dispose(bool disposing) { if (disposing) { if (viewport is not null) viewport.SizeChanged -= OnViewport; tips.Dispose(); } base.Dispose(disposing); }
}
