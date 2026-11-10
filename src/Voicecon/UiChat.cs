using System.Drawing.Drawing2D;

namespace Voicecon;

/// <summary>
/// Chat history drawn directly on glass: rounded bubbles (yours on the right in the accent colour, others on the left),
/// day separators, sender and time headers. Messages sit at the bottom like a messenger. Right-click copies a message.
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
    public static readonly Font MessageFont = new("Segoe UI", 10.5f);
    private static readonly Font HeaderFont = new("Segoe UI Semibold", 8.5f);
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

    public ChatView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = UiTheme.Current.GlassSolid; TabStop = true; AccessibleRole = AccessibleRole.List; AccessibleName = "История чата";
    }
    public int Count => records.Count;
    public IReadOnlyList<ChatRecord> Records => records;
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get => emptyText; set { if (emptyText == value) return; emptyText = value; if (records.Count == 0) Invalidate(); } }
    public void SetRecords(IEnumerable<ChatRecord> list) { records.Clear(); records.AddRange(list); focused = null; Relayout(); }
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
        items.Clear(); laidOutWidth = Width;
        if (Width < 40) { contentHeight = 0; FitHeight(); return; }
        var s = S; var side = 6 * s; var maxBubble = Math.Max(120 * s, (Width - 2 * side) * 0.74f);
        var padX = 14 * s; var padY = 8 * s; var y = 8 * s;
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
            var bubbleW = Math.Max(44 * s, measured.Width + 2 * padX + 2); var bubbleH = measured.Height + 2 * padY;
            var x = record.Mine ? Width - side - bubbleW : side;
            var bubble = new RectangleF(x, y, bubbleW, bubbleH);
            items.Add(new Item(false, text, bubble, new RectangleF(x + padX, y + padY, bubbleW - 2 * padX + 2, measured.Height + 2), record, header, headerBounds));
            y += bubbleH + 4 * s; previous = record;
        }
        contentHeight = y + 10 * s;
        FitHeight();
    }

    private Item? Hit(Point p)
    {
        var offset = Offset;
        return items.FirstOrDefault(i => !i.Day && new RectangleF(i.Bounds.X, i.Bounds.Y + offset, i.Bounds.Width, i.Bounds.Height).Contains(p));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus(); var hit = Hit(e.Location); focused = hit?.Record; Invalidate();
        if (e.Button == MouseButtons.Right && hit?.Record is { } record)
        {
            var menu = ThemeMenuRenderer.CreateMenu();
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
                Glass.Pill(g, bounds, bounds.Height / 2, s, Color.FromArgb(20, 255, 255, 255), 0, 0, 0.6f);
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
            if (record.Mine) Glass.Pill(g, bounds, radius, s, t.Accent, record == focused ? 0.6f : 0);
            else Glass.Pill(g, bounds, radius, s, Color.FromArgb(record == focused ? 40 : 28, 255, 255, 255));
            var textRect = Rectangle.Round(new RectangleF(item.TextBounds.X, item.TextBounds.Y + offset, item.TextBounds.Width, item.TextBounds.Height));
            TextRenderer.DrawText(g, item.Text, MessageFont, textRect, record.Mine ? t.AccentInk : t.Text, Wrap);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing && viewport is not null) viewport.SizeChanged -= OnViewport; base.Dispose(disposing); }
}
