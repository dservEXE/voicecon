using System.Diagnostics;

namespace Voicecon;

public sealed partial class MainForm
{
    private readonly SurfacePanel settingsDrawer = new() { Gutter = 0, Radius = 22, Elevation = 0, Padding = new Padding(18, 14, 10, 12) };
    private readonly ThemedScrollHost settingsScroller = new() { Dock = DockStyle.Fill };
    private readonly SlimSlider microphoneGain = new();
    private readonly UiLabel microphoneGainValue = new(), chatUsage = new();
    private readonly ThemeSwatches themes = new();
    private readonly SegmentedPicker animationPicker = new();
    private readonly ToggleRow messageSounds = new(), showOnline = new(), showMetrics = new();
    private readonly SoftButton closeSettings = new(), refresh = new(), network = new(), devToggle = new(), manualCodes = new(), clearHistory = new(), openLogs = new(), aboutButton = new(), copyCode = new();
    private TableLayoutPanel? devPanel;
    private RowStyle? devRow;
    private Control? devAnchor;
    private bool settingsOpen, devExpanded;
    private float drawerProgress;

    private void BuildSettingsDrawer()
    {
        settingsDrawer.Visible = false; settingsDrawer.Wash = Color.FromArgb(205, UiTheme.Current.Background);
        var outer = Grid(1); settingsDrawer.Controls.Add(outer);
        var top = Columns(-1, 44); top.Controls.Add(TextLabel("Настройки", 15, TextTone.Normal, true), 0, 0);
        PrepareButton(closeSettings, "", UiIcon.Close, ButtonTone.Quiet); closeSettings.Circle = true; closeSettings.AccessibleName = "Закрыть настройки"; top.Controls.Add(closeSettings, 1, 0);
        AddRow(outer, top, 44);
        AddRow(outer, settingsScroller, 0); outer.RowStyles[^1].SizeType = SizeType.Percent; outer.RowStyles[^1].Height = 100;
        var grid = Grid(1); grid.Dock = DockStyle.Top; grid.AutoSize = true; grid.AutoSizeMode = AutoSizeMode.GrowAndShrink; grid.Padding = new Padding(0, 0, 10, 16);
        settingsScroller.SetContent(grid);

        Section(grid, "Профиль");
        AddRow(grid, TextLabel("Имя для друзей", 9, TextTone.Muted), 24); name.MaxLength = 24; name.Text = settings.Name; AddRow(grid, InputSurface(name, false), 42);
        AddRow(grid, Space(), 8); PrepareButton(copyCode, "Скопировать мой код друга", UiIcon.Copy); AddRow(grid, copyCode, 46);

        Section(grid, "Звук");
        AddRow(grid, TextLabel("Микрофон", 9, TextTone.Muted), 24);
        var micRow = Columns(-1, 8, 44); PrepareCombo(microphones); micRow.Controls.Add(microphones, 0, 0);
        PrepareButton(refresh, "", UiIcon.Refresh, ButtonTone.Quiet); refresh.Circle = true; refresh.AccessibleName = "Обновить список устройств"; tips.SetToolTip(refresh, "Обновить список устройств"); micRow.Controls.Add(refresh, 2, 0); AddRow(grid, micRow, 42);
        AddRow(grid, TextLabel("Наушники", 9, TextTone.Muted), 24); PrepareCombo(speakers); AddRow(grid, speakers, 42);
        AddLevel(grid, "Громкость приложения", volume, volumeValue, settings.Volume);
        AddLevel(grid, "Усиление микрофона", microphoneGain, microphoneGainValue, settings.MicrophoneGain);
        AddRow(grid, Space(), 6);
        SetupToggle(noise, "Шумоподавление", "Убирает ровный фон и гул", settings.NoiseSuppression); AddRow(grid, noise, 52);
        SetupToggle(ptt, "Говорить по кнопке", "Голос передаётся, пока клавиша нажата", settings.PushToTalk); AddRow(grid, ptt, 52);
        var keyRow = Columns(-1, 170); keyRow.Controls.Add(TextLabel("Клавиша разговора", 9, TextTone.Muted), 0, 0);
        PrepareCombo(talkKey); talkKey.Items.AddRange([new KeyChoice("V", 0x56), new KeyChoice("Caps Lock", 0x14), new KeyChoice("Мышь 4", 0x05), new KeyChoice("Мышь 5", 0x06)]);
        talkKey.SelectedIndex = Math.Max(0, talkKey.Items.Cast<KeyChoice>().ToList().FindIndex(k => k.Code == settings.TalkKey)); talkKey.Enabled = settings.PushToTalk; keyRow.Controls.Add(talkKey, 1, 0); AddRow(grid, keyRow, 42);

        Section(grid, "Уведомления");
        SetupToggle(notifications, "Звуки событий", "Подключения, демонстрации, зрители. Без звуков нажатий", settings.NotificationSounds); AddRow(grid, notifications, 52);
        SetupToggle(messageSounds, "Звук новых сообщений", "Тихая капля, когда пишут в чат звонка", settings.MessageSounds); AddRow(grid, messageSounds, 52);
        AddRow(grid, TextLabel("Мелодия входящего звонка", 9, TextTone.Muted), 26);
        PrepareCombo(ringtones); ringtones.Items.AddRange([new Choice("boris", "Flying Bird · Boris Brejcha"), new Choice("birdmachine", "Bird Machine · Jingle Bells"), new Choice("soft", "Капли · мягкий"), new Choice("bell", "Старый телефон"), new Choice("custom", "Свой WAV или MP3")]);
        ringtones.SelectedIndex = settings.Ringtone switch { "birdmachine" => 1, "soft" => 2, "bell" => 3, "custom" => 4, _ => 0 }; AddRow(grid, ringtones, 42);
        var ringButtons = Columns(-50, 8, -50);
        var choose = new SoftButton { Text = "Свой файл", Glyph = UiIcon.Folder, Tone = ButtonTone.Quiet, Dock = DockStyle.Fill }; var preview = new SoftButton { Text = "Прослушать", Glyph = UiIcon.Volume, Tone = ButtonTone.Quiet, Dock = DockStyle.Fill };
        choose.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Звук WAV или MP3|*.wav;*.mp3", Title = "Мелодия входящего звонка", CheckFileExists = true }; if (picker.ShowDialog(this) == DialogResult.OK) { settings.RingtoneFile = picker.FileName; settings.Ringtone = "custom"; ringtones.SelectedIndex = 4; SaveSettings(); } };
        preview.Click += (_, _) => sounds.PlayRing(settings.Ringtone, settings.RingtoneFile, OutputDevice);
        ringButtons.Controls.Add(choose, 0, 0); ringButtons.Controls.Add(preview, 2, 0); AddRow(grid, ringButtons, 42);

        Section(grid, "Оформление");
        AddRow(grid, TextLabel("Тема", 9, TextTone.Muted), 24); themes.Selected = UiTheme.Current.Id; themes.AccessibleName = "Тема оформления"; AddRow(grid, themes, 52);
        AddRow(grid, TextLabel("Анимации", 9, TextTone.Muted), 26);
        animationPicker.Options.Add(new("Полные")); animationPicker.Options.Add(new("Минимум")); animationPicker.Options.Add(new("Выкл."));
        animationPicker.SetSilently(UiMotion.Parse(settings.AnimationLevel) switch { MotionLevel.Minimal => 1, MotionLevel.Off => 2, _ => 0 }); animationPicker.AccessibleName = "Анимации";
        tips.SetToolTip(animationPicker, "Минимум: короткие переходы без декоративных эффектов. Выкл.: всё мгновенно.");
        AddRow(grid, animationPicker, 42);

        Section(grid, "Горячие клавиши");
        AddRow(grid, TextLabel("Пусто — не назначено. Нажмите поле и клавишу · Esc — отмена · ПКМ — убрать", 8.5f, TextTone.Muted), 34);
        AddHotkey(grid, "Микрофон", micKey, settings.MicrophoneHotkey); AddHotkey(grid, "Наушники и весь звук", earsKey, settings.HeadphonesHotkey); AddHotkey(grid, "Открыть окно", openKey, settings.OpenWindowHotkey);
        AddHotkey(grid, "Демонстрация экрана", screenKey, settings.ScreenHotkey); AddHotkey(grid, "Камера", cameraKey, settings.CameraHotkey);

        Section(grid, "Система");
        SetupToggle(startup, "Запускать вместе с Windows", "Сразу в трее, без окна", settings.StartWithWindows); AddRow(grid, startup, 52);
        SetupToggle(showOnline, "Показывать друзьям, что я в сети", "Без имени и адресов: только «в сети» или «в звонке»", settings.ShowOnlineStatus); AddRow(grid, showOnline, 52);
        AddUpdateSettings(grid);

        AddRow(grid, Space(), 18);
        PrepareButton(devToggle, "Для разработчиков", UiIcon.Chevron, ButtonTone.Quiet); devToggle.Font = UiTheme.Small; AddRow(grid, devToggle, 36); devAnchor = devToggle;
        devPanel = Grid(1);
        SetupToggle(gate, "Тишина в паузах", "Закрывает микрофон ниже порога громкости", settings.NoiseGate); AddRow(devPanel, gate, 52);
        SetupToggle(showMetrics, "Показывать нагрузку и трафик", "CPU, память и кбит/с внизу окна", settings.ShowMetrics); AddRow(devPanel, showMetrics, 52);
        PrepareButton(network, "Сеть: STUN, TURN и доставка", UiIcon.Sliders); AddRow(devPanel, network, 46); AddRow(devPanel, Space(), 6);
        PrepareButton(manualCodes, "Подключение по разовому коду", UiIcon.Copy); AddRow(devPanel, manualCodes, 46); AddRow(devPanel, Space(), 10);
        PrepareLabel(chatUsage, TextTone.Muted); chatUsage.Font = UiTheme.Small; AddRow(devPanel, chatUsage, 22);
        PrepareButton(clearHistory, "Очистить историю чатов", UiIcon.Trash, ButtonTone.Quiet); AddRow(devPanel, clearHistory, 42);
        PrepareButton(openLogs, "Открыть папку журнала", UiIcon.Folder, ButtonTone.Quiet); AddRow(devPanel, openLogs, 42);
        PrepareButton(aboutButton, "О приложении и лицензии", UiIcon.Info, ButtonTone.Quiet); AddRow(devPanel, aboutButton, 42);
        var devIndex = grid.RowCount; AddRow(grid, devPanel, 0); devRow = grid.RowStyles[devIndex]; devPanel.Visible = false;

        mainHost.Controls.Add(settingsDrawer); settingsDrawer.BringToFront();
    }
    private static void Section(TableLayoutPanel grid, string title)
    {
        AddRow(grid, Space(), 16); AddRow(grid, TextLabel(title.ToUpperInvariant(), 8, TextTone.Accent, true), 24); AddRow(grid, new DividerLine(), 8);
    }
    private static void AddLevel(TableLayoutPanel grid, string title, SlimSlider slider, UiLabel value, int initial)
    {
        var row = Columns(-1, 70); row.Controls.Add(TextLabel(title, 9, TextTone.Muted), 0, 0); PrepareLabel(value, TextTone.Accent); value.TextAlign = ContentAlignment.MiddleRight; value.Text = $"{initial}%"; row.Controls.Add(value, 1, 0);
        AddRow(grid, row, 28); slider.Maximum = AudioLevels.MaximumPercent; slider.Value = initial; AddRow(grid, slider, 30);
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
        ringtones.SelectedIndexChanged += (_, _) => { settings.Ringtone = (ringtones.SelectedItem as Choice)?.Id ?? "boris"; SaveSettings(); };
        themes.ThemeChosen += id => { if (!applyingTheme) SwitchTheme(id); };
        animationPicker.SelectedIndexChanged += (_, _) =>
        {
            var level = animationPicker.SelectedIndex switch { 1 => MotionLevel.Minimal, 2 => MotionLevel.Off, _ => MotionLevel.Full };
            UiMotion.Level = level; if (level != MotionLevel.Full) UiMotion.FinishAll(); settings.AnimationLevel = UiMotion.Format(level); settings.Animations = level != MotionLevel.Off; SaveSettings();
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
        if (devPanel is null || devRow is null) return;
        devExpanded = !devExpanded; UpdateChatUsage();
        var target = devExpanded ? devPanel.RowStyles.Cast<RowStyle>().Sum(r => r.Height) : 0; var start = devRow.Height; devPanel.Visible = true;
        devToggle.Text = devExpanded ? "Скрыть раздел для разработчиков" : "Для разработчиков";
        UiMotion.Run(devRow, this, 240, p => { devRow.Height = start + (target - start) * p; settingsScroller.UpdateRange(); },
            () => { devPanel.Visible = devExpanded; settingsScroller.UpdateRange(); if (devExpanded && devAnchor is not null) settingsScroller.ScrollTo(devAnchor); });
    }
    private void UpdateChatUsage() => chatUsage.Text = chats is null ? "История чатов недоступна" : $"История чатов: {chats.TotalBytes / 1048576.0:0.0} МБ из {chats.Limit / 1048576} МБ · старые сообщения удаляются автоматически";

    private void ToggleSettings() { if (settingsOpen) CloseSettings(); else OpenSettings(); }
    private void OpenSettings()
    {
        if (settingsOpen) return; settingsOpen = true; settingsButton.Selected = false; UpdateChatUsage();
        settingsDrawer.Visible = true; settingsDrawer.BringToFront(); var start = drawerProgress;
        UiMotion.Run(settingsDrawer, this, 260, p => { drawerProgress = start + (1 - start) * p; PlaceDrawer(true); });
    }
    private void CloseSettings()
    {
        if (!settingsOpen) return; settingsOpen = false; var start = drawerProgress;
        UiMotion.Run(settingsDrawer, this, 220, p => { drawerProgress = start * (1 - p); PlaceDrawer(true); }, () => settingsDrawer.Visible = false);
        PersistSettings();
    }
    private void PlaceDrawer(bool moving)
    {
        if (mainHost.Width < 10) return;
        var width = Math.Min((int)(470 * DeviceDpi / 96f), mainHost.Width);
        var progress = settingsOpen || moving ? drawerProgress : 0;
        settingsDrawer.Bounds = new Rectangle(mainHost.Width - (int)(width * progress), 0, width, mainHost.Height);
        if (moving) settingsDrawer.Update();
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
