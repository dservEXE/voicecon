using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Voicecon;

public sealed partial class MainForm : Form
{
    private readonly Settings settings = Settings.Load();
    private readonly bool previewMode, startInTray;
    private bool windowRequested, initialized, quitting, busy, isMuted, isDeafened, balloonShown;
    private VoiceRoom? room;
    private ContactBook? contacts;
    private CallMailbox? mailbox;
    private NotifyIcon? tray;
    private ToolStripMenuItem? stopVideoItem;
    private Icon? ownedIcon;
    private readonly UiSounds sounds = new();
    private CallAudioFeedback? callFeedback; private bool outgoingAnswered;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer settingsTimer = new() { Interval = 350 };
    private readonly Process process = Process.GetCurrentProcess();
    private DateTime lastSample = DateTime.UtcNow;
    private TimeSpan lastCpu;
    private long lastSent, lastReceived, lastRing;
    private PendingCall? incomingCall, outgoingCall;
    private readonly List<VoiceRoom.ChatLine> chatHistory = [];
    private Keys[] registeredKeys = [];
    private readonly object themeTrack = new();

    private readonly Label state = new(), details = new(), metrics = new(), hint = new(), statusBadge = new(), delivery = new(), volumeValue = new(), shortcutHint = new(), incomingTitle = new();
    private readonly TextBox outgoing = new(), incoming = new(), name = new(), chatInput = new();
    private readonly RichTextBox chatLog = new();
    private readonly SoftButton create = new(), apply = new(), copy = new(), end = new(), mute = new(), deafen = new(), network = new(), refresh = new(), callFriend = new(), addFriend = new(), ownCard = new(), acceptCall = new(), rejectCall = new(), sendChat = new(), emoji = new(), settingsNav = new(), callNav = new();
    private readonly ThemedComboBox microphones = new(), speakers = new(), talkKey = new(), themes = new(), friends = new(), ringtones = new();
    private readonly ToggleRow ptt = new(), noise = new(), gate = new(), startup = new(), animations = new(), notifications = new();
    private readonly HotkeyBox micKey = new(), earsKey = new(), openKey = new();
    private readonly SlimSlider volume = new();
    private readonly ThemedFlowList people = new();
    private readonly ToolTip tips = new() { InitialDelay = 500, AutoPopDelay = 5000 };
    private readonly Panel pages = new(), incomingPanel = new(), manualPanel = new();
    private Control callPage = null!, settingsPage = null!;
    private bool applyingTheme;
    private string participantSignature = "";

