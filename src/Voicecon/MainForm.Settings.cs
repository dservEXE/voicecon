using System.Diagnostics;

namespace Voicecon;

public sealed partial class MainForm
{
    /// <summary>The settings page: a full sheet over the conversation area that slides in from the right.</summary>
    private readonly BackdropPanel settingsDrawer = new();
    private readonly ThemedScrollHost settingsScroller = new() { Dock = DockStyle.Fill };
    private readonly SlimSlider microphoneGain = new();
    private readonly UiLabel microphoneGainValue = new(), chatUsage = new();
    private readonly ThemeSwatches themes = new();
    private readonly SegmentedPicker animationPicker = new();
    private readonly ToggleRow messageSounds = new(), showOnline = new(), showMetrics = new(), backgroundGlow = new();
    private readonly SoftButton closeSettings = new(), refresh = new(), network = new(), devToggle = new(), manualCodes = new(), clearHistory = new(), openLogs = new(), aboutButton = new(), copyCode = new();
    private TableLayoutPanel? devPanel, settingsPage, settingsTop;
    private RowStyle? devRow, devCardRow;
    private Control? devAnchor;
    private bool settingsOpen, devExpanded;
    private float drawerProgress;
    private readonly SlideOverlay slide = new();
    private const int SettingsColumn = 720;

