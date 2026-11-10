using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Voicecon;

public enum ContactKind { Group, Friend }
public sealed record ContactEntry(ContactKind Kind, string Id, string Name, string Subtitle, PresenceStatus Status, bool Active = false)
{
    public string Key => (Kind == ContactKind.Group ? "g:" : "f:") + Id;
}

/// <summary>Sidebar list: groups and friends with avatars, presence dots and a sliding raised selection.</summary>
public sealed class ContactListView : Control
{
    private abstract record Row(float Height);
    private sealed record Header(string Text, bool CanAdd) : Row(32);
    private sealed record Item(ContactEntry Entry) : Row(60);
    private sealed record Empty(string Text) : Row(54);
    private readonly List<Row> rows = [];
    private int hovered = -1;
    private float hoverAmount, selectionY = -1, selectionTarget = -1;
    private readonly object hoverTrack = new(), selectTrack = new();
    private Rectangle addGroupBounds;
    public string? SelectedKey { get; private set; }
    public event Action<ContactEntry>? SelectionChanged;
    public event Action<ContactEntry>? EntryActivated;
    public event Action<ContactEntry, Point>? EntryMenu;
    public event Action? AddGroupClicked;

    public ContactListView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = UiTheme.Current.Surface; TabStop = true; AccessibleRole = AccessibleRole.List; AccessibleName = "Друзья и группы";
    }
    private float S => DeviceDpi / 96f;
    public IEnumerable<ContactEntry> Entries => rows.OfType<Item>().Select(i => i.Entry);

    public void SetEntries(IReadOnlyList<ContactEntry> groups, IReadOnlyList<ContactEntry> friends, string? selected)
    {
        rows.Clear();
        rows.Add(new Header("ГРУППЫ", true));
        if (groups.Count == 0) rows.Add(new Empty("Соберите друзей в группу кнопкой +"));
        rows.AddRange(groups.Select(g => new Item(g)));
        var online = friends.Count(f => f.Status is PresenceStatus.Online or PresenceStatus.InCall);
        rows.Add(new Header(online > 0 ? $"ДРУЗЬЯ · {online} В СЕТИ" : "ДРУЗЬЯ", false));
        if (friends.Count == 0) rows.Add(new Empty("Пока никого — нажмите + вверху"));
        rows.AddRange(friends.Select(f => new Item(f)));
        SelectedKey = selected is not null && Entries.Any(e => e.Key == selected) ? selected : null;
        Height = (int)Math.Ceiling(rows.Sum(r => r.Height) * S) + (int)(6 * S);
        var target = SelectedKey is null ? -1 : RowTop(rows.FindIndex(r => r is Item i && i.Entry.Key == SelectedKey));
        selectionTarget = target; if (selectionY < 0 || target < 0) selectionY = target;
        hovered = -1; Invalidate();
    }

    public void Select(string key, bool notify = true)
    {
        var index = rows.FindIndex(r => r is Item i && i.Entry.Key == key); if (index < 0) return;
        var entry = ((Item)rows[index]).Entry; var changed = SelectedKey != key; SelectedKey = key;
        var start = selectionY < 0 ? RowTop(index) : selectionY; var end = RowTop(index); selectionTarget = end;
        UiMotion.Run(selectTrack, this, 240, p => { selectionY = start + (end - start) * p; Invalidate(); });
        if (changed && notify) SelectionChanged?.Invoke(entry);
    }

    private float RowTop(int index) { var y = 3 * S; for (var i = 0; i < index && i < rows.Count; i++) y += rows[i].Height * S; return index < 0 ? -1 : y; }
    private int HitRow(Point p) { var y = 3 * S; for (var i = 0; i < rows.Count; i++) { var h = rows[i].Height * S; if (p.Y >= y && p.Y < y + h) return i; y += h; } return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitRow(e.Location); if (hit >= 0 && rows[hit] is not Item) hit = -1;
        Cursor = hit >= 0 || addGroupBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (hit != hovered) { hovered = hit; UiMotion.Run(hoverTrack, this, 140, p => { hoverAmount = p; Invalidate(); }); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (addGroupBounds.Contains(e.Location) && e.Button == MouseButtons.Left) { AddGroupClicked?.Invoke(); return; }
        var hit = HitRow(e.Location);
        if (hit >= 0 && rows[hit] is Item item)
        {
            Select(item.Entry.Key);
            if (e.Button == MouseButtons.Right) EntryMenu?.Invoke(item.Entry, PointToScreen(e.Location));
        }
        base.OnMouseDown(e);
    }
    protected override void OnDoubleClick(EventArgs e)
    {
        var hit = HitRow(PointToClient(Cursor.Position));
        if (hit >= 0 && rows[hit] is Item item) EntryActivated?.Invoke(item.Entry);
        base.OnDoubleClick(e);
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var items = Entries.ToList(); var current = items.FindIndex(i => i.Key == SelectedKey);
        if (e.KeyCode == Keys.Down && items.Count > 0) { Select(items[Math.Min(items.Count - 1, current + 1)].Key); e.Handled = true; }
        if (e.KeyCode == Keys.Up && items.Count > 0) { Select(items[Math.Max(0, current - 1)].Key); e.Handled = true; }
        if (e.KeyCode == Keys.Enter && current >= 0) { EntryActivated?.Invoke(items[current]); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var t = UiTheme.Current; var g = e.Graphics; var s = S; g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        // The chosen conversation is pressed into the surface; the well slides between rows.
        if (selectionY >= 0 && SelectedKey is not null) Neu.Inset(g, RowCard(selectionY), 18 * s, s, 0.8f);
        var y = 3 * s; addGroupBounds = Rectangle.Empty;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var h = row.Height * s;
            if (y + h >= e.ClipRectangle.Top && y <= e.ClipRectangle.Bottom)
            {
                switch (row)
                {
                    case Header header:
                        TextRenderer.DrawText(g, header.Text, UiTheme.Caption, new Rectangle((int)(10 * s), (int)(y + 8 * s), Width - (int)(50 * s), (int)(20 * s)), t.Muted, flags);
                        if (header.CanAdd)
                        {
                            addGroupBounds = new Rectangle(Width - (int)(34 * s), (int)(y + 5 * s), (int)(26 * s), (int)(24 * s));
                            var hot = addGroupBounds.Contains(PointToClient(Cursor.Position));
                            UiIcons.Draw(g, UiIcon.Plus, new RectangleF(addGroupBounds.X + 7 * s, addGroupBounds.Y + 6 * s, 12 * s, 12 * s), hot ? t.Accent : t.Muted);
                        }
                        break;
                    case Empty empty:
                        TextRenderer.DrawText(g, empty.Text, UiTheme.Small, new Rectangle((int)(12 * s), (int)y, Width - (int)(20 * s), (int)h), UiTheme.WithAlpha(t.Muted, 200), flags | TextFormatFlags.WordBreak);
                        break;
                    case Item item:
                        var entry = item.Entry; var rect = RowCard(y);
                        var chosen = entry.Key == SelectedKey && Math.Abs(selectionY - y) < 1;
                        if (i == hovered && entry.Key != SelectedKey && hoverAmount > 0.02f)
                            Neu.Raised(g, rect, 18 * s, s, Neu.SmallDepth * hoverAmount, t.Surface, rim: hoverAmount * 0.8f);
                        var avatar = new RectangleF(rect.X + 9 * s, rect.Y + 8 * s, 38 * s, 38 * s);
                        Neu.Avatar(g, avatar, entry.Id, entry.Name, entry.Kind == ContactKind.Group);
                        if (entry.Kind == ContactKind.Friend)
                        {
                            var dot = entry.Status switch { PresenceStatus.Online => t.Online, PresenceStatus.InCall => t.Accent, _ => UiTheme.Blend(t.Muted, t.Background, 0.35f) };
                            Neu.StatusDot(g, new PointF(avatar.Right - 4 * s, avatar.Bottom - 4 * s), 10 * s, dot, chosen ? t.Field : t.Surface);
                        }
                        var textLeft = (int)(avatar.Right + 12 * s); var textWidth = Width - textLeft - (int)(12 * s);
                        var nameColor = entry.Key == SelectedKey ? t.Text : UiTheme.Blend(t.Text, t.Muted, 0.15f);
                        TextRenderer.DrawText(g, entry.Name, UiTheme.Strong, new Rectangle(textLeft, (int)(rect.Y + 8 * s), textWidth, (int)(20 * s)), nameColor, flags);
                        var subtitleColor = entry.Active ? t.Accent : entry.Status == PresenceStatus.Online ? UiTheme.Blend(t.Online, t.Muted, 0.35f) : t.Muted;
                        TextRenderer.DrawText(g, entry.Subtitle, UiTheme.Small, new Rectangle(textLeft, (int)(rect.Y + 28 * s), textWidth, (int)(18 * s)), subtitleColor, flags);
                        if (entry.Active)
                        {
                            using var live = new SolidBrush(t.Accent); g.FillEllipse(live, rect.Right - 18 * s, rect.Y + rect.Height / 2 - 4 * s, 8 * s, 8 * s);
                        }
                        break;
                }
            }
            y += h;
        }
        if (Focused && ShowFocusCues && SelectedKey is not null && selectionY >= 0) Neu.Focus(g, RowCard(selectionY), 18 * s, s);
    }
    /// <summary>Row shape, inset from the control edges far enough that a hover shadow is never clipped.</summary>
    private RectangleF RowCard(float y) { var s = S; return new RectangleF(6 * s, y + 3 * s, Width - 12 * s, 54 * s); }
    protected override void Dispose(bool disposing) { if (disposing) { UiMotion.Cancel(hoverTrack); UiMotion.Cancel(selectTrack); } base.Dispose(disposing); }
}