    public MainForm(bool previewMode = false, string? previewTheme = null, bool startInTray = false)
    {
        if (previewMode) settings.Name = "Игрок";
        this.previewMode = previewMode; this.startInTray = startInTray; windowRequested = !startInTray;
        if (!previewMode) settings.StartWithWindows = StartupManager.IsEnabled();
        UiTheme.Select(previewTheme ?? settings.Theme); UiMotion.Enabled = settings.Animations;
        BuildLayout(); ApplyTheme();
        if (previewMode)
        {
            microphones.Items.Add(new Device(-1, "По умолчанию в Windows")); speakers.Items.Add(new Device(-1, "По умолчанию в Windows"));
            microphones.SelectedIndex = speakers.SelectedIndex = 0; RefreshFriends(); RefreshControls(); return;
        }
        BuildTray(); WireActions(); LoadDevices(); RefreshFriends(); RefreshControls();
        timer.Tick += (_, _) => { UpdateStats(); CheckCalls(); ScheduleUpdateCheck(); }; timer.Start();
        settingsTimer.Tick += (_, _) => { settingsTimer.Stop(); PersistSettings(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        Deactivate += (_, _) => UiMotion.FinishAll();
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { BackColor = UiTheme.Current.Field, ForeColor = UiTheme.Current.Text, Renderer = new ThemeMenuRenderer(), Font = UiTheme.Body };
        menu.Items.Add("Открыть Voicecon", null, (_, _) => ShowWindow());
        menu.Items.Add("Микрофон: включить / выключить", null, (_, _) => ToggleMute());
        menu.Items.Add("Наушники: включить / выключить", null, (_, _) => ToggleDeafen());
        menu.Items.Add("Настройки", null, (_, _) => { ShowWindow(); ShowPage(true); });
        stopVideoItem = new ToolStripMenuItem("Остановить экран и камеру", null, (_, _) => StopAllVideo()) { Enabled = false }; menu.Items.Add(stopVideoItem);
        menu.Items.Add("О приложении и лицензии", null, (_, _) => { using var about = new AboutDialog(); about.ShowDialog(this); });
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Выйти полностью", null, (_, _) => { quitting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "Voicecon", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowWindow(); tray.BalloonTipClicked += (_, _) => ShowWindow();
    }
    private void WireActions()
    {
        create.Click += async (_, _) => await Run(async () =>
        {
            if (room?.WaitingForAnswer == true) { CancelOutgoing(); room.CancelInvite(); outgoing.Clear(); SetStatus("Приглашение отменено"); return; }
            EnsureRoom(); SetStatus("Готовим приглашение…"); outgoing.Clear(); outgoing.Text = await room!.CreateInviteAsync();
            SetStatus("Приглашение готово", "Отправьте код другу и вставьте его ответ ниже.");
        });
        apply.Click += async (_, _) => await Run(async () =>
        {
            if (incoming.Text.Trim().Length == 0) throw new InvalidOperationException("Вставьте полный код друга.");
            EnsureRoom(); var answer = await room!.UseCodeAsync(incoming.Text.Trim()); incoming.Clear();
            if (answer is not null) { outgoing.Text = answer; SetStatus("Ответ готов", "Отправьте верхний код организатору."); }
            else SetStatus("Соединяем участников…");
        });
        copy.Click += (_, _) => { if (outgoing.TextLength > 0) { Clipboard.SetText(outgoing.Text); SetHint("Код скопирован."); } };
        mute.Click += (_, _) => ToggleMute(); deafen.Click += (_, _) => ToggleDeafen(); end.Click += (_, _) => EndCall();
        callFriend.Click += async (_, _) => await Run(CallFriendAsync);
        addFriend.Click += (_, _) => AddFriend(); ownCard.Click += (_, _) => ShowOwnCard();
        acceptCall.Click += async (_, _) => await Run(AcceptIncomingAsync); rejectCall.Click += (_, _) => RejectIncoming();
        sendChat.Click += (_, _) => SendMessage(); emoji.Click += (_, _) => OpenEmoji();
        chatInput.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendMessage(); } };
        callNav.Click += (_, _) => ShowPage(false); settingsNav.Click += (_, _) => ShowPage(true);
        refresh.Click += (_, _) => LoadDevices();
        network.Click += (_, _) =>
        {
            var oldServer = settings.SignalingServer;
            using var dialog = new NetworkDialog(settings);
            if (dialog.ShowDialog(this) == DialogResult.OK) { SaveSettings(); if (oldServer != settings.SignalingServer) StartMailbox(); }
        };
        foreach (var check in new[] { ptt, noise, gate }) check.CheckedChanged += (_, _) => { UpdateAudioOptions(); SaveSettings(); };
        talkKey.SelectedIndexChanged += (_, _) => { UpdateAudioOptions(); SaveSettings(); };
        volume.ValueChanged += (_, _) => { UpdateAudioOptions(); SaveSettings(); };
        name.Leave += (_, _) => SaveSettings();
        microphones.SelectedIndexChanged += (_, _) => SaveSettings(); speakers.SelectedIndexChanged += (_, _) => SaveSettings();
        friends.SelectedIndexChanged += (_, _) => RefreshControls();
        startup.CheckedChanged += (_, _) =>
        {
            try { StartupManager.SetEnabled(startup.Checked); settings.StartWithWindows = startup.Checked; SaveSettings(); }
            catch (Exception ex) { SetHint("Не удалось изменить автозагрузку: " + ex.Message); }
        };
        animations.CheckedChanged += (_, _) => { settings.Animations = animations.Checked; UiMotion.Enabled = animations.Checked; if (!animations.Checked) UiMotion.FinishAll(); SaveSettings(); };
        notifications.CheckedChanged += (_, _) => { settings.NotificationSounds = notifications.Checked; if (!notifications.Checked) sounds.StopCue(); UpdateCallFeedback(); SaveSettings(); };
        ringtones.SelectedIndexChanged += (_, _) => { settings.Ringtone = (ringtones.SelectedItem as Choice)?.Id ?? "bell"; SaveSettings(); };
        micKey.Captured += (_, _) => ChangeHotkey(micKey, 0); earsKey.Captured += (_, _) => ChangeHotkey(earsKey, 1); openKey.Captured += (_, _) => ChangeHotkey(openKey, 2);
        foreach (var box in new[] { micKey, earsKey, openKey, screenKey, cameraKey })
        {
            box.CaptureStarted += (_, _) => UnregisterShortcuts();
            box.CaptureEnded += (_, _) => RegisterShortcuts();
        }
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; RefreshControls();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Не удалось выполнить действие", ex.Message); SetHint(ex.Message); }
        finally { busy = false; RefreshControls(); }
    }
    private void InitializeContacts()
    {
        if (initialized || previewMode) return; initialized = true;
        try { contacts = new ContactBook(); RefreshFriends(); StartMailbox(); }
        catch (Exception ex) { delivery.Text = "Контакты недоступны"; SetHint(ex.Message); }
    }
    private void StartMailbox()
    {
        if (contacts is null || previewMode) return;
        mailbox?.Dispose(); mailbox = new CallMailbox(contacts, contacts.OwnCard(settings.Name, settings.SignalingServer));
        var current = mailbox;
        mailbox.ConnectivityChanged += ready => Dispatch(() => { if (mailbox == current) delivery.Text = ready ? "●  Входящие доступны" : "●  Доставка звонков недоступна"; });
        mailbox.Received += (friend, signal) => Dispatch(async () =>
        {
            if (mailbox != current) return;
            try { await OnSignal(friend, signal); } catch (Exception ex) { SetHint("Звонок: " + ex.Message); }
        });
        delivery.Text = "●  Подключаем доставку звонков…"; mailbox.Start();
    }
    private void Dispatch(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed && !quitting) action(); })); } catch (InvalidOperationException) { }
    }
    private async Task CallFriendAsync()
    {
        if (friends.SelectedItem is not FriendChoice { Friend: { } friend }) throw new InvalidOperationException("Сначала добавьте друга и выберите его в списке.");
        if (mailbox is not { Connected: true }) throw new InvalidOperationException("Сервис доставки ещё не подключён. Проверьте интернет или используйте обмен кодами.");
        if (incomingCall is not null || outgoingCall is not null) throw new InvalidOperationException("Сначала завершите предыдущий вызов.");
        EnsureRoom(); if (!room!.CanInvite) throw new InvalidOperationException("Новый вызов может создать организатор комнаты. Завершите текущий звонок для входа в другую комнату.");
        SetStatus("Готовим вызов…", friend.Name);
        var code = await room.CreateInviteAsync(); var id = Guid.NewGuid().ToString("N"); outgoingAnswered = false; outgoingCall = new(friend, id, code, DateTimeOffset.UtcNow.AddSeconds(70));
        try { await mailbox.SendAsync(friend, CallSignal.Create("offer", id, code)); SetStatus("Вызываем " + friend.Name, "У друга прозвучит звонок, если Voicecon работает в фоне и он добавил ваш контакт."); }
        catch { outgoingCall = null; room.CancelInvite(); throw; }
    }
    private async Task OnSignal(FriendCard friend, CallSignal signal)
    {
        if (signal.Kind == "offer")
        {
            if (signal.Code is null) return; InviteCode.Decode(signal.Code);
            if (incomingCall is not null || outgoingCall is not null || room?.ConnectedCount > 0 || busy)
            { await mailbox!.SendAsync(friend, CallSignal.Create("busy", signal.Id)); return; }
            incomingCall = new(friend, signal.Id, signal.Code, DateTimeOffset.UtcNow.AddSeconds(45));
            incomingTitle.Text = "Входящий звонок · " + friend.Name; incomingPanel.Visible = true;
            SetStatus("Вам звонит " + friend.Name, "Откройте Voicecon и примите звонок."); if (!previewMode) PlayRingtone();
            tray?.ShowBalloonTip(6000, "Входящий звонок", friend.Name + " звонит в Voicecon", ToolTipIcon.Info); RefreshControls();
        }
        else if (signal.Kind == "cancel" && incomingCall is { } pending && pending.Id == signal.Id && pending.Friend.Id == friend.Id)
        { ClearIncoming(); SetStatus("Вызов отменён", friend.Name + " завершил вызов."); }
        else if (outgoingCall is { } sent && sent.Id == signal.Id && sent.Friend.Id == friend.Id)
        {
            if (signal.Kind == "answer" && signal.Code is not null && room is not null) { await room.UseCodeAsync(signal.Code); outgoingAnswered = true; SetStatus("Подключаемся…"); }
            else if (signal.Kind is "reject" or "busy") { outgoingCall = null; room?.CancelInvite(); SetStatus(signal.Kind == "busy" ? "Друг занят" : "Звонок отклонён", friend.Name); }
        }
        RefreshControls();
    }
    private async Task AcceptIncomingAsync()
    {
        if (incomingCall is not { } pending || mailbox is null) return;
        ClearIncoming(); EndRoomOnly(); EnsureRoom();
        try { var answer = await room!.UseCodeAsync(pending.Code); await mailbox.SendAsync(pending.Friend, CallSignal.Create("answer", pending.Id, answer)); SetStatus("Подключаемся…"); }
        catch { EndRoomOnly(); await SendQuiet(pending.Friend, CallSignal.Create("reject", pending.Id)); throw; }
    }
    private void RejectIncoming()
    {
        if (incomingCall is { } pending) _ = SendQuiet(pending.Friend, CallSignal.Create("reject", pending.Id));
        ClearIncoming(); SetStatus("Звонок отклонён");
    }
    private async Task SendQuiet(FriendCard friend, CallSignal signal)
    { try { if (mailbox is not null) await mailbox.SendAsync(friend, signal); } catch { } }
    private void ClearIncoming() { incomingCall = null; sounds.StopRing(); incomingPanel.Visible = false; RefreshControls(); }
    private void CancelOutgoing()
    {
        if (outgoingCall is { } pending) _ = SendQuiet(pending.Friend, CallSignal.Create("cancel", pending.Id));
        outgoingCall = null;
    }
    private void CheckCalls()
    {
        UpdateCallFeedback();
        var now = DateTimeOffset.UtcNow;
        if (incomingCall is { } call)
        {
            if (now > call.Deadline) { ClearIncoming(); SetStatus("Пропущенный звонок", call.Friend.Name); }
            else if (Environment.TickCount64 - lastRing > 3200) PlayRingtone();
        }
        if (outgoingCall is { } sent && now > sent.Deadline)
        { CancelOutgoing(); room?.CancelInvite(); SetStatus("Нет ответа", "Друг не принял звонок или его приложение недоступно."); RefreshControls(); }
    }
    private void UpdateCallFeedback()
    {
        sounds.VolumePercent = settings.Volume;
        if (previewMode) return;
        callFeedback ??= new CallAudioFeedback(sounds);
        callFeedback.Update(outgoingAnswered ? null : outgoingCall?.Id, room is { IsDisposed: false } current ? current.ConnectedCount : 0, settings.NotificationSounds, OutputDevice, Environment.TickCount64);
    }    private void PlayRingtone() { lastRing = Environment.TickCount64; sounds.PlayRing(settings.Ringtone, settings.RingtoneFile, OutputDevice); }
    private int OutputDevice => (speakers.SelectedItem as Device)?.Index ?? -1;

    private void EnsureRoom()
    {
        if (room is { IsDisposed: false }) return;
        SaveSettings(); latency.Clear(); chatHistory.Clear(); RenderChat();
        room = new VoiceRoom(settings, (microphones.SelectedItem as Device)?.Index ?? -1, OutputDevice, localOnly: previewMode, enableAudio: !previewMode); room.SetMuted(isMuted, isDeafened);
        var current = room;
        room.Status += text =>
        {
            if (room != current) return;
            if (text == "Голосовая связь установлена") SetStatus("Вы на связи", "Голос и чат подключены. На странице «Видео» можно включить экран и камеру.");
            else SetStatus(text);
        };
        room.Changed += () =>
        {
            if (room != current) return;
            if (room.IsDisposed || room.ConnectedCount == 0) StopAllVideo();
            if (outgoingCall is not null && !room.WaitingForAnswer && room.ConnectedCount > 0) outgoingCall = null;
            SyncMute(); RefreshControls();
        };
        room.ChatReceived += line =>
        {
            if (room != current) return;
            using var redraw = new UiRedrawScope(chatLog); chatHistory.Add(line);
            if (chatHistory.Count > 200)
            {
                var old = chatHistory[0]; chatHistory.RemoveAt(0); chatLog.Select(0, ChatText(old).Length); chatLog.SelectedText = "";
            }
            AppendChat(line); chatLog.SelectionStart = chatLog.TextLength; chatLog.ScrollToCaret();
        };
        room.VideoReceived += (id, name, bytes) => { if (room == current && !current.IsDisposed) ReceiveVideo(id, name, bytes); };
        room.VideoStopped += (id, kind, sequence) => { if (room == current) StopRemoteVideo(id, kind, sequence); };
        room.ParticipantLeft += id => { if (room == current) RemoveVideoParticipant(id); };
        room.ParticipantJoined += id => { if (room == current) unavailableVideo.TryRemove(id, out _); };
        room.Error += error => { if (room == current) { EndCall(); SetStatus("Звонок остановлен", error); } };
    }
    private void EndRoomOnly() { StopAllVideo(); room?.Dispose(); room = null; }
    private void EndCall()
    {
        CancelOutgoing(); if (incomingCall is not null) RejectIncoming(); EndRoomOnly(); outgoing.Clear(); incoming.Clear();
        lastSent = lastReceived = 0; SetStatus("Звонок завершён", "Можно снова позвонить другу или пригласить участников."); SetHint("Микрофон выключен до следующего соединения."); RefreshControls();
    }
    private void SendMessage()
    {
        try { room?.SendChat(chatInput.Text); if (room?.CanChat == true) chatInput.Clear(); }
        catch (Exception ex) { SetHint(ex.Message); }
    }
    private void RenderChat()
    {
        using var redraw = new UiRedrawScope(chatLog); chatLog.Clear(); foreach (var line in chatHistory) AppendChat(line);
        chatLog.SelectionStart = chatLog.TextLength; chatLog.ScrollToCaret();
    }
    private static string ChatText(VoiceRoom.ChatLine line) => (line.IsMine ? "Вы" : line.Name) + "  ·  " + line.Time.ToString("HH:mm") + "\n" + line.Text.Replace("\r\n", "\n").Replace('\r', '\n') + "\n\n";
    private void AppendChat(VoiceRoom.ChatLine line)
    {
        var t = UiTheme.Current; chatLog.SelectionStart = chatLog.TextLength; chatLog.SelectionColor = line.IsMine ? t.Accent : t.Muted;
        chatLog.AppendText((line.IsMine ? "Вы" : line.Name) + "  ·  " + line.Time.ToString("HH:mm") + "\n"); chatLog.SelectionColor = t.Text; chatLog.AppendText(line.Text + "\n\n");
    }
    private void OpenEmoji()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI Emoji", 16), BackColor = UiTheme.Current.Field, ForeColor = UiTheme.Current.Text, Renderer = new ThemeMenuRenderer() };
        foreach (var value in new[] { "😀", "😂", "😎", "❤️", "👍", "🔥", "🎮", "🎯", "💀", "😅", "👀", "✅" }) menu.Items.Add(value, null, (_, _) => { chatInput.SelectedText = value; chatInput.Focus(); });
        menu.Closed += (_, _) => menu.Dispose(); menu.Show(emoji, new Point(0, -menu.PreferredSize.Height));
    }
    private void ShowOwnCard()
    {
        if (contacts is null) { SetHint("Контакты ещё загружаются."); return; }
        var card = contacts.OwnCard(settings.Name, settings.SignalingServer).Encode();
        Clipboard.SetText(card); SetHint("Ваш код друга скопирован. Обменяйтесь кодами VCF1 один раз и добавьте друг друга.");
    }
    private void AddFriend()
    {
        if (contacts is null) return;
        var text = PromptCode("Добавить друга", "Вставьте код друга VCF1.…\nПопросите его также добавить ваш код.");
        if (text is null) return;
        try { var friend = FriendCard.Decode(text); contacts.Add(friend); RefreshFriends(friend.Id); SetHint("Контакт добавлен: " + friend.Name + ". Для входящих он должен добавить и ваш код."); }
        catch (Exception ex) { SetHint(ex.Message); }
    }
    private void RefreshFriends(string? selected = null)
    {
        selected ??= (friends.SelectedItem as FriendChoice)?.Friend?.Id; friends.Items.Clear();
        if (contacts is not null) foreach (var friend in contacts.Friends) friends.Items.Add(new FriendChoice(friend.Name, friend));
        if (friends.Items.Count == 0) friends.Items.Add(new FriendChoice("Сначала добавьте друга", null));
        friends.SelectedIndex = Math.Max(0, friends.Items.Cast<FriendChoice>().ToList().FindIndex(f => f.Friend?.Id == selected));
        RefreshControls();
    }

    private void ToggleMute() { isMuted = !isMuted; SyncMute(); RefreshControls(); if (settings.NotificationSounds) sounds.Play(!isMuted, OutputDevice); }
    private void ToggleDeafen() { isDeafened = !isDeafened; SyncMute(); RefreshControls(); if (settings.NotificationSounds) sounds.PlayHeadphones(!isDeafened, OutputDevice); }
    private void SyncMute() => room?.SetMuted(isMuted, isDeafened);
    private void RefreshControls()
    {
        var active = room is { IsDisposed: false };
        create.Enabled = !busy && (!active || room!.CanInvite || room.WaitingForAnswer); create.Text = active && room!.WaitingForAnswer ? "Отменить приглашение" : "Создать приглашение";
        apply.Enabled = !busy; copy.Enabled = outgoing.TextLength > 0; end.Enabled = active || outgoingCall is not null || incomingCall is not null;
        callFriend.Enabled = !busy && outgoingCall is null && incomingCall is null && friends.SelectedItem is FriendChoice { Friend: not null };
        name.Enabled = microphones.Enabled = speakers.Enabled = refresh.Enabled = network.Enabled = !active && !busy;
        talkKey.Enabled = ptt.Checked; chatInput.Enabled = sendChat.Enabled = emoji.Enabled = room?.CanChat == true;
        acceptCall.Enabled = rejectCall.Enabled = !busy; incomingPanel.Visible = incomingCall is not null;
        UpdateCallFeedback(); UpdateUpdateControls(); RenderParticipants(active ? room : null); RefreshParticipantAudio(); UpdateStatusBadge();
        mute.Text = isMuted ? "Микрофон выключен" : "Микрофон"; mute.Selected = mute.Crossed = isMuted; mute.Invalidate();
        deafen.Text = isDeafened ? "Наушники выключены" : "Наушники"; deafen.Selected = deafen.Crossed = isDeafened; deafen.Invalidate();
    }
    private void UpdateAudioOptions()
    {
        settings.PushToTalk = ptt.Checked; settings.TalkKey = (talkKey.SelectedItem as KeyChoice)?.Code ?? 0x56; settings.NoiseGate = gate.Checked; settings.NoiseSuppression = noise.Checked;
        settings.Volume = volume.Value; sounds.VolumePercent = settings.Volume; room?.ApplyAudioSettings(); talkKey.Enabled = ptt.Checked; volumeValue.Text = $"{volume.Value}%";
    }
    private void SaveSettings()
    {
        if (previewMode) return;
        settings.Name = string.IsNullOrWhiteSpace(name.Text) ? "Игрок" : name.Text.Trim();
        settings.Microphone = (microphones.SelectedItem as Device)?.Name ?? settings.Microphone;
        settings.Speakers = (speakers.SelectedItem as Device)?.Name ?? settings.Speakers;
        settingsTimer.Stop(); settingsTimer.Start();
    }
    private void PersistSettings() { if (previewMode) return; try { settings.Save(); } catch (Exception ex) { SetHint("Не удалось сохранить настройки: " + ex.Message); } }
    private void LoadDevices()
    {
        var mic = settings.Microphone; var output = settings.Speakers; microphones.Items.Clear(); speakers.Items.Clear();
        microphones.Items.Add(new Device(-1, "По умолчанию в Windows")); speakers.Items.Add(new Device(-1, "По умолчанию в Windows"));
        for (var i = 0; i < WaveIn.DeviceCount; i++) microphones.Items.Add(new Device(i, WaveIn.GetCapabilities(i).ProductName));
        for (var i = 0; i < WaveOut.DeviceCount; i++) speakers.Items.Add(new Device(i, WaveOut.GetCapabilities(i).ProductName));
        microphones.SelectedIndex = Math.Max(0, microphones.Items.Cast<Device>().ToList().FindIndex(d => d.Name == mic));
        speakers.SelectedIndex = Math.Max(0, speakers.Items.Cast<Device>().ToList().FindIndex(d => d.Name == output));
    }
    private void SetStatus(string title, string? description = null) { state.Text = title; if (description is not null) details.Text = description; UpdateStatusBadge(); }
    private void SetHint(string text) => hint.Text = text;
    private void UpdateStats()
    {
        SampleLatency(); UpdateVideoState(); process.Refresh(); var now = DateTime.UtcNow; var elapsed = Math.Max(0.01, (now - lastSample).TotalSeconds);
        var helpers = MediaRuntime.Resources(); var totalCpu = process.TotalProcessorTime + helpers.Cpu; var cpu = Math.Max(0, (totalCpu - lastCpu).TotalSeconds) / elapsed / Environment.ProcessorCount * 100;
        var sent = (room?.SentBytes ?? 0) + (room?.VideoSentBytes ?? 0); var received = (room?.ReceivedBytes ?? 0) + (room?.VideoReceivedBytes ?? 0);
        metrics.Text = $"CPU {cpu:0.0}%   RAM {(process.WorkingSet64 + helpers.Memory) / 1048576} МБ   ↑{Math.Max(0, sent - lastSent) * 8 / elapsed / 1000:0} ↓{Math.Max(0, received - lastReceived) * 8 / elapsed / 1000:0} кбит/с";
        lastSample = now; lastCpu = totalCpu; lastSent = sent; lastReceived = received;
    }
    private void HideToTray()
    {
        CloseExpandedVideo(); viewingVideo = false; SuspendVideoDecoders(); UiMotion.FinishAll(); Hide();
        if (balloonShown) return; balloonShown = true;
        tray?.ShowBalloonTip(3000, "Voicecon остаётся на связи", "Входящие звонки продолжат поступать. Для полного выхода используйте меню значка.", ToolTipIcon.Info);
    }
    private void ShowWindow() { windowRequested = true; Show(); WindowState = FormWindowState.Normal; Activate(); if (incomingCall is not null) ShowPage(false); viewingVideo = videoPage.Visible; }
    public void DeliverInstanceCommand(string command) => Dispatch(() => { if (command == "exit") { quitting = true; Close(); } else ShowWindow(); });
    protected override void SetVisibleCore(bool value)
    {
        if (value && startInTray && !windowRequested) { if (!IsHandleCreated) CreateHandle(); base.SetVisibleCore(false); return; }
        base.SetVisibleCore(value);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); ApplyTitleBar(); if (previewMode) return;
        RegisterShortcuts(); BeginInvoke(new Action(() => { InitializeContacts(); ScheduleUpdateCheck(); }));
    }
    protected override void OnResizeBegin(EventArgs e) { timer.Stop(); UiMotion.FinishAll(); base.OnResizeBegin(e); }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); Invalidate(true); if (!previewMode) timer.Start(); }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!previewMode && !quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; SaveSettings(); settingsTimer.Stop(); PersistSettings(); HideToTray(); return; }
        SaveSettings(); settingsTimer.Stop(); PersistSettings(); timer.Stop(); CancelOutgoing(); sounds.StopRing(); mailbox?.Dispose(); StopAllVideo(); room?.Dispose(); UnregisterShortcuts(); tray?.Dispose(); base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { UiMotion.Cancel(themeTrack); DisposeUpdates(); timer.Dispose(); keyPoll.Dispose(); settingsTimer.Dispose(); tips.Dispose(); CloseExpandedVideo(); StopShareSound(); SuspendVideoDecoders(); capture.Dispose(); foreach (var picture in pictures.Values) ReleasePictureImage(picture); sounds.Dispose(); mailbox?.Dispose(); room?.Dispose(); contacts?.Dispose(); tray?.Dispose(); process.Dispose(); ownedIcon?.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed record Device(int Index, string Name) { public override string ToString() => Name; }
    private sealed record KeyChoice(string Name, int Code) { public override string ToString() => Name; }
    private sealed record FriendChoice(string Name, FriendCard? Friend) { public override string ToString() => Name; }
    private sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record PendingCall(FriendCard Friend, string Id, string Code, DateTimeOffset Deadline);
}
