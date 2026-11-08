using System.Runtime.InteropServices;

namespace Voicecon;

public sealed partial class MainForm
{
    private enum TextTone { Normal, Muted, Accent }
    private RowStyle? incomingRow, manualRow;
    private bool manualExpanded;
    private void BuildLayout()
    {
        SuspendLayout(); Text = "Voicecon · своя команда"; Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
        ClientSize = new Size(1100, 840); MinimumSize = new Size(940, 700); StartPosition = FormStartPosition.CenterScreen;
        if (!previewMode && Screen.PrimaryScreen is { } screen) ClientSize = new Size(Math.Min(1100, screen.WorkingArea.Width - 32), Math.Min(840, screen.WorkingArea.Height - 64));
        var root = Grid(1, new Padding(26, 20, 26, 16)); Controls.Add(root);
        var header = Columns(52, -1, 115, 115, 145);
        header.Controls.Add(new BrandMark { Size = new Size(38, 38), Anchor = AnchorStyles.Left }, 0, 0);
        var brand = Grid(1); AddRow(brand, TextLabel("voicecon", 22, TextTone.Normal, true), 34); AddRow(brand, TextLabel("Своя команда. Всегда на связи.", 9, TextTone.Muted), 21); header.Controls.Add(brand, 1, 0);
        PrepareButton(callNav, "Звонок", UiIcon.Phone, ButtonTone.Primary); PrepareButton(videoNav, "Видео", UiIcon.Palette); PrepareButton(settingsNav, "Настройки", UiIcon.Sliders);
        callNav.Margin = new Padding(0, 8, 8, 8); videoNav.Margin = new Padding(0, 8, 8, 8); settingsNav.Margin = new Padding(0, 8, 0, 8);
        header.Controls.Add(callNav, 2, 0); header.Controls.Add(videoNav, 3, 0); header.Controls.Add(settingsNav, 4, 0); AddRow(root, header, 62);
        AddRow(root, Space(), 16); AddRow(root, BuildUpdateBanner(), 0); updateBannerRow = root.RowStyles[^1];
        pages.Dock = DockStyle.Fill; pages.Margin = Padding.Empty; AddRow(root, pages, 0); root.RowStyles[^1].SizeType = SizeType.Percent; root.RowStyles[^1].Height = 100;
        callPage = BuildCallPage(); settingsPage = BuildSettingsPage(); videoPage = BuildVideoPage();
        pages.Controls.Add(callPage); pages.Controls.Add(settingsPage); pages.Controls.Add(videoPage); settingsPage.Visible = videoPage.Visible = false;
        AddRow(root, Space(), 14);
        var bar = new SurfacePanel { Padding = new Padding(12), Dock = DockStyle.Fill };
        var controls = Columns(-34, 10, -34, 10, -32);
        PrepareButton(mute, "Микрофон", UiIcon.Microphone); PrepareButton(deafen, "Наушники", UiIcon.Headphones); PrepareButton(end, "Завершить", UiIcon.Phone, ButtonTone.Danger);
        controls.Controls.Add(mute, 0, 0); controls.Controls.Add(deafen, 2, 0); controls.Controls.Add(end, 4, 0); bar.Controls.Add(controls); AddRow(root, bar, 68);
        PrepareLabel(shortcutHint, TextTone.Muted); shortcutHint.Font = UiTheme.Small; UpdateShortcutHint(); AddRow(root, shortcutHint, 24);
        PrepareLabel(hint, TextTone.Muted); hint.Font = UiTheme.Small; hint.Text = "Добавьте друг друга по коду VCF1 один раз — дальше звоните кнопкой."; AddRow(root, hint, 24);
        var footer = Columns(-1, 168, 250); pingButton.Dock = DockStyle.Fill; pingButton.Click += (_, _) => ShowLatency(); PrepareLabel(metrics, TextTone.Muted); metrics.Font = UiTheme.Small; metrics.Text = "CPU —    RAM —    ↑— ↓— кбит/с";
        PrepareLabel(delivery, TextTone.Muted); delivery.Font = UiTheme.Small; delivery.TextAlign = ContentAlignment.MiddleRight; delivery.Text = previewMode ? "●  Доставка звонков" : "●  Подключаемся…";
        footer.Controls.Add(metrics, 0, 0); footer.Controls.Add(pingButton, 1, 0); footer.Controls.Add(delivery, 2, 0); AddRow(root, footer, 24);
        tips.SetToolTip(copy, "Копировать весь код подключения"); tips.SetToolTip(ownCard, "Скопировать постоянный код своего контакта");
        tips.SetToolTip(micKey, "Нажмите нужную комбинацию. Escape — отменить ввод."); tips.SetToolTip(earsKey, "Нажмите нужную комбинацию. Escape — отменить ввод.");
        ResumeLayout(true);
    }
    private Control BuildCallPage()
    {
        var body = Columns(-51, 16, -49);
        var roomCard = new SurfacePanel { Padding = new Padding(22, 18, 22, 18), AutoScroll = false, Dock = DockStyle.Fill };
        var content = Grid(1); content.Dock = DockStyle.Top; content.AutoSize = true; content.AutoSizeMode = AutoSizeMode.GrowAndShrink; var roomScroll = new ThemedScrollHost { Dock = DockStyle.Fill }; roomScroll.SetContent(content); roomCard.Controls.Add(roomScroll);
        var heading = Columns(-70, -30); heading.Controls.Add(TextLabel("КОМНАТА", 9, TextTone.Muted, true), 0, 0); PrepareLabel(statusBadge, TextTone.Accent); statusBadge.TextAlign = ContentAlignment.MiddleRight; statusBadge.Font = UiTheme.Small;
        heading.Controls.Add(statusBadge, 1, 0); AddRow(content, heading, 28);
        PrepareLabel(state); state.Font = UiTheme.Title; state.Text = "Соберите команду"; AddRow(content, state, 48);
        PrepareLabel(details, TextTone.Muted); details.Text = "Выберите друга и позвоните.\nДля первого знакомства обменяйтесь кодами VCF1."; details.Font = new Font("Segoe UI", 9.5f); AddRow(content, details, 50);
        people.Dock = DockStyle.Fill; people.WrapContents = true; people.AutoScroll = false; people.Margin = Padding.Empty; AddRow(content, people, 64); var levels = new SoftButton { Text = "Громкость участников", Glyph = UiIcon.Headphones, Tone = ButtonTone.Quiet }; levels.Click += (_, _) => ShowParticipantSettings(); AddRow(content, levels, 32);
        AddRow(content, TextLabel("ДРУЗЬЯ", 9, TextTone.Muted, true), 28);
        var friendRow = Columns(-1, 10, 135); PrepareCombo(friends); PrepareButton(callFriend, "Позвонить", UiIcon.Phone, ButtonTone.Primary); friendRow.Controls.Add(friends, 0, 0); friendRow.Controls.Add(callFriend, 2, 0); AddRow(content, friendRow, 42);
        var friendsActions = Columns(-50, 10, -50); PrepareButton(addFriend, "Добавить друга", UiIcon.Plus, ButtonTone.Quiet); PrepareButton(ownCard, "Мой код друга", UiIcon.Copy, ButtonTone.Quiet);
        friendsActions.Controls.Add(addFriend, 0, 0); friendsActions.Controls.Add(ownCard, 2, 0); AddRow(content, Space(), 8); AddRow(content, friendsActions, 36);
        incomingPanel.Dock = DockStyle.Fill; var incomingGrid = Grid(1); PrepareLabel(incomingTitle, TextTone.Accent); incomingTitle.Font = UiTheme.Strong; AddRow(incomingGrid, incomingTitle, 30);
        var incomingActions = Columns(-50, 10, -50); PrepareButton(acceptCall, "Принять", UiIcon.Phone, ButtonTone.Primary); PrepareButton(rejectCall, "Отклонить", UiIcon.Phone, ButtonTone.Danger);
        incomingActions.Controls.Add(acceptCall, 0, 0); incomingActions.Controls.Add(rejectCall, 2, 0); AddRow(incomingGrid, incomingActions, 40); incomingPanel.Controls.Add(incomingGrid); AddRow(content, incomingPanel, 0); incomingRow = content.RowStyles[^1];
        AddRow(content, Space(), 14);
        var disclosure = new SoftButton { Text = "Подключение по разовому коду", Glyph = UiIcon.Plus, Tone = ButtonTone.Quiet };
        disclosure.Click += (_, _) =>
        {
            manualExpanded = !manualExpanded; var start = manualRow!.Height; var target = manualExpanded ? 194 : 0;
            manualPanel.Visible = true;
            UiMotion.Run(manualPanel, this, 180, p => manualRow.Height = start + (target - start) * p, () => manualPanel.Visible = manualExpanded);
        };
        AddRow(content, disclosure, 36); manualPanel.Dock = DockStyle.Fill; var codes = Grid(1); manualPanel.Controls.Add(codes);
        PrepareButton(create, "Создать приглашение", UiIcon.Plus); AddRow(codes, create, 38); AddRow(codes, TextLabel("ВАШ КОД · отправьте другу", 8.5f, TextTone.Muted), 24);
        outgoing.ReadOnly = true; outgoing.Multiline = true; outgoing.PlaceholderText = "Приглашение или ответ"; outgoing.Font = UiTheme.Code; PrepareButton(copy, "", UiIcon.Copy); copy.AccessibleName = "Копировать код";
        AddRow(codes, CodeRow(outgoing, copy, 44), 42); AddRow(codes, TextLabel("КОД ДРУГА · приглашение или ответ", 8.5f, TextTone.Muted), 24);
        incoming.Multiline = true; incoming.MaxLength = 24000; incoming.PlaceholderText = "Вставьте полный код VC1.…"; incoming.Font = UiTheme.Code; PrepareButton(apply, "Применить", UiIcon.Arrow); AddRow(codes, CodeRow(incoming, apply, 120), 42);
        AddRow(content, manualPanel, 0); manualRow = content.RowStyles[^1]; manualPanel.Visible = false;
        body.Controls.Add(roomCard, 0, 0); body.Controls.Add(BuildChatPanel(), 2, 0); return body;
    }
    private Control BuildChatPanel()
    {
        var card = new SurfacePanel { Padding = new Padding(20), Dock = DockStyle.Fill }; var grid = Grid(1); card.Controls.Add(grid);
        AddRow(grid, TextLabel("Чат звонка", 15, TextTone.Normal, true), 34); AddRow(grid, TextLabel("Только участники вашей комнаты", 9, TextTone.Muted), 25);
        chatLog.Dock = DockStyle.Fill; chatLog.BorderStyle = BorderStyle.None; chatLog.ReadOnly = true; chatLog.DetectUrls = false; chatLog.Font = new Font("Segoe UI", 10); chatLog.ScrollBars = RichTextBoxScrollBars.Vertical;
        var logSurface = new SurfacePanel { Recessed = true, Padding = new Padding(12), Dock = DockStyle.Fill }; logSurface.Controls.Add(new ThemedTextScroll(chatLog)); AddRow(grid, logSurface, 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        AddRow(grid, Space(), 10); chatInput.Multiline = true; chatInput.MaxLength = 2000; chatInput.PlaceholderText = "Сообщение…"; AddRow(grid, InputSurface(chatInput, true), 64);
        var composer = Columns(44, -1, 125); PrepareButton(emoji, "☺", UiIcon.None); PrepareButton(sendChat, "Отправить", UiIcon.Arrow, ButtonTone.Primary);
        composer.Controls.Add(emoji, 0, 0); composer.Controls.Add(TextLabel("Enter — отправить", 8.5f, TextTone.Muted), 1, 0); composer.Controls.Add(sendChat, 2, 0); AddRow(grid, Space(), 8); AddRow(grid, composer, 38); return card;
    }
    private Control BuildSettingsPage()
    {
        var scroller = new ThemedScrollHost { Dock = DockStyle.Fill, Margin = Padding.Empty }; settingsScroller = scroller;
        var columns = Columns(-50, 16, -50); columns.Dock = DockStyle.Top; columns.Height = 1220;
        var audio = new SurfacePanel { Padding = new Padding(22, 18, 22, 18), Dock = DockStyle.Fill };
        var appearance = new SurfacePanel { Padding = new Padding(22, 18, 22, 18), Dock = DockStyle.Fill };
        columns.Controls.Add(audio, 0, 0); columns.Controls.Add(appearance, 2, 0); scroller.SetContent(columns);
        var sound = Grid(1); audio.Controls.Add(sound);
        var title = Columns(-1, 34); title.Controls.Add(TextLabel("Звук и устройства", 15, TextTone.Normal, true), 0, 0); PrepareButton(refresh, "", UiIcon.Refresh); title.Controls.Add(refresh, 1, 0); AddRow(sound, title, 36);
        AddRow(sound, TextLabel("Микрофон", 9, TextTone.Muted), 28); PrepareCombo(microphones); AddRow(sound, microphones, 36);
        AddRow(sound, TextLabel("Наушники", 9, TextTone.Muted), 28); PrepareCombo(speakers); AddRow(sound, speakers, 36);
        var loudness = Columns(-70, -30); loudness.Controls.Add(TextLabel("Общая громкость приложения", 9, TextTone.Muted), 0, 0); PrepareLabel(volumeValue, TextTone.Accent); volumeValue.TextAlign = ContentAlignment.MiddleRight; volumeValue.Text = $"{settings.Volume}%"; loudness.Controls.Add(volumeValue, 1, 0); AddRow(sound, loudness, 32);
        volume.Maximum = AudioLevels.MaximumPercent; volume.Value = settings.Volume; AddRow(sound, volume, 28); AddMicrophoneGain(sound); AddParticipantAudioSettings(sound); AddRow(sound, Space(), 12);
        SetupToggle(noise, "Шумоподавление", "Убирает постоянный тихий фон", settings.NoiseSuppression); SetupToggle(gate, "Тишина в паузах", "Закрывает микрофон, пока вы молчите", settings.NoiseGate); SetupToggle(ptt, "Говорить по кнопке", "Голос при удержании клавиши", settings.PushToTalk);
        AddRow(sound, noise, 53); AddRow(sound, gate, 53); AddRow(sound, ptt, 53); AddRow(sound, TextLabel("Клавиша разговора", 9, TextTone.Muted), 26);
        PrepareCombo(talkKey); talkKey.Items.AddRange([new KeyChoice("V", 0x56), new KeyChoice("Caps Lock", 0x14), new KeyChoice("Мышь 4", 0x05), new KeyChoice("Мышь 5", 0x06)]); talkKey.SelectedIndex = Math.Max(0, talkKey.Items.Cast<KeyChoice>().ToList().FindIndex(k => k.Code == settings.TalkKey)); AddRow(sound, talkKey, 36);
        AddRow(sound, Space(), 18); AddRow(sound, TextLabel("Видео по умолчанию", 12, TextTone.Normal, true), 30);
        PrepareCombo(videoQuality); videoQuality.Items.AddRange(["30 кадров/с", "60 кадров/с", "120 кадров/с"]); videoQuality.SelectedIndex = settings.VideoFps switch { 60 => 1, 120 => 2, _ => 0 }; AddRow(sound, videoQuality, 36);
        PrepareCombo(videoResolution); videoResolution.Items.AddRange(["720p", "1080p", "Оригинал источника"]); videoResolution.SelectedIndex = settings.VideoResolution switch { 1080 => 1, 0 => 2, _ => 0 }; AddRow(sound, Space(), 6); AddRow(sound, videoResolution, 36);
        AddRow(sound, TextLabel("Экран, окно, звук и точное разрешение выбираются\nперед началом демонстрации. Изменения — для следующего запуска.", 9, TextTone.Muted), 56);
        PrepareButton(network, "Сеть и доставка звонков", UiIcon.Sliders); AddRow(sound, network, 38); AddRow(sound, Space(), 0); sound.RowStyles[^1].SizeType = SizeType.Percent; sound.RowStyles[^1].Height = 100;

        var ui = Grid(1); appearance.Controls.Add(ui); AddRow(ui, TextLabel("Внешний вид и поведение", 15, TextTone.Normal, true), 36);
        AddRow(ui, TextLabel("Ваше имя", 9, TextTone.Muted), 24); name.MaxLength = 24; name.Text = settings.Name; AddRow(ui, InputSurface(name, false), 36);
        AddRow(ui, TextLabel("Тема", 9, TextTone.Muted), 26); PrepareCombo(themes); themes.Items.AddRange(UiTheme.All.Select(t => new Choice(t.Id, t.Name)).ToArray()); themes.SelectedIndex = Array.FindIndex(UiTheme.All, t => t.Id == UiTheme.Current.Id); AddRow(ui, themes, 36);
        themes.SelectedIndexChanged += (_, _) => { if (!applyingTheme && themes.SelectedItem is Choice choice) SwitchTheme(choice.Id); };
        SetupToggle(animations, "Плавные анимации", "Только при взаимодействии с окном", settings.Animations); SetupToggle(startup, "Запускать вместе с Windows", "Без окна, сразу в системном трее", settings.StartWithWindows); SetupToggle(notifications, "Звуки действий", "Кнопки, дозвон и подключение", settings.NotificationSounds);
        AddRow(ui, animations, 53); AddRow(ui, startup, 53); AddRow(ui, notifications, 53);
        AddRow(ui, TextLabel("Входящий звонок", 9, TextTone.Muted), 26); PrepareCombo(ringtones); ringtones.Items.AddRange([new Choice("bell", "Старый телефон"), new Choice("soft", "Мягкий звонок"), new Choice("custom", "Свой WAV-файл")]); ringtones.SelectedIndex = settings.Ringtone switch { "soft" => 1, "custom" => 2, _ => 0 }; AddRow(ui, ringtones, 36);
        var ringButtons = Columns(-50, 10, -50); var choose = new SoftButton { Text = "Выбрать WAV", Glyph = UiIcon.Plus, Dock = DockStyle.Fill }; var preview = new SoftButton { Text = "Прослушать", Glyph = UiIcon.Headphones, Dock = DockStyle.Fill };
        choose.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Звук WAV|*.wav", Title = "Мелодия входящего звонка", CheckFileExists = true }; if (picker.ShowDialog(this) == DialogResult.OK) { settings.RingtoneFile = picker.FileName; settings.Ringtone = "custom"; ringtones.SelectedIndex = 2; SaveSettings(); } };
        preview.Click += (_, _) => sounds.PlayRing(settings.Ringtone, settings.RingtoneFile, OutputDevice); ringButtons.Controls.Add(choose, 0, 0); ringButtons.Controls.Add(preview, 2, 0); AddRow(ui, Space(), 6); AddRow(ui, ringButtons, 34);
        AddRow(ui, TextLabel("Горячие клавиши · Esc — отмена · ПКМ — убрать", 10, TextTone.Normal, true), 32);
        AddHotkey(ui, "Микрофон", micKey, settings.MicrophoneHotkey); AddHotkey(ui, "Наушники", earsKey, settings.HeadphonesHotkey); AddHotkey(ui, "Открыть окно", openKey, settings.OpenWindowHotkey); AddHotkey(ui, "Демонстрация экрана", screenKey, settings.ScreenHotkey); AddHotkey(ui, "Камера", cameraKey, settings.CameraHotkey); AddUpdateSettings(ui);
        return scroller;
    }
    private static void AddHotkey(TableLayoutPanel grid, string label, HotkeyBox box, int chord)
    {
        var row = Columns(-52, -48); row.Controls.Add(TextLabel(label, 9, TextTone.Muted), 0, 0); box.Chord = (Keys)chord; box.Dock = DockStyle.Fill; row.Controls.Add(box, 1, 0); AddRow(grid, row, 36); AddRow(grid, Space(), 4);
    }
    private void ShowPage(bool showSettings) => ShowContent(showSettings ? settingsPage : callPage);
    private void ShowContent(Control page)
    {
        if (page.Visible) return; if (page != videoPage) CloseExpandedVideo();
        var old = pages.Controls.Cast<Control>().FirstOrDefault(c => c.Visible); PageFade.Switch(pages, old, page, this);
        viewingVideo = Visible && page == videoPage; if (!viewingVideo) SuspendVideoDecoders();
        callNav.Tone = page == callPage ? ButtonTone.Primary : ButtonTone.Secondary; settingsNav.Tone = page == settingsPage ? ButtonTone.Primary : ButtonTone.Secondary; videoNav.Tone = page == videoPage ? ButtonTone.Primary : ButtonTone.Secondary;
        callNav.Invalidate(); settingsNav.Invalidate(); videoNav.Invalidate();
    }
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
    private static Control Space() => new Panel { Margin = Padding.Empty };
    private static Label TextLabel(string text, float size = 10, TextTone tone = TextTone.Normal, bool bold = false)
    { var label = new Label { Text = text, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) }; PrepareLabel(label, tone); return label; }
    private static void PrepareLabel(Label label, TextTone tone = TextTone.Normal)
    { label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft; label.AutoEllipsis = true; label.Tag = tone; label.Margin = Padding.Empty; }
    private static void PrepareButton(SoftButton button, string text, UiIcon icon, ButtonTone tone = ButtonTone.Secondary)
    { button.Text = text; button.Glyph = icon; button.Tone = tone; button.Dock = DockStyle.Fill; }
    private static void PrepareCombo(ThemedComboBox combo) { combo.Dock = DockStyle.Fill; combo.Margin = Padding.Empty; }
    private static void SetupToggle(ToggleRow toggle, string text, string description, bool value) { toggle.Text = text; toggle.Description = description; toggle.Checked = value; }
    private static SurfacePanel InputSurface(TextBox input, bool multiline)
    {
        var surface = new SurfacePanel { Recessed = true, Padding = new Padding(11, multiline ? 7 : 8, 10, 5), Dock = DockStyle.Fill };
        input.BorderStyle = BorderStyle.None; input.Dock = DockStyle.Fill; input.Margin = Padding.Empty; input.ScrollBars = ScrollBars.None; surface.Controls.Add(input); return surface;
    }
    private static Control CodeRow(TextBox input, SoftButton action, int width)
    { var row = Columns(-1, 8, width); row.Controls.Add(InputSurface(input, true), 0, 0); row.Controls.Add(action, 2, 0); return row; }
    private void ApplyTheme(bool full = true)
    {
        WindowTheme.Apply(this); applyingTheme = true; var t = UiTheme.Current; BackColor = t.Background; ForeColor = t.Text; UiPainting.ApplyBackgrounds(this);
        void Visit(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is Label label) label.ForeColor = label.Tag as string == "live" ? t.Danger : label.Tag is TextTone.Accent ? t.Accent : label.Tag is TextTone.Muted ? t.Muted : t.Text;
                else if (child is TextBoxBase input) { input.BackColor = t.Field; input.ForeColor = t.Text; }
                else child.ForeColor = t.Text;
                Visit(child);
            }
        }
        Visit(this);
        if (full)
        {
            ApplyTitleBar(); participantSignature = ""; RenderParticipants(room); RenderChat();
            var old = ownedIcon; ownedIcon = BrandIcon.Create(); Icon = ownedIcon; if (tray is not null) { tray.Icon = ownedIcon; if (tray.ContextMenuStrip is { } menu) { menu.BackColor = t.Field; menu.ForeColor = t.Text; foreach (ToolStripItem item in menu.Items) item.ForeColor = t.Text; } } old?.Dispose();
        }
        Invalidate(true); applyingTheme = false;
    }
    private void SwitchTheme(string id)
    {
        settings.Theme = id; var from = UiTheme.Current; var to = UiTheme.All.First(t => t.Id == id);
        UiMotion.Run(themeTrack, this, 260, p => { UiTheme.Transition(from, to, p); ApplyTheme(false); }, () => { UiTheme.Select(id); ApplyTheme(); }); SaveSettings();
    }
    private void RenderParticipants(VoiceRoom? current)
    {
        var names = current is not null && current.ConnectedCount > 0 ? current.ParticipantNames : new[] { settings.Name + " (вы)" };
        var signature = string.Join("|", names) + UiTheme.Current.Id; if (signature == participantSignature) return; participantSignature = signature;
        people.SuspendLayout(); foreach (Control old in people.Controls.Cast<Control>().ToArray()) old.Dispose();
        foreach (var value in names) people.Controls.Add(new Label { Text = "●  " + value.Replace(" · в сети", ""), AutoSize = true, MaximumSize = new Size(210, 0), Font = UiTheme.Small, ForeColor = UiTheme.Current.Text, BackColor = UiTheme.Current.Field, Padding = new Padding(8, 5, 8, 5), Margin = new Padding(0, 2, 6, 4) });
        people.ResumeLayout(true);
    }
    private void UpdateStatusBadge()
    {
        var count = room is { IsDisposed: false } ? room.ConnectedCount : 0;
        statusBadge.Text = count > 0 ? $"●  {count + 1} / 6 в сети" : busy ? "●  Соединение" : "●  Не в звонке";
        if (incomingRow is not null) incomingRow.Height = incomingCall is null ? 0 : 78;
    }
    private string? PromptCode(string title, string description)
    {
        using var dialog = new ThemedDialog { Text = title, ClientSize = new Size(540, 280), StartPosition = FormStartPosition.CenterParent, BackColor = UiTheme.Current.Background, Font = UiTheme.Body, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Icon = Icon };
        var grid = Grid(1, new Padding(20)); dialog.Controls.Add(grid); AddRow(grid, TextLabel(description, 10, TextTone.Muted), 62);
        var text = new TextBox { Multiline = true, MaxLength = 4000 }; AddRow(grid, InputSurface(text, true), 105); AddRow(grid, Space(), 12);
        var ok = new SoftButton { Text = "Добавить контакт", Tone = ButtonTone.Primary, DialogResult = DialogResult.OK }; AddRow(grid, ok, 40); dialog.AcceptButton = ok; UiPainting.ApplyBackgrounds(dialog); text.ForeColor = UiTheme.Current.Text;
        return dialog.ShowDialog(this) == DialogResult.OK ? text.Text : null;
    }
    private void ApplyTitleBar() => WindowTheme.Apply(this);
}