/// <summary>Avatar, title and status of the selected conversation.</summary>
public sealed class ConversationHeader : Control
{
    private string id = "", title = "", subtitle = "";
    private bool group, live;
    private PresenceStatus status;
    public ConversationHeader() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; }
    public void Set(string id, string title, string subtitle, PresenceStatus status, bool group, bool live)
    {
        if (this.id == id && this.title == title && this.subtitle == subtitle && this.status == status && this.group == group && this.live == live) return;
        this.id = id; this.title = title; this.subtitle = subtitle; this.status = status; this.group = group; this.live = live; AccessibleName = title + ". " + subtitle; Invalidate();
    }
    public string Subtitle => subtitle;
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        var size = Math.Min(Height - 14 * s, 48 * s); var avatar = new RectangleF(7 * s, (Height - size) / 2, size, size);
        if (id.Length == 0)
        {
            Neu.Inset(g, avatar, size / 2, s, 0.8f);
            UiIcons.Draw(g, UiIcon.Chat, RectangleF.Inflate(avatar, -size * 0.28f, -size * 0.28f), t.Muted);
        }
        else
        {
            if (live)
            {
                using var ring = new Pen(UiTheme.WithAlpha(t.Accent, 200), 2 * s); g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawEllipse(ring, RectangleF.Inflate(avatar, 3.5f * s, 3.5f * s));
            }
            Neu.Avatar(g, avatar, id, title, group);
            if (!group) Neu.StatusDot(g, new PointF(avatar.Right - 5 * s, avatar.Bottom - 5 * s), 12 * s, status switch { PresenceStatus.Online => t.Online, PresenceStatus.InCall => t.Accent, _ => UiTheme.Blend(t.Muted, t.Background, 0.35f) }, Backdrop.Behind(this));
        }
        var left = (int)(avatar.Right + 14 * s); var width = Math.Max(1, Width - left - 4);
        TextRenderer.DrawText(g, title.Length > 0 ? title : "Выберите друга или группу", UiTheme.Title, new Rectangle(left, (int)(Height / 2f - 26 * s), width, (int)(30 * s)), t.Text, flags);
        TextRenderer.DrawText(g, subtitle, UiTheme.Body, new Rectangle(left, (int)(Height / 2f + 4 * s), width, (int)(22 * s)), live ? t.Accent : t.Muted, flags);
    }
}