    private void BuildSettingsDrawer()
    {
        settingsDrawer.Visible = false;
        var outer = Grid(1); settingsDrawer.Controls.Add(outer);
        settingsTop = Columns(-1, 60); settingsTop.Padding = new Padding(0, 8, 0, 0);
        var titles = Grid(1); AddRow(titles, TextLabel("Настройки", 17, TextTone.Normal, true), 32); AddRow(titles, TextLabel("Изменения применяются сразу", 8.5f, TextTone.Muted), 20);
        settingsTop.Controls.Add(titles, 0, 0);
        PrepareButton(closeSettings, "", UiIcon.Close, ButtonTone.Secondary); closeSettings.Circle = true; closeSettings.AccessibleName = "Закрыть настройки"; tips.SetToolTip(closeSettings, "Закрыть · Esc"); settingsTop.Controls.Add(closeSettings, 1, 0);
        AddRow(outer, settingsTop, 66);
        AddRow(outer, settingsScroller, 0); outer.RowStyles[^1].SizeType = SizeType.Percent; outer.RowStyles[^1].Height = 100;
        var page = Grid(1); page.Dock = DockStyle.Top; page.AutoSize = true; page.AutoSizeMode = AutoSizeMode.GrowAndShrink; page.Padding = new Padding(0, 0, 0, 18); settingsPage = page;
        settingsScroller.SetContent(page); settingsScroller.SizeChanged += (_, _) => CenterSettings();

        var profile = SettingsCard("Профиль", UiIcon.Person);
        AddRow(profile, TextLabel("Имя для друзей", 9, TextTone.Muted), 24); name.MaxLength = 24; name.Text = settings.Name; AddRow(profile, InputSurface(name, false), 42);
        AddRow(profile, Space(), 8); PrepareButton(copyCode, "Скопировать мой код друга", UiIcon.Copy); AddRow(profile, copyCode, 48);
        FinishCard(page, profile);

        var sound = SettingsCard("Звук", UiIcon.Headphones);
        AddRow(sound, TextLabel("Микрофон", 9, TextTone.Muted), 24);
        var micRow = Columns(-1, 8, 48); PrepareCombo(microphones); micRow.Controls.Add(microphones, 0, 0);
        PrepareButton(refresh, "", UiIcon.Refresh, ButtonTone.Quiet); refresh.Circle = true; refresh.AccessibleName = "Обновить список устройств"; tips.SetToolTip(refresh, "Обновить список устройств"); micRow.Controls.Add(refresh, 2, 0); AddRow(sound, micRow, 48);
        AddRow(sound, TextLabel("Наушники", 9, TextTone.Muted), 24); PrepareCombo(speakers); AddRow(sound, speakers, 48);
        AddLevel(sound, "Громкость приложения", volume, volumeValue, settings.Volume);
        AddLevel(sound, "Усиление микрофона", microphoneGain, microphoneGainValue, settings.MicrophoneGain);
        AddRow(sound, Space(), 6);
        SetupToggle(noise, "Шумоподавление", "Убирает ровный фон и гул", settings.NoiseSuppression); AddRow(sound, noise, 52);
        SetupToggle(ptt, "Говорить по кнопке", "Голос передаётся, пока клавиша нажата", settings.PushToTalk); AddRow(sound, ptt, 52);
        var keyRow = Columns(-1, 190); keyRow.Controls.Add(TextLabel("Клавиша разговора", 9, TextTone.Muted), 0, 0);
        PrepareCombo(talkKey); talkKey.Items.AddRange([new KeyChoice("V", 0x56), new KeyChoice("Caps Lock", 0x14), new KeyChoice("Мышь 4", 0x05), new KeyChoice("Мышь 5", 0x06)]);
        talkKey.SelectedIndex = Math.Max(0, talkKey.Items.Cast<KeyChoice>().ToList().FindIndex(k => k.Code == settings.TalkKey)); talkKey.Enabled = settings.PushToTalk; keyRow.Controls.Add(talkKey, 1, 0); AddRow(sound, keyRow, 48);
        FinishCard(page, sound);

        var alerts = SettingsCard("Уведомления", UiIcon.Volume);
        SetupToggle(notifications, "Звуки событий", "Подключения, демонстрации, зрители. Без звуков нажатий", settings.NotificationSounds); AddRow(alerts, notifications, 52);
        SetupToggle(messageSounds, "Звук новых сообщений", "Тихая капля, когда пишут в чат звонка", settings.MessageSounds); AddRow(alerts, messageSounds, 52);
        AddRow(alerts, TextLabel("Мелодия входящего звонка", 9, TextTone.Muted), 26);
        PrepareCombo(ringtones); ringtones.Items.AddRange([new Choice("boris", "Flying Bird · Boris Brejcha"), new Choice("birdmachine", "Bird Machine · Jingle Bells"), new Choice("soft", "Капли · мягкий"), new Choice("bell", "Старый телефон"), new Choice("custom", "Свой WAV или MP3")]);
        ringtones.SelectedIndex = settings.Ringtone switch { "birdmachine" => 1, "soft" => 2, "bell" => 3, "custom" => 4, _ => 0 }; AddRow(alerts, ringtones, 48);
        var ringButtons = Columns(-50, 8, -50);
        var choose = new SoftButton { Text = "Свой файл", Glyph = UiIcon.Folder, Tone = ButtonTone.Secondary, Dock = DockStyle.Fill }; var preview = new SoftButton { Text = "Прослушать", Glyph = UiIcon.Volume, Tone = ButtonTone.Secondary, Dock = DockStyle.Fill };
        choose.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Звук WAV или MP3|*.wav;*.mp3", Title = "Мелодия входящего звонка", CheckFileExists = true }; if (picker.ShowDialog(this) == DialogResult.OK) { settings.RingtoneFile = picker.FileName; settings.Ringtone = "custom"; ringtones.SelectedIndex = 4; SaveSettings(); } };
        preview.Click += (_, _) => sounds.PlayRing(settings.Ringtone, settings.RingtoneFile, OutputDevice);
        ringButtons.Controls.Add(choose, 0, 0); ringButtons.Controls.Add(preview, 2, 0); AddRow(alerts, ringButtons, 48);
        FinishCard(page, alerts);

        var look = SettingsCard("Оформление", UiIcon.Palette);
        AddRow(look, TextLabel("Тема", 9, TextTone.Muted), 24); themes.Selected = UiTheme.Current.Id; themes.AccessibleName = "Тема оформления"; AddRow(look, themes, 58);
        AddRow(look, TextLabel("Анимации", 9, TextTone.Muted), 26);
        animationPicker.Options.Add(new("Полные")); animationPicker.Options.Add(new("Минимум")); animationPicker.Options.Add(new("Выкл."));
        animationPicker.SetSilently(UiMotion.Parse(settings.AnimationLevel) switch { MotionLevel.Minimal => 1, MotionLevel.Off => 2, _ => 0 }); animationPicker.AccessibleName = "Анимации";
        tips.SetToolTip(animationPicker, "Минимум: короткие переходы без декоративных эффектов. Выкл.: всё мгновенно.");
        AddRow(look, animationPicker, 48);
        AddRow(look, Space(), 6);
        SetupToggle(backgroundGlow, "Цветное переливание фона", "Мягкие пятна цвета вокруг карточек, медленно плывут", settings.BackgroundGlow); AddRow(look, backgroundGlow, 52);
        FinishCard(page, look);

        var keys = SettingsCard("Горячие клавиши", UiIcon.Sliders);
        AddRow(keys, TextLabel("Пусто — не назначено. Нажмите поле и клавишу · Esc — отмена · ПКМ — убрать", 8.5f, TextTone.Muted), 30);
        AddHotkey(keys, "Микрофон", micKey, settings.MicrophoneHotkey); AddHotkey(keys, "Наушники и весь звук", earsKey, settings.HeadphonesHotkey); AddHotkey(keys, "Открыть окно", openKey, settings.OpenWindowHotkey);
        AddHotkey(keys, "Демонстрация экрана", screenKey, settings.ScreenHotkey); AddHotkey(keys, "Камера", cameraKey, settings.CameraHotkey);
        FinishCard(page, keys);

        var system = SettingsCard("Система", UiIcon.Gear);
        SetupToggle(startup, "Запускать вместе с Windows", "Сразу в трее, без окна", settings.StartWithWindows); AddRow(system, startup, 52);
        SetupToggle(showOnline, "Показывать друзьям, что я в сети", "Без имени и адресов: только «в сети» или «в звонке»", settings.ShowOnlineStatus); AddRow(system, showOnline, 52);
        AddUpdateSettings(system);
        FinishCard(page, system);

        var dev = SettingsCard("Для разработчиков", UiIcon.Info, collapsed: true);
        PrepareButton(devToggle, "Показать раздел", UiIcon.Chevron, ButtonTone.Secondary); devToggle.Font = UiTheme.Small; AddRow(dev, devToggle, 46); devAnchor = devToggle;
        devPanel = Grid(1);
        AddRow(devPanel, Space(), 8);
        SetupToggle(gate, "Тишина в паузах", "Закрывает микрофон ниже порога громкости", settings.NoiseGate); AddRow(devPanel, gate, 52);
        SetupToggle(showMetrics, "Показывать нагрузку и трафик", "CPU, память и кбит/с внизу окна", settings.ShowMetrics); AddRow(devPanel, showMetrics, 52);
        PrepareButton(network, "Сеть: STUN, TURN и доставка", UiIcon.Sliders); AddRow(devPanel, network, 48); AddRow(devPanel, Space(), 4);
        PrepareButton(manualCodes, "Подключение по разовому коду", UiIcon.Copy); AddRow(devPanel, manualCodes, 48); AddRow(devPanel, Space(), 10);
        PrepareLabel(chatUsage, TextTone.Muted); chatUsage.Font = UiTheme.Small; AddRow(devPanel, chatUsage, 22);
        PrepareButton(clearHistory, "Очистить историю чатов", UiIcon.Trash, ButtonTone.Quiet); AddRow(devPanel, clearHistory, 44);
        PrepareButton(openLogs, "Открыть папку журнала", UiIcon.Folder, ButtonTone.Quiet); AddRow(devPanel, openLogs, 44);
        PrepareButton(aboutButton, "О приложении и лицензии", UiIcon.Info, ButtonTone.Quiet); AddRow(devPanel, aboutButton, 44);
        var devIndex = dev.RowCount; AddRow(dev, devPanel, 0); devRow = dev.RowStyles[devIndex]; devPanel.Visible = false;
        devCardRow = FinishCard(page, dev);

        mainHost.Controls.Add(settingsDrawer); settingsDrawer.BringToFront();
        mainHost.Controls.Add(slide); slide.BringToFront();
    }
    /// <summary>A raised section card with an icon and a title; rows are added to the returned grid.</summary>
    private static TableLayoutPanel SettingsCard(string title, UiIcon icon, bool collapsed = false)
    {
        var inner = Grid(1);
        var head = Columns(30, -1); head.Controls.Add(new GlyphMark { Glyph = icon, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
        var label = TextLabel(title, 11.5f, TextTone.Normal, true); label.Padding = new Padding(4, 0, 0, 0); head.Controls.Add(label, 1, 0);
        AddRow(inner, head, 34); if (!collapsed) AddRow(inner, Space(), 6);
        return inner;
    }
    /// <summary>Wraps the grid in a card sized to its rows and adds it to the page.</summary>
    private static RowStyle FinishCard(TableLayoutPanel page, TableLayoutPanel inner)
    {
        var card = new SurfacePanel { Padding = new Padding(22, 16, 22, 16) }; card.Controls.Add(inner);
        var height = inner.RowStyles.Cast<RowStyle>().Sum(r => r.Height) + card.Padding.Vertical + 2 * Neu.Extent(card.Elevation, 1);
        AddRow(page, card, (int)Math.Ceiling(height)); return page.RowStyles[^1];
    }
    /// <summary>Keeps the settings in one readable column centred on wide windows.</summary>
    private void CenterSettings()
    {
        if (settingsPage is null || settingsTop is null) return;
        var scale = DeviceDpi / 96f; var gutter = Neu.Extent(1, scale);
        var side = Math.Max(0, (settingsScroller.ClientSize.Width - (int)(SettingsColumn * scale)) / 2);
        if (settingsPage.Padding.Left != side) settingsPage.Padding = new Padding(side, 0, side, settingsPage.Padding.Bottom);
        var top = new Padding(side + gutter + (int)(6 * scale), settingsTop.Padding.Top, side + gutter - (int)(4 * scale), 0);
        if (settingsTop.Padding != top) settingsTop.Padding = top;
    }
    private static void AddLevel(TableLayoutPanel grid, string title, SlimSlider slider, UiLabel value, int initial)
    {
        var row = Columns(-1, 70); row.Controls.Add(TextLabel(title, 9, TextTone.Muted), 0, 0); PrepareLabel(value, TextTone.Accent); value.TextAlign = ContentAlignment.MiddleRight; value.Text = $"{initial}%"; row.Controls.Add(value, 1, 0);
        AddRow(grid, row, 28); slider.Maximum = AudioLevels.MaximumPercent; slider.Value = initial; AddRow(grid, slider, 36);
    }
    private static void AddHotkey(TableLayoutPanel grid, string label, HotkeyBox box, int chord)
    {
        var row = Columns(-48, -52); row.Controls.Add(TextLabel(label, 9, TextTone.Muted), 0, 0); box.Chord = (Keys)chord; box.Dock = DockStyle.Fill; row.Controls.Add(box, 1, 0); AddRow(grid, row, 40); AddRow(grid, Space(), 4);
        box.AccessibleName = "Горячая клавиша: " + label;
    }

    private void WireSettings()
    {
        closeSettings.Click += (_, _) => CloseSettings();
        copyCode.Click += (_, _) => ShowOwnCard();
        refresh.Click += (_, _) => LoadDevices();
        network.Click += (_, _) =>
        {
            var oldServer = settings.SignalingServer;
            using var dialog = new NetworkDialog(settings);
            if (dialog.ShowDialog(this) == DialogResult.OK) { SaveSettings(); if (oldServer != settings.SignalingServer) { StartMailbox(); StartPresence(); } }
        };
        foreach (var check in new[] { ptt, noise, gate }) check.CheckedChanged += (_, _) => { UpdateAudioOptions(); SaveSettings(); };
        talkKey.SelectedIndexChanged += (_, _) => { UpdateAudioOptions(); SaveSettings(); };
        volume.ValueChanged += (_, _) => { UpdateAudioOptions(); SaveSettings(); };
        microphoneGain.ValueChanged += (_, _) => { microphoneGainValue.Text = $"{microphoneGain.Value}%"; settings.MicrophoneGain = microphoneGain.Value; room?.ApplyAudioSettings(); SaveSettings(); };
        name.Leave += (_, _) => { SaveSettings(); if (contacts is not null) presence?.UpdateOwn(contacts.OwnCard(settings.Name, settings.SignalingServer)); };
        microphones.SelectedIndexChanged += (_, _) => SaveSettings(); speakers.SelectedIndexChanged += (_, _) => SaveSettings();
        startup.CheckedChanged += (_, _) =>
        {
            try { StartupManager.SetEnabled(startup.Checked); settings.StartWithWindows = startup.Checked; SaveSettings(); }
            catch (Exception ex) { SetHint("Не удалось изменить автозагрузку: " + ex.Message); }
        };
        showOnline.CheckedChanged += (_, _) => { settings.ShowOnlineStatus = showOnline.Checked; presence?.SetVisible(showOnline.Checked); SaveSettings(); };
        showMetrics.CheckedChanged += (_, _) => { settings.ShowMetrics = showMetrics.Checked; if (!settings.ShowMetrics) metrics.Text = ""; SaveSettings(); };
        notifications.CheckedChanged += (_, _) => { settings.NotificationSounds = notifications.Checked; if (!notifications.Checked) sounds.StopCue(); UpdateCallFeedback(); SaveSettings(); };
        messageSounds.CheckedChanged += (_, _) => { settings.MessageSounds = messageSounds.Checked; SaveSettings(); };
        backgroundGlow.CheckedChanged += (_, _) => { settings.BackgroundGlow = Backdrop.Glow = backgroundGlow.Checked; SaveSettings(); Invalidate(true); UpdateDrift(); };
        ringtones.SelectedIndexChanged += (_, _) => { settings.Ringtone = (ringtones.SelectedItem as Choice)?.Id ?? "boris"; SaveSettings(); };
        themes.ThemeChosen += id => { if (!applyingTheme) SwitchTheme(id); };
        animationPicker.SelectedIndexChanged += (_, _) =>
        {
            var level = animationPicker.SelectedIndex switch { 1 => MotionLevel.Minimal, 2 => MotionLevel.Off, _ => MotionLevel.Full };
            UiMotion.Level = level; if (level != MotionLevel.Full) UiMotion.FinishAll(); UpdateDrift(); settings.AnimationLevel = UiMotion.Format(level); settings.Animations = level != MotionLevel.Off; SaveSettings();
        };
        micKey.Captured += (_, _) => ChangeHotkey(micKey, 0); earsKey.Captured += (_, _) => ChangeHotkey(earsKey, 1); openKey.Captured += (_, _) => ChangeHotkey(openKey, 2);
        screenKey.Captured += (_, _) => ChangeHotkey(screenKey, 3); cameraKey.Captured += (_, _) => ChangeHotkey(cameraKey, 4);
        foreach (var box in new[] { micKey, earsKey, openKey, screenKey, cameraKey }) { box.CaptureStarted += (_, _) => UnregisterShortcuts(); box.CaptureEnded += (_, _) => RegisterShortcuts(); }
        devToggle.Click += (_, _) => ToggleDeveloper();
        manualCodes.Click += (_, _) => ShowManualCodes();
        clearHistory.Click += (_, _) =>
        {
            if (chats is null || MessageBox.Show(this, "Удалить всю историю чатов на этом ПК? У собеседников их копии останутся.", "Voicecon", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            chats.Clear(); LoadHistory(); UpdateChatUsage();
        };
        openLogs.Click += (_, _) => { Directory.CreateDirectory(AppLog.Directory); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppLog.Directory + "\"") { UseShellExecute = false }); };
        aboutButton.Click += (_, _) => { using var about = new AboutDialog(); about.ShowDialog(this); };
    }

    private void ToggleDeveloper()
    {
        if (devPanel is null || devRow is null || devCardRow is null) return;
        devExpanded = !devExpanded; UpdateChatUsage();
        var target = devExpanded ? devPanel.RowStyles.Cast<RowStyle>().Sum(r => r.Height) : 0; var start = devRow.Height; var cardStart = devCardRow.Height; devPanel.Visible = true;
        devToggle.Text = devExpanded ? "Скрыть раздел" : "Показать раздел";
        UiMotion.Run(devRow, this, 260, p => { var h = start + (target - start) * p; devCardRow.Height = cardStart + h - start; devRow.Height = h; settingsScroller.UpdateRange(); },
            () => { devPanel.Visible = devExpanded; settingsScroller.UpdateRange(); if (devExpanded && devAnchor is not null) settingsScroller.ScrollTo(devAnchor); });
    }
    private void UpdateChatUsage() => chatUsage.Text = chats is null ? "История чатов недоступна" : $"История чатов: {chats.TotalBytes / 1048576.0:0.0} МБ из {chats.Limit / 1048576} МБ · старые сообщения удаляются автоматически";

    private void ToggleSettings() { if (settingsOpen) CloseSettings(); else OpenSettings(); }
    /// <summary>
    /// The page slides as two snapshots on one overlay: the real controls stay still, so every frame is two blits and the
    /// slide holds the monitor rate (170 Hz included). A slide reverses cleanly if the button is pressed again mid-way.
    /// </summary>
    private void OpenSettings()
    {
        if (settingsOpen) return; settingsOpen = true; settingsButton.Selected = false; UpdateChatUsage();
        if (!UiMotion.WillAnimate(this) || mainHost.Width < 10)
        {
            UiMotion.Cancel(settingsDrawer); slide.End(); drawerProgress = 1; settingsDrawer.Visible = true; PlaceDrawer(false); settingsDrawer.BringToFront(); return;
        }
        if (!slide.Active)
        {
            var under = SlideOverlay.FromScreen(mainHost);
            // Lay the page out off-screen and render it once; nothing on screen changes until the overlay appears.
            drawerProgress = 0; PlaceDrawer(true);
            if (!settingsDrawer.Visible) settingsDrawer.Visible = true;
            settingsDrawer.PerformLayout(); CenterSettings();
            slide.Bounds = new Rectangle(Point.Empty, mainHost.ClientSize);
            slide.Begin(under, SlideOverlay.Render(settingsDrawer), mainHost.Width);
        }
        var start = drawerProgress;
        UiMotion.Run(settingsDrawer, this, 300, p => { drawerProgress = start + (1 - start) * p; slide.SetEdge(SheetLeft(drawerProgress)); },
            () => { PlaceDrawer(false); settingsDrawer.BringToFront(); slide.End(); }, glide: true);
    }
    private void CloseSettings()
    {
        if (!settingsOpen) return; settingsOpen = false; PersistSettings();
        if (!UiMotion.WillAnimate(this) || mainHost.Width < 10)
        {
            UiMotion.Cancel(settingsDrawer); slide.End(); drawerProgress = 0; settingsDrawer.Visible = false; return;
        }
        if (!slide.Active)
        {
            var main = mainHost.Controls.Cast<Control>().First(c => c != settingsDrawer && c != slide);
            slide.Bounds = new Rectangle(Point.Empty, mainHost.ClientSize);
            slide.Begin(SlideOverlay.Render(main), SlideOverlay.FromScreen(settingsDrawer), SheetLeft(drawerProgress));
        }
        var start = drawerProgress;
        UiMotion.Run(settingsDrawer, this, 260, p => { drawerProgress = start * (1 - p); slide.SetEdge(SheetLeft(drawerProgress)); },
            () => { settingsDrawer.Visible = false; slide.End(); }, glide: true);
    }
    /// <summary>Renders the page once off-screen after start-up, so the first slide does not pay for shadow templates and layout.</summary>
    private void WarmSettings()
    {
        if (IsDisposed || settingsOpen || settingsDrawer.Visible || slide.Active || mainHost.Width < 10) return;
        drawerProgress = 0; PlaceDrawer(true); settingsDrawer.Visible = true; settingsDrawer.PerformLayout(); CenterSettings();
        using (SlideOverlay.Render(settingsDrawer)) { }
        if (!settingsOpen) settingsDrawer.Visible = false;
    }
    private int SheetLeft(float progress) => (int)Math.Round(mainHost.Width * (1 - progress));
    private void PlaceDrawer(bool moving)
    {
        if (mainHost.Width < 10) return;
        var progress = settingsOpen || moving ? drawerProgress : 0;
        var bounds = new Rectangle(SheetLeft(progress), 0, mainHost.Width, mainHost.Height);
        if (settingsDrawer.Bounds != bounds) settingsDrawer.Bounds = bounds;
        if (!moving) CenterSettings();
    }

    private void ShowManualCodes()
    {
        var t = UiTheme.Current;
        using var dialog = new ThemedDialog { Text = "Подключение по разовому коду", ClientSize = new Size(600, 520), StartPosition = FormStartPosition.CenterParent, Font = UiTheme.Body, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Icon = Icon, AutoScaleMode = AutoScaleMode.Dpi };
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) }; dialog.Padding = new Padding(10); dialog.Controls.Add(card);
        var grid = Grid(1); card.Controls.Add(grid);
        AddRow(grid, TextLabel("Без сервиса доставки", 13, TextTone.Normal, true), 32);
        AddRow(grid, TextLabel("Организатор создаёт приглашение и отправляет его другу. Друг вставляет его ниже\nи возвращает ответ; организатор вставляет ответ. Контакты не нужны.", 9, TextTone.Muted), 44);
        var create = new SoftButton { Text = "Создать приглашение", Glyph = UiIcon.Plus, Tone = ButtonTone.Secondary, Dock = DockStyle.Fill }; AddRow(grid, create, 46);
        AddRow(grid, TextLabel("ВАШ КОД · отправьте собеседнику", 8, TextTone.Muted, true), 26);
        var outgoing = new TextBox { Multiline = true, ReadOnly = true, Font = UiTheme.Code }; var copy = new SoftButton { Glyph = UiIcon.Copy, Circle = true, Tone = ButtonTone.Quiet, AccessibleName = "Копировать код" };
        var outRow = Columns(-1, 8, 48); outRow.Controls.Add(InputSurface(outgoing, true), 0, 0); outRow.Controls.Add(copy, 2, 0); AddRow(grid, outRow, 90);
        AddRow(grid, TextLabel("КОД СОБЕСЕДНИКА · приглашение или ответ", 8, TextTone.Muted, true), 26);
        var incoming = new TextBox { Multiline = true, MaxLength = 24000, Font = UiTheme.Code }; AddRow(grid, InputSurface(incoming, true), 90);
        AddRow(grid, Space(), 10);
        var apply = new SoftButton { Text = "Применить код", Glyph = UiIcon.Arrow, Tone = ButtonTone.Primary, Dock = DockStyle.Fill }; AddRow(grid, apply, 48);
        var manual = new Conversation("x:manual", ContactKind.Friend, "manual", "Разовое подключение");
        create.Click += async (_, _) => await Run(async () =>
        {
            if (room?.WaitingForAnswer == true) { room.CancelInvite(); outgoing.Clear(); }
            EnsureRoom(manual); outgoing.Text = await room!.CreateInviteAsync(); SetHint("Приглашение готово: отправьте код собеседнику.");
        });
        apply.Click += async (_, _) => await Run(async () =>
        {
            if (incoming.Text.Trim().Length == 0) throw new InvalidOperationException("Вставьте полный код собеседника.");
            EnsureRoom(manual); var answer = await room!.UseCodeAsync(incoming.Text.Trim()); incoming.Clear();
            if (answer is not null) { outgoing.Text = answer; SetHint("Ответ готов: отправьте верхний код организатору."); } else SetHint("Соединяем участников…");
        });
        copy.Click += (_, _) => { if (outgoing.TextLength > 0) { Clipboard.SetText(outgoing.Text); SetHint("Код скопирован."); } };
        UiPainting.ApplyBackgrounds(dialog); outgoing.ForeColor = incoming.ForeColor = t.Text;
        dialog.ShowDialog(this);
    }
}
