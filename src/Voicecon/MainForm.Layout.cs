namespace Voicecon;

public sealed partial class MainForm
{
    private enum TextTone { Normal, Muted, Accent }
    private readonly ContactListView contactList = new();
    private readonly ThemedScrollHost contactHost = new();
    private readonly SelfCard selfCard = new();
    private readonly ConversationHeader header = new();
    private readonly BufferedPeople actions = new();
    private readonly CallStrip callStrip = new();
    private readonly SurfacePanel incomingPanel = new();
    private readonly UiLabel incomingTitle = new(), hint = new(), metrics = new(), volumeValue = new();
    private readonly TextBox name = new(), chatInput = new();
    private readonly ChatView chatView = new();
    private readonly ThemedScrollHost chatHost = new();
    private readonly SoftButton callButton = new(), endButton = new(), shareButton = new(), inviteButton = new(), mute = new(), deafen = new(), addFriend = new(), ownCard = new(),
        acceptCall = new(), rejectCall = new(), sendChat = new(), emoji = new(), settingsButton = new(), olderMessages = new();
    private readonly ThemedComboBox microphones = new(), speakers = new(), talkKey = new(), ringtones = new();
    private readonly SegmentedPicker modePicker = new();
    private readonly ToggleRow ptt = new(), noise = new(), gate = new(), startup = new(), notifications = new();
    private readonly HotkeyBox micKey = new(), earsKey = new(), openKey = new(), screenKey = new(), cameraKey = new();
    private readonly SlimSlider volume = new();
    private readonly ToolTip tips = new() { InitialDelay = 450, AutoPopDelay = 6000 };
    private readonly BackdropPanel mainHost = new();
    private RowStyle? incomingRow, stripRow;
    private TableLayoutPanel? headerGrid;
    private bool applyingTheme;
    private IReadOnlyList<ChatRecord> previewChat = [];
    private readonly List<CallStrip.Chip> previewChips = [];

    private void BuildLayout()
    {
        SuspendLayout(); Text = "Voicecon"; Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        ClientSize = new Size(1200, 790); MinimumSize = new Size(980, 660); StartPosition = FormStartPosition.CenterScreen;
        if (!previewMode && Screen.PrimaryScreen is { } screen) ClientSize = new Size(Math.Min(1200, screen.WorkingArea.Width - 32), Math.Min(790, screen.WorkingArea.Height - 64));
        // Cards carry their own shadow gutters, so the grid itself needs almost no spacing.
        var root = Columns(324, -1); root.Padding = new Padding(6, 6, 6, 4); Controls.Add(root);
        root.Controls.Add(BuildSidebar(), 0, 0);
        mainHost.Dock = DockStyle.Fill; mainHost.Margin = Padding.Empty; mainHost.Controls.Add(BuildMain()); root.Controls.Add(mainHost, 1, 0);
        BuildSettingsDrawer();
        ResumeLayout(true);
    }