/// <summary>Bottom of the sidebar: own avatar, name and delivery status.</summary>
public sealed class SelfCard : Control
{
    private string id = "me", name = "Игрок", status = "Подключаемся…"; private bool ready;
    public SelfCard() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; }
    public void Set(string id, string name, string status, bool ready) { this.id = id; this.name = name; this.status = status; this.ready = ready; AccessibleName = name + ". " + status; Invalidate(); }
    public string Status => status;
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var t = UiTheme.Current; var g = e.Graphics; var s = DeviceDpi / 96f;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        var avatar = new RectangleF(6 * s, (Height - 36 * s) / 2, 36 * s, 36 * s);
        Neu.Avatar(g, avatar, id, name);
        Neu.StatusDot(g, new PointF(avatar.Right - 3 * s, avatar.Bottom - 3 * s), 9 * s, ready ? t.Online : UiTheme.Blend(t.Muted, t.Background, 0.3f), Backdrop.Behind(this));
        var left = (int)(avatar.Right + 10 * s);
        TextRenderer.DrawText(g, name, UiTheme.Strong, new Rectangle(left, (int)(Height / 2f - 19 * s), Width - left, (int)(20 * s)), t.Text, flags);
        TextRenderer.DrawText(g, status, UiTheme.Small, new Rectangle(left, (int)(Height / 2f + 1 * s), Width - left, (int)(18 * s)), ready ? UiTheme.Blend(t.Online, t.Muted, 0.35f) : t.Muted, flags);
    }
}