    private Control BuildSidebar()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 8) };
        var grid = Grid(1); card.Controls.Add(grid);
        var brand = Columns(46, -1, 46);
        brand.Controls.Add(new BrandMark { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
        var title = TextLabel("voicecon", 15, TextTone.Normal, true); title.Padding = new Padding(6, 0, 0, 2); brand.Controls.Add(title, 1, 0);
        PrepareButton(settingsButton, "", UiIcon.Gear, ButtonTone.Quiet); settingsButton.Circle = true; settingsButton.AccessibleName = "Настройки"; tips.SetToolTip(settingsButton, "Настройки");
        brand.Controls.Add(settingsButton, 2, 0); AddRow(grid, brand, 46);
        AddRow(grid, Space(), 10);
        var people = Columns(-1, 56);
        var contactsTitle = Grid(1); AddRow(contactsTitle, TextLabel("Контакты", 12, TextTone.Normal, true), 26); AddRow(contactsTitle, TextLabel("Друзья и группы", 8.5f, TextTone.Muted), 20);
        people.Controls.Add(contactsTitle, 0, 0);
        PrepareButton(addFriend, "", UiIcon.Plus, ButtonTone.Primary); addFriend.Circle = true; addFriend.AccessibleName = "Добавить друга по коду"; tips.SetToolTip(addFriend, "Добавить друга по коду");
        people.Controls.Add(addFriend, 1, 0); AddRow(grid, people, 54);
        AddRow(grid, Space(), 8);
        contactHost.Dock = DockStyle.Fill; contactHost.Margin = Padding.Empty; contactHost.SetContent(contactList);
        AddRow(grid, contactHost, 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        AddRow(grid, Divider(), 9);
        var self = Columns(-1, 46); selfCard.Dock = DockStyle.Fill; self.Controls.Add(selfCard, 0, 0);
        PrepareButton(ownCard, "", UiIcon.Copy, ButtonTone.Quiet); ownCard.Circle = true; ownCard.AccessibleName = "Скопировать мой код друга"; tips.SetToolTip(ownCard, "Скопировать мой код друга");
        self.Controls.Add(ownCard, 1, 0); AddRow(grid, self, 52);
        return card;
    }

    private Control BuildMain()
    {
        var grid = Grid(1); grid.Dock = DockStyle.Fill;
        AddRow(grid, BuildUpdateBanner(), 0); updateBannerRow = grid.RowStyles[^1];
        BuildIncomingPanel(); AddRow(grid, incomingPanel, 0); incomingRow = grid.RowStyles[^1]; incomingPanel.Visible = false;

        var headerCard = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 10, 6) };
        headerGrid = Columns(-1, 470); headerCard.Controls.Add(headerGrid);
        header.Dock = DockStyle.Fill; headerGrid.Controls.Add(header, 0, 0);
        actions.FlowDirection = FlowDirection.RightToLeft; actions.WrapContents = false; actions.Dock = DockStyle.Fill; actions.Padding = new Padding(0, 5, 0, 0); actions.Margin = Padding.Empty;
        PrepareButton(endButton, "Завершить звонок", UiIcon.HangUp, ButtonTone.Danger); endButton.Size = new Size(196, 54); endButton.Radius = 24;
        PrepareButton(callButton, "Позвонить", UiIcon.Phone, ButtonTone.Primary); callButton.Size = new Size(184, 54); callButton.Radius = 24;
        PrepareButton(shareButton, "Демо / камера", UiIcon.Screen, ButtonTone.Secondary); shareButton.Size = new Size(190, 54); shareButton.Radius = 24;
        PrepareButton(inviteButton, "", UiIcon.PersonAdd, ButtonTone.Secondary); inviteButton.Circle = true; inviteButton.Size = new Size(54, 54); inviteButton.AccessibleName = "Пригласить в звонок"; tips.SetToolTip(inviteButton, "Пригласить друга в этот звонок");
        foreach (var button in new[] { endButton, callButton, shareButton, inviteButton }) { button.Dock = DockStyle.None; button.Margin = new Padding(4, 0, 0, 0); actions.Controls.Add(button); }
        tips.SetToolTip(endButton, "Завершить звонок для себя"); tips.SetToolTip(shareButton, "Показать экран, окно или камеру участникам");
        headerGrid.Controls.Add(actions, 1, 0);
        AddRow(grid, headerCard, 104);

        callStrip.Dock = DockStyle.Fill; callStrip.Margin = Padding.Empty;
        var stripHost = new BackdropPanel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 0) }; stripHost.Controls.Add(callStrip);
        AddRow(grid, stripHost, 0); stripRow = grid.RowStyles[^1];

        AddRow(grid, BuildChatCard(), 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        AddRow(grid, BuildBottomBar(), 90);
        return grid;
    }

    private void BuildIncomingPanel()
    {
        incomingPanel.Dock = DockStyle.Fill; incomingPanel.Padding = new Padding(16, 6, 10, 6);
        var row = Columns(-1, 160, 4, 160);
        var text = Grid(1); AddRow(text, TextLabel("ВХОДЯЩИЙ ЗВОНОК", 8, TextTone.Accent, true), 22);
        PrepareLabel(incomingTitle); incomingTitle.Font = UiTheme.Heading; AddRow(text, incomingTitle, 30);
        PrepareButton(acceptCall, "Принять", UiIcon.Phone, ButtonTone.Primary); acceptCall.Radius = 22;
        PrepareButton(rejectCall, "Отклонить", UiIcon.HangUp, ButtonTone.Danger); rejectCall.Radius = 22;
        row.Controls.Add(text, 0, 0); row.Controls.Add(acceptCall, 1, 0); row.Controls.Add(rejectCall, 3, 0); incomingPanel.Controls.Add(row);
    }
    private void AnimateIncoming()
    {
        // A gentle breathing accent wash while the call rings (decorative: skipped in the minimal level).
        var accent = UiTheme.Current.Accent;
        incomingPanel.Wash = Color.FromArgb(34, accent); incomingPanel.Invalidate(true);
        if (UiMotion.Level != MotionLevel.Full) return;
        void Breathe(bool up)
        {
            var started = Environment.TickCount64;
            UiMotion.Run(incomingPanel, this, 900, p =>
            {
                incomingPanel.Wash = Color.FromArgb((int)(18 + 30 * (up ? p : 1 - p)), accent); incomingPanel.Invalidate(true);
            }, () =>
            {
                // A step that finished instantly means the window is hidden or unfocused: stop instead of spinning.
                if (incomingCall is null || incomingPanel.IsDisposed || Environment.TickCount64 - started < 500) return;
                Breathe(!up);
            }, decorative: true);
        }
        Breathe(true);
    }

    private Control BuildChatCard()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) }; var grid = Grid(1); card.Controls.Add(grid);
        var top = Columns(-1, 170);
        var titles = Grid(1); AddRow(titles, TextLabel("Чат", 12, TextTone.Normal, true), 24); AddRow(titles, TextLabel("Пишите в любое время · история хранится только на ваших ПК, до 300 МБ", 8.5f, TextTone.Muted), 18);
        top.Controls.Add(titles, 0, 0);
        PrepareButton(olderMessages, "Показать раньше", UiIcon.Chevron, ButtonTone.Quiet); olderMessages.Font = UiTheme.Small; olderMessages.Visible = false; top.Controls.Add(olderMessages, 1, 0);
        AddRow(grid, top, 46); AddRow(grid, Space(), 6);
        chatHost.Dock = DockStyle.Fill; chatHost.Margin = Padding.Empty; chatHost.SetContent(chatView);
        AddRow(grid, chatHost, 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        AddRow(grid, Space(), 10);
        var composer = Columns(54, 6, -1, 6, 54);
        PrepareButton(emoji, "", UiIcon.Smile, ButtonTone.Quiet); emoji.Circle = true; emoji.AccessibleName = "Эмодзи"; tips.SetToolTip(emoji, "Эмодзи");
        chatInput.Multiline = true; chatInput.MaxLength = 2000; chatInput.Font = UiTheme.Body;
        PrepareButton(sendChat, "", UiIcon.Send, ButtonTone.Primary); sendChat.Circle = true; sendChat.AccessibleName = "Отправить"; tips.SetToolTip(sendChat, "Отправить · Enter");
        composer.Controls.Add(emoji, 0, 0); composer.Controls.Add(InputSurface(chatInput, true), 2, 0); composer.Controls.Add(sendChat, 4, 0);
        AddRow(grid, composer, 54);
        return card;
    }

    private Control BuildBottomBar()
    {
        var bar = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6), Elevation = 0.8f, Radius = 20 };
        var row = Columns(350, 6, 140, 8, -1, 6, 54, 2, 54);
        modePicker.Options.Add(new("Обычный", "24 кбит/с", "Лучшее качество голоса"));
        modePicker.Options.Add(new("Игровой", "16 кбит/с", "Меньше нагрузки на сеть во время игры"));
        modePicker.Options.Add(new("Эконом", "8 кбит/с", "Минимальный трафик"));
        modePicker.SetSilently(settings.AudioMode switch { VoiceMode.Normal => 0, VoiceMode.Economy => 2, _ => 1 }); modePicker.AccessibleName = "Режим звонка"; modePicker.Dock = DockStyle.Fill;
        tips.SetToolTip(modePicker, "Режим звонка: применяется к вашему голосу сразу, даже во время разговора");
        row.Controls.Add(modePicker, 0, 0);
        pingButton.Dock = DockStyle.Fill; pingButton.Glyph = UiIcon.Info; pingButton.Font = UiTheme.Small; pingButton.Click += (_, _) => ShowLatency(); tips.SetToolTip(pingButton, "Задержка до участников и маршрут · график за 2 минуты");
        row.Controls.Add(pingButton, 2, 0);
        var texts = Grid(1); PrepareLabel(hint, TextTone.Muted); hint.Font = UiTheme.Small; hint.Text = "Добавьте друга кнопкой + и звоните в один клик.";
        PrepareLabel(metrics, TextTone.Muted); metrics.Font = UiTheme.Small; AddRow(texts, hint, 26); AddRow(texts, metrics, 20);
        row.Controls.Add(texts, 4, 0);
        PrepareButton(mute, "", UiIcon.Microphone); mute.Circle = true; mute.AccessibleName = "Микрофон";
        PrepareButton(deafen, "", UiIcon.Headphones); deafen.Circle = true; deafen.AccessibleName = "Наушники";
        row.Controls.Add(mute, 6, 0); row.Controls.Add(deafen, 8, 0);
        bar.Controls.Add(row); return bar;
    }

    private void LoadPreviewData()
    {
        // Offscreen renders and device-free checks: sample content only, nothing read from disk or network.
        var now = DateTimeOffset.Now;
        contactList.SetEntries(
            [new ContactEntry(ContactKind.Group, "0f0e0d0c0b0a09080706050403020100", "Вечерний рейд", "4 участника · 2 в сети", PresenceStatus.Unknown)],
            [new ContactEntry(ContactKind.Friend, "a1b2c3d4e5f60718293a4b5c6d7e8f90", "Алекс", "в сети", PresenceStatus.Online),
             new ContactEntry(ContactKind.Friend, "b2c3d4e5f60718293a4b5c6d7e8f90a1", "Мира", "в сети · в звонке", PresenceStatus.InCall),
             new ContactEntry(ContactKind.Friend, "c3d4e5f60718293a4b5c6d7e8f90a1b2", "Денис", "не в сети · был(а) в 21:40", PresenceStatus.Offline)],
            "f:a1b2c3d4e5f60718293a4b5c6d7e8f90");
        previewChat =
        [
            new("1", "Алекс", "Заходишь вечером? Собираем рейд 🎮", false, now.AddMinutes(-42)),
            new("2", "Игрок", "Да, в девять буду. Включу демку, покажу сборку", true, now.AddMinutes(-40)),
            new("3", "Алекс", "Отлично, режим «Игровой» уже стоит 👍", false, now.AddMinutes(-39)),
        ];
        selected = new Conversation("f:a1b2c3d4e5f60718293a4b5c6d7e8f90", ContactKind.Friend, "a1b2c3d4e5f60718293a4b5c6d7e8f90", "Алекс");
        selfCard.Set("ffeeddccbbaa99887766554433221100", settings.Name, "В сети · входящие доступны", true);
        LoadHistory(); UpdateHeader();
    }
    /// <summary>Render-only: shows the in-call look (participants and streams) without a real room.</summary>
    private void ShowPreviewCall()
    {
        previewChips.Clear();
        previewChips.Add(new CallStrip.OwnStreamChip(1, 2, "720p · 30 к/с"));
        previewChips.Add(new CallStrip.PersonChip("a1b2c3d4e5f60718293a4b5c6d7e8f90", "Алекс", true, 28));
        previewChips.Add(new CallStrip.PersonChip("b2c3d4e5f60718293a4b5c6d7e8f90a1", "Мира", true, 41));
        previewChips.Add(new CallStrip.RemoteStreamChip("b2c3d4e5f60718293a4b5c6d7e8f90a1", "Мира", 2, false));
        SetShown(callButton, false); SetShown(endButton, true); SetShown(shareButton, true); SetShown(inviteButton, true); endButton.Text = "Завершить звонок";
        shareButton.Text = "Остановить показ"; shareButton.Tone = ButtonTone.Live; shareButton.Glyph = UiIcon.Stop;
        header.Set("a1b2c3d4e5f60718293a4b5c6d7e8f90", "Алекс", "В звонке · 3 участника · 12:48", PresenceStatus.InCall, false, true);
        UpdateStrip(); if (stripRow is not null) stripRow.Height = callStrip.DesiredHeight(Math.Max(600, callStrip.Width));
        FitActions();
    }

    /// <summary>The action column takes exactly the width of the visible buttons, leaving the rest to the conversation title.</summary>
    private void FitActions()
    {
        if (headerGrid is null) return;
        var width = actions.Controls.Cast<Control>().Where(c => c.Tag is true).Sum(c => c.Width + c.Margin.Horizontal) + actions.Padding.Horizontal + 4;
        var scaled = (float)width; // button widths and absolute styles are scaled together by WinForms
        if (Math.Abs(headerGrid.ColumnStyles[1].Width - scaled) > 0.5f) headerGrid.ColumnStyles[1].Width = scaled;
        actions.PerformLayout();
    }

    // ---------------- helpers ----------------
    private static TableLayoutPanel Grid(int columns, Padding? padding = null)
    {
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = columns > 1 ? 1 : 0, BackColor = UiTheme.Current.Surface, Padding = padding ?? Padding.Empty, Margin = Padding.Empty };
        if (columns > 1) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); else grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return grid;
    }
    private static TableLayoutPanel Columns(params int[] widths)
    {
        var grid = Grid(widths.Length);
        foreach (var width in widths) grid.ColumnStyles.Add(new ColumnStyle(width > 0 ? SizeType.Absolute : SizeType.Percent, Math.Abs(width))); return grid;
    }
    private static void AddRow(TableLayoutPanel grid, Control child, int height)
    { var row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); child.Dock = DockStyle.Fill; child.Margin = Padding.Empty; grid.Controls.Add(child, 0, row); }
    private static Control Space() => new BackdropPanel { Margin = Padding.Empty };
    private static Control Divider() => new DividerLine();
    private static UiLabel TextLabel(string text, float size = 10, TextTone tone = TextTone.Normal, bool bold = false)
    { var label = new UiLabel { Text = text, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) }; PrepareLabel(label, tone); return label; }
    private static void PrepareLabel(Label label, TextTone tone = TextTone.Normal)
    { label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft; label.AutoEllipsis = true; label.Tag = tone; label.Margin = Padding.Empty; }
    private static void PrepareButton(SoftButton button, string text, UiIcon icon, ButtonTone tone = ButtonTone.Secondary)
    { button.Text = text; button.Glyph = icon; button.Tone = tone; button.Dock = DockStyle.Fill; }
    private static void PrepareCombo(ThemedComboBox combo) { combo.Dock = DockStyle.Fill; combo.Margin = Padding.Empty; }
    private static void SetupToggle(ToggleRow toggle, string text, string description, bool value) { toggle.Text = text; toggle.Description = description; toggle.Checked = value; }
    private static SurfacePanel InputSurface(TextBox input, bool multiline)
    {
        var surface = new SurfacePanel { Recessed = true, Radius = multiline ? 20 : 14, Padding = new Padding(16, multiline ? 12 : 11, 14, 6), Dock = DockStyle.Fill };
        input.BorderStyle = BorderStyle.None; input.Dock = DockStyle.Fill; input.Margin = Padding.Empty; input.ScrollBars = ScrollBars.None; surface.Controls.Add(input); return surface;
    }

    private void ApplyTheme(bool full = true)
    {
        WindowTheme.Apply(this); applyingTheme = true; var t = UiTheme.Current; BackColor = t.Background; ForeColor = t.Text;
        Backdrop.Invalidate(this); UiPainting.ApplyBackgrounds(this);
        void Visit(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is Label label) label.ForeColor = label.Tag is TextTone.Accent ? t.Accent : label.Tag is TextTone.Muted ? t.Muted : t.Text;
                else if (child is TextBoxBase input) { input.BackColor = t.Field; input.ForeColor = t.Text; }
                else child.ForeColor = t.Text;
                Visit(child);
            }
        }
        Visit(this); Visit(settingsDrawer);
        UiPainting.ApplyBackgrounds(settingsDrawer);
        if (full)
        {
            ApplyTitleBar(); LoadHistory();
            var old = ownedIcon; ownedIcon = BrandIcon.Create(); Icon = ownedIcon; if (tray is not null) { tray.Icon = ownedIcon; if (tray.ContextMenuStrip is { } menu) { menu.BackColor = ThemeMenuRenderer.MenuBack; menu.ForeColor = t.Text; foreach (ToolStripItem item in menu.Items) item.ForeColor = t.Text; } } old?.Dispose();
            foreach (var view in streamWindows.Values) { var window = view.Window; WindowTheme.Apply(window); Backdrop.Invalidate(window); UiPainting.ApplyBackgrounds(window); window.Invalidate(true); }
        }
        Invalidate(true); applyingTheme = false;
    }
    private void SwitchTheme(string id)
    {
        settings.Theme = id; var from = UiTheme.Current; var to = UiTheme.All.First(t => t.Id == id);
        UiMotion.Run(themeTrack, this, 320, p => { UiTheme.Transition(from, to, p); ApplyTheme(false); }, () => { UiTheme.Select(id); ApplyTheme(); }, decorative: true); SaveSettings();
    }
    private void ApplyTitleBar() => WindowTheme.Apply(this);
}

/// <summary>Engraved separator: a dark groove with a light lip under it, fading out at both ends.</summary>
public sealed class DividerLine : Control
{
    public DividerLine() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true); BackColor = UiTheme.Current.Surface; Margin = Padding.Empty; }
    protected override void OnPaint(PaintEventArgs e)
    {
        Backdrop.Paint(e.Graphics, this, ClientRectangle);
        if (Width < 4) return; var y = (int)(Height / 2f);
        void Line(Color color, int offset)
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0, y), new PointF(Width, y), Color.Transparent, Color.Transparent);
            brush.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend { Colors = [Color.FromArgb(0, color), color, color, Color.FromArgb(0, color)], Positions = [0f, 0.12f, 0.88f, 1f] };
            using var pen = new Pen(brush, 1); e.Graphics.DrawLine(pen, 0, y + offset, Width, y + offset);
        }
        var dark = UiTheme.Current.Dark;
        Line(dark ? Color.FromArgb(150, UiTheme.Blend(Backdrop.Behind(this), Color.Black, 0.55f)) : Color.FromArgb(90, 120, 132, 160), 0);
        Line(Color.FromArgb(dark ? 60 : 255, 255, 255, 255), 1);
    }
}