/// <summary>Participants and streams of the current call as glass chips. Click a person for their volume; click a stream to watch.</summary>
public sealed class CallStrip : Control
{
    public abstract record Chip(string Key);
    public sealed record PersonChip(string Id, string Name, bool Connected, double? Ping) : Chip("p:" + Id);
    public sealed record RemoteStreamChip(string PeerId, string Name, byte Kind, bool Watching) : Chip("s:" + PeerId + ":" + Kind);
    public sealed record OwnStreamChip(byte Kind, int Viewers, string Details) : Chip("o:" + Kind);
    private List<Chip> chips = [];
    private readonly List<(RectangleF Bounds, Chip Chip)> layout = [];
    private string? hovered;
    public event Action<PersonChip, Point>? PersonClicked;
    public event Action<RemoteStreamChip>? StreamClicked;
    public event Action<OwnStreamChip>? StopClicked;
    public CallStrip() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; }
    public IReadOnlyList<Chip> Chips => chips;
    public void SetChips(IEnumerable<Chip> next)
    {
        var list = next.ToList();
        if (list.SequenceEqual(chips)) return;
        chips = list; Layout(); Invalidate();
    }
    private float S => DeviceDpi / 96f;
    private float ChipWidth(Chip chip, Graphics? g = null)
    {
        var s = S; float Text(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        return chip switch
        {
            PersonChip p => 48 * s + Math.Min(170 * s, Math.Max(Text(p.Name, UiTheme.Strong), Text(p.Ping is { } ms ? $"{ms:0} мс · громкость" : "громкость", UiTheme.Small))) + 22 * s,
            RemoteStreamChip r => 44 * s + Math.Min(170 * s, Text(r.Name + (r.Kind == 1 ? " · экран" : " · камера"), UiTheme.Strong)) + 112 * s,
            OwnStreamChip o => 44 * s + Text(o.Kind == 1 ? "Экран в эфире" : "Камера в эфире", UiTheme.Strong) + 150 * s,
            _ => 100 * s
        };
    }
    public int DesiredHeight(int width)
    {
        var s = S; if (chips.Count == 0) return 0; float x = 0, rows = 1;
        foreach (var chip in chips) { var w = ChipWidth(chip); if (x > 0 && x + w > width - 16 * s) { rows++; x = 0; } x += w + 14 * s; }
        return (int)Math.Ceiling(rows * 46 * s + (rows - 1) * 14 * s + 18 * s);
    }
    private new void Layout()
    {
        // Chips keep a margin on every side for their soft shadows.
        layout.Clear(); var s = S; float x = 9 * s, y = 9 * s;
        foreach (var chip in chips)
        {
            var w = ChipWidth(chip); if (x > 9 * s && x + w > Width - 9 * s) { x = 9 * s; y += 60 * s; }
            layout.Add((new RectangleF(x, y, w, 46 * s), chip)); x += w + 14 * s;
        }
    }
    protected override void OnSizeChanged(EventArgs e) { Layout(); base.OnSizeChanged(e); }
    private static void ChipFace(Graphics g, RectangleF bounds, float radius, float s, bool hot) =>
        Neu.Raised(g, bounds, radius, s, Neu.ChipDepth, hot ? UiTheme.Blend(UiTheme.Current.Surface, Color.White, 0.03f) : UiTheme.Current.Surface, rim: hot ? 1.3f : 1);
    private Chip? Hit(Point p) => layout.FirstOrDefault(l => l.Bounds.Contains(p)).Chip;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = Hit(e.Location)?.Key; Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        if (hit != hovered) { hovered = hit; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = null; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        switch (Hit(e.Location))
        {
            case PersonChip p: PersonClicked?.Invoke(p, PointToScreen(new Point(e.X, e.Y))); break;
            case RemoteStreamChip r: StreamClicked?.Invoke(r); break;
            case OwnStreamChip o: StopClicked?.Invoke(o); break;
        }
        base.OnMouseClick(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var t = UiTheme.Current; var g = e.Graphics; var s = S; g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        foreach (var (bounds, chip) in layout)
        {
            var hot = chip.Key == hovered; var radius = bounds.Height / 2;
            switch (chip)
            {
                case PersonChip p:
                {
                    ChipFace(g, bounds, radius, s, hot);
                    var avatar = new RectangleF(bounds.X + 6 * s, bounds.Y + 6 * s, 34 * s, 34 * s);
                    if (!p.Connected) { using var fade = new SolidBrush(Color.FromArgb(120, t.Surface)); Neu.Avatar(g, avatar, p.Id, p.Name, depth: 0); g.FillEllipse(fade, avatar); }
                    else Neu.Avatar(g, avatar, p.Id, p.Name, depth: 0);
                    TextRenderer.DrawText(g, p.Name, UiTheme.Strong, new Rectangle((int)(avatar.Right + 8 * s), (int)bounds.Y, (int)(bounds.Right - avatar.Right - 16 * s), (int)(bounds.Height * 0.62f)), p.Connected ? t.Text : t.Muted, flags);
                    var detail = !p.Connected ? "соединяем…" : p.Ping is { } ms ? $"{ms:0} мс · громкость" : "громкость";
                    TextRenderer.DrawText(g, detail, UiTheme.Small, new Rectangle((int)(avatar.Right + 8 * s), (int)(bounds.Y + bounds.Height * 0.5f), (int)(bounds.Right - avatar.Right - 16 * s), (int)(bounds.Height * 0.42f)), t.Muted, flags);
                    break;
                }
                case RemoteStreamChip r:
                {
                    ChipFace(g, bounds, radius, s, hot);
                    var icon = new RectangleF(bounds.X + 14 * s, bounds.Y + 13 * s, 20 * s, 20 * s);
                    UiIcons.Draw(g, r.Kind == 1 ? UiIcon.Screen : UiIcon.Camera, icon, t.Accent);
                    var label = r.Name + (r.Kind == 1 ? " · экран" : " · камера");
                    var action = new RectangleF(bounds.Right - 104 * s, bounds.Y + 7 * s, 96 * s, bounds.Height - 14 * s);
                    TextRenderer.DrawText(g, label, UiTheme.Strong, new Rectangle((int)(icon.Right + 10 * s), (int)bounds.Y, (int)(action.X - icon.Right - 14 * s), (int)bounds.Height), t.Text, flags);
                    if (r.Watching) Neu.Inset(g, action, action.Height / 2, s, 0.6f);
                    else Neu.Raised(g, action, action.Height / 2, s, Neu.SmallDepth, hot ? UiTheme.Blend(t.Accent, Color.White, 0.08f) : t.Accent, convex: 1, rim: 1.3f);
                    UiIcons.Draw(g, UiIcon.Eye, new RectangleF(action.X + 10 * s, action.Y + action.Height / 2 - 7 * s, 14 * s, 14 * s), r.Watching ? t.Accent : t.AccentInk);
                    TextRenderer.DrawText(g, r.Watching ? "Открыто" : "Смотреть", UiTheme.Small, new Rectangle((int)(action.X + 28 * s), (int)action.Y, (int)(action.Width - 32 * s), (int)action.Height), r.Watching ? t.Accent : t.AccentInk, flags);
                    break;
                }
                case OwnStreamChip o:
                {
                    var amber = SoftButton.LiveColor;
                    ChipFace(g, bounds, radius, s, hot);
                    var pulse = new RectangleF(bounds.X + 16 * s, bounds.Y + bounds.Height / 2 - 5 * s, 10 * s, 10 * s);
                    using (var dot = new SolidBrush(Color.FromArgb(255, 255, 92, 92))) g.FillEllipse(dot, pulse);
                    var title = o.Kind == 1 ? "Экран в эфире" : "Камера в эфире";
                    var stop = new RectangleF(bounds.Right - 92 * s, bounds.Y + 7 * s, 84 * s, bounds.Height - 14 * s);
                    TextRenderer.DrawText(g, title, UiTheme.Strong, new Rectangle((int)(pulse.Right + 10 * s), (int)bounds.Y, (int)(stop.X - pulse.Right - 14 * s), (int)(bounds.Height * 0.62f)), t.Text, flags);
                    var viewers = o.Viewers switch { 0 => "пока никто не смотрит", 1 => "1 зритель", < 5 => $"{o.Viewers} зрителя", _ => $"{o.Viewers} зрителей" };
                    TextRenderer.DrawText(g, viewers + (o.Details.Length > 0 ? " · " + o.Details : ""), UiTheme.Small, new Rectangle((int)(pulse.Right + 10 * s), (int)(bounds.Y + bounds.Height * 0.5f), (int)(stop.X - pulse.Right - 14 * s), (int)(bounds.Height * 0.42f)), t.Muted, flags);
                    Neu.Raised(g, stop, stop.Height / 2, s, Neu.SmallDepth, hot ? UiTheme.Blend(amber, Color.White, 0.08f) : amber, convex: 1, rim: 1.3f);
                    UiIcons.Draw(g, UiIcon.Stop, new RectangleF(stop.X + 11 * s, stop.Y + stop.Height / 2 - 6 * s, 12 * s, 12 * s), Color.FromArgb(255, 40, 18, 6));
                    TextRenderer.DrawText(g, "Стоп", UiTheme.Strong, new Rectangle((int)(stop.X + 30 * s), (int)stop.Y, (int)(stop.Width - 34 * s), (int)stop.Height), Color.FromArgb(255, 40, 18, 6), flags);
                    break;
                }
            }
        }
    }
}

/// <summary>Row of theme swatches.</summary>
public sealed class ThemeSwatches : Control
{
    private string selected = UiTheme.Current.Id; private int hovered = -1;
    public event Action<string>? ThemeChosen;
    public ThemeSwatches() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); BackColor = UiTheme.Current.Surface; Cursor = Cursors.Hand; TabStop = true; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Selected { get => selected; set { selected = value; Invalidate(); } }
    private RectangleF Swatch(int i) { var s = DeviceDpi / 96f; var size = Math.Min(36 * s, Math.Min(Height - 16 * s, (Width - 16 * s) / UiTheme.All.Length - 14 * s)); return new RectangleF(8 * s + i * (size + 14 * s), (Height - size) / 2, size, size); }
    protected override void OnMouseMove(MouseEventArgs e) { var hit = Enumerable.Range(0, UiTheme.All.Length).FirstOrDefault(i => Swatch(i).Contains(e.Location), -1); if (hit != hovered) { hovered = hit; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        for (var i = 0; i < UiTheme.All.Length; i++) if (Swatch(i).Contains(e.Location)) { selected = UiTheme.All[i].Id; Invalidate(); ThemeChosen?.Invoke(selected); }
        base.OnMouseClick(e);
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var index = Array.FindIndex(UiTheme.All, x => x.Id == selected);
        if (e.KeyCode is Keys.Left or Keys.Right) { index = Math.Clamp(index + (e.KeyCode == Keys.Left ? -1 : 1), 0, UiTheme.All.Length - 1); selected = UiTheme.All[index].Id; Invalidate(); ThemeChosen?.Invoke(selected); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var g = e.Graphics; var s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < UiTheme.All.Length; i++)
        {
            var theme = UiTheme.All[i]; var r = Swatch(i);
            // The chosen theme sits pressed into the surface; the others are raised discs.
            var chosen = theme.Id == selected;
            if (chosen) Neu.Inset(g, r, r.Width / 2, s, 0.8f, theme.Background);
            else Neu.Raised(g, r, r.Width / 2, s, i == hovered ? Neu.ButtonDepth : Neu.SmallDepth, theme.Background, rim: 1);
            var dot = RectangleF.Inflate(r, -r.Width * 0.27f, -r.Height * 0.27f);
            using (var accent = new LinearGradientBrush(RectangleF.Inflate(dot, 1, 1), theme.Accent, theme.Glow, 45f)) g.FillEllipse(accent, dot);
            if (chosen) { using var ring = new Pen(UiTheme.WithAlpha(UiTheme.Current.Accent, 220), 1.6f * s); g.DrawEllipse(ring, RectangleF.Inflate(r, 3.5f * s, 3.5f * s)); }
        }
    }
}

/// <summary>Popup with a participant's voice and screen-sound volume. Changes apply instantly and only for you.</summary>
public sealed class PeerVolumePopup : ToolStripDropDown
{
    public PeerVolumePopup(string id, string name, double? ping, int voice, int share, Action<int, int> changed)
    {
        var s = DeviceDpi / 96f; var t = UiTheme.Current;
        Padding = Padding.Empty; Margin = Padding.Empty; DropShadowEnabled = false; BackColor = ThemeMenuRenderer.MenuBack; Renderer = new ThemeMenuRenderer();
        var card = new SurfacePanel { Gutter = 0, Radius = 0, Elevation = 0, FaceColor = ThemeMenuRenderer.MenuBack, Size = new Size((int)(300 * s), (int)(254 * s)), Padding = new Padding((int)(16 * s)), BackColor = ThemeMenuRenderer.MenuBack };
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty }; grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.Controls.Add(grid);
        void Row(Control c, int h) { var r = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h * s)); c.Dock = DockStyle.Fill; c.Margin = Padding.Empty; grid.Controls.Add(c, 0, r); }
        var header = new ConversationHeader(); header.Set(id, name, ping is { } ms ? $"Пинг {ms:0} мс · только для вас" : "Громкость только для вас", PresenceStatus.Unknown, false, false); Row(header, 54);
        UiLabel Caption(string text) => new() { Text = text, Font = UiTheme.Small, ForeColor = t.Muted, TextAlign = ContentAlignment.MiddleLeft };
        var voiceValue = Caption($"Голос · {voice}%"); Row(voiceValue, 22);
        var voiceSlider = new SlimSlider { Maximum = AudioLevels.MaximumPercent, Value = voice, AccessibleName = "Громкость голоса " + name }; Row(voiceSlider, 34);
        var shareValue = Caption($"Звук демонстрации · {share}%"); Row(shareValue, 22);
        var shareSlider = new SlimSlider { Maximum = AudioLevels.MaximumPercent, Value = share, AccessibleName = "Громкость демонстрации " + name }; Row(shareSlider, 34);
        var buttons = new BufferedGrid { ColumnCount = 3, Margin = Padding.Empty }; buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8 * s)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var mute = new SoftButton { Text = voice == 0 ? "Включить" : "Заглушить", Glyph = UiIcon.Volume, Crossed = voice != 0, Dock = DockStyle.Fill, Font = UiTheme.Small };
        var reset = new SoftButton { Text = "100%", Tone = ButtonTone.Quiet, Dock = DockStyle.Fill, Font = UiTheme.Small };
        buttons.Controls.Add(mute, 0, 0); buttons.Controls.Add(reset, 2, 0); Row(buttons, 44);
        var restore = voice == 0 ? 100 : voice;
        void Apply() { voiceValue.Text = $"Голос · {voiceSlider.Value}%"; shareValue.Text = $"Звук демонстрации · {shareSlider.Value}%"; mute.Text = voiceSlider.Value == 0 ? "Включить" : "Заглушить"; mute.Crossed = voiceSlider.Value != 0; mute.Invalidate(); changed(voiceSlider.Value, shareSlider.Value); }
        voiceSlider.ValueChanged += (_, _) => { if (voiceSlider.Value > 0) restore = voiceSlider.Value; Apply(); };
        shareSlider.ValueChanged += (_, _) => Apply();
        mute.Click += (_, _) => voiceSlider.Value = voiceSlider.Value == 0 ? restore : 0;
        reset.Click += (_, _) => { voiceSlider.Value = 100; shareSlider.Value = 100; };
        Items.Add(new ToolStripControlHost(card) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = card.Size });
        HandleCreated += (_, _) => WindowTheme.RoundPopup(Handle);
        UiPainting.ApplyBackgrounds(card);
    }
}

/// <summary>Small engraved well with an accent glyph: the icon of a settings section.</summary>
public sealed class GlyphMark : Control
{
    [System.ComponentModel.DefaultValue(UiIcon.None)] public UiIcon Glyph { get; set; }
    public GlyphMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        var s = DeviceDpi / 96f; var side = Math.Min(Width, Height) - 2 * s; if (side < 6) return;
        var r = new RectangleF((Width - side) / 2, (Height - side) / 2, side, side);
        Neu.Inset(e.Graphics, r, side / 2, s, 0.6f);
        UiIcons.Draw(e.Graphics, Glyph, RectangleF.Inflate(r, -side * 0.22f, -side * 0.22f), UiTheme.Current.Accent);
    }
}
