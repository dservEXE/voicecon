using System.Diagnostics;
using Microsoft.Win32;
using NAudio.Wave;

namespace Voicecon;

/// <summary>
/// One screen: friends and groups on the left, the selected conversation (call controls, participants, chat history) on the right,
/// call modes and microphone/headphones at the bottom. Settings slide in as a drawer; streams open in their own windows.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly Settings settings = Settings.Load();
    private readonly bool previewMode, startInTray;
    private bool windowRequested, initialized, quitting, busy, isMuted, isDeafened, balloonShown, deliveryReady;
    private VoiceRoom? room;
    private ContactBook? contacts;
    private CallMailbox? mailbox;
    private PresenceService? presence;
    private GroupBook? groups;
    private ChatStore? chats;
    private NotifyIcon? tray;
    private ToolStripMenuItem? stopVideoItem;
    private Icon? ownedIcon;
    private readonly UiSounds sounds = new();
    private CallAudioFeedback? callFeedback;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer settingsTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer contactsTimer = new() { Interval = 250 };
    private readonly Process process = Process.GetCurrentProcess();
    private DateTime lastSample = DateTime.UtcNow;
    private TimeSpan lastCpu;
    private long lastSent, lastReceived, lastRing;
    private int lastConnectedCount;
    private DateTimeOffset? callStarted;
    private PendingCall? incomingCall;
    private readonly Dictionary<string, OutgoingCall> outgoingCalls = [];
    private readonly List<VoiceRoom.ChatLine> chatHistory = [];
    private Keys[] registeredKeys = [];
    private readonly object themeTrack = new();
    private Conversation? selected, active;
    private int historyLimit = 300;
    private string deliveryText = "Подключаемся…";
    private string callStatus = "";

    public MainForm(bool previewMode = false, string? previewTheme = null, bool startInTray = false)
    {
        if (previewMode) settings.Name = "Игрок";
        UiSounds.WarmRingtones();
        this.previewMode = previewMode; this.startInTray = startInTray; windowRequested = !startInTray;
        if (!previewMode) settings.StartWithWindows = StartupManager.IsEnabled();
        UiTheme.Select(previewTheme ?? settings.Theme); UiMotion.Level = previewMode ? MotionLevel.Off : UiMotion.Parse(settings.AnimationLevel);
        BuildLayout(); ApplyTheme();
        if (previewMode)
        {
            microphones.Items.Add(new Device(-1, "По умолчанию в Windows")); speakers.Items.Add(new Device(-1, "По умолчанию в Windows"));
            microphones.SelectedIndex = speakers.SelectedIndex = 0; LoadPreviewData(); RefreshControls(); return;
        }
        BuildTray(); WireActions(); LoadDevices(); RefreshContacts(); RefreshControls();
        timer.Tick += (_, _) => { UpdateStats(); CheckCalls(); ScheduleUpdateCheck(); }; timer.Start();
        settingsTimer.Tick += (_, _) => { settingsTimer.Stop(); PersistSettings(); };
        contactsTimer.Tick += (_, _) => { contactsTimer.Stop(); RefreshContacts(); RefreshControls(); };
        Deactivate += (_, _) => UiMotion.FinishAll();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void BuildTray()
    {
        var menu = ThemeMenuRenderer.CreateMenu();
        menu.Items.Add("Открыть Voicecon", null, (_, _) => ShowWindow());
        menu.Items.Add("Микрофон: включить / выключить", null, (_, _) => ToggleMute());
        menu.Items.Add("Наушники: включить / выключить", null, (_, _) => ToggleDeafen());
        menu.Items.Add("Настройки", null, (_, _) => { ShowWindow(); OpenSettings(); });
        stopVideoItem = new ToolStripMenuItem("Остановить демонстрацию и камеру", null, (_, _) => StopOwnVideo()) { Enabled = false }; menu.Items.Add(stopVideoItem);
        menu.Items.Add("О приложении и лицензии", null, (_, _) => { using var about = new AboutDialog(); about.ShowDialog(this); });
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Выйти полностью", null, (_, _) => { quitting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "Voicecon", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowWindow(); tray.BalloonTipClicked += (_, _) => ShowWindow();
    }

    private void WireActions()
    {
        mute.Click += (_, _) => ToggleMute(); deafen.Click += (_, _) => ToggleDeafen();
        endButton.Click += (_, _) => EndCall();
        callButton.Click += async (_, _) => await Run(CallSelectedAsync);
        addFriend.Click += (_, _) => AddFriend(); ownCard.Click += (_, _) => ShowOwnCard();
        acceptCall.Click += async (_, _) => await Run(AcceptIncomingAsync); rejectCall.Click += (_, _) => RejectIncoming();
        sendChat.Click += (_, _) => SendMessage(); emoji.Click += (_, _) => OpenEmoji();
        chatInput.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendMessage(); } };
        settingsButton.Click += (_, _) => ToggleSettings();
        inviteButton.Click += (_, _) => ShowInviteMenu();
        olderMessages.Click += (_, _) => { historyLimit += 500; LoadHistory(); };
        modePicker.SelectedIndexChanged += (_, _) => SetVoiceMode(modePicker.SelectedIndex switch { 0 => VoiceMode.Normal, 2 => VoiceMode.Economy, _ => VoiceMode.Gaming });
        contactList.SelectionChanged += entry => SelectConversation(entry);
        contactList.EntryActivated += async entry => { SelectConversation(entry); if (room is not { IsDisposed: false, HasStarted: true }) await Run(CallSelectedAsync); };
        contactList.EntryMenu += (entry, point) => ShowContactMenu(entry, point);
        contactList.AddGroupClicked += () => CreateGroup();
        callStrip.PersonClicked += (person, point) => ShowPeerVolume(person, point);
        callStrip.StreamClicked += stream => ToggleWatch(stream.PeerId, stream.Name, stream.Kind);
        callStrip.StopClicked += own => { if (own.Kind == 1) StopScreen(); else _ = StopCameraAsync(); };
        WireVideo(); WireSettings();
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; RefreshControls();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Validation messages are expected; everything else is worth a diagnostic record.
            if (ex is not (InvalidOperationException or FormatException)) AppLog.Error("Ошибка действия", ex);
            SetStatus("Не удалось выполнить действие", ex.Message); SetHint(ex.Message);
        }
        finally { busy = false; RefreshControls(); }
    }

    // ---------------- Contacts, groups, presence ----------------
    private void InitializeContacts()
    {
        if (initialized || previewMode) return; initialized = true;
        string? notice = settings.RecoveredFrom is { } settingsBackup
            ? "Файл настроек был повреждён, восстановлены настройки по умолчанию. Копия старого файла: " + Path.GetFileName(settingsBackup) + "."
            : null;
        try { groups = new GroupBook(); } catch (Exception ex) { AppLog.Error("Группы недоступны", ex); }
        try { chats = new ChatStore(); } catch (Exception ex) { AppLog.Error("История чата недоступна", ex); notice = Join(notice, "История чата недоступна: " + ex.Message); }
        try { contacts = new ContactBook(); }
        catch (ContactBookCorruptException ex)
        {
            AppLog.Error("Файл контактов не открыт", ex);
            if (!ConfirmContactsReset(ex)) { SetDelivery("Контакты недоступны", false); SetHint(ex.Message + " Файл контактов не изменён; входящие звонки недоступны."); return; }
            try
            {
                var backup = ContactBook.PreserveCorrupt();
                contacts = new ContactBook();
                AppLog.Info("Создан новый профиль контактов, старый файл сохранён");
                notice = Join(notice, "Создан новый профиль контактов. Старый файл сохранён: " + Path.GetFileName(backup) + ". Отправьте друзьям новый код и добавьте их коды заново.");
            }
            catch (Exception inner) { AppLog.Error("Не удалось создать новый профиль контактов", inner); SetDelivery("Контакты недоступны", false); SetHint(inner.Message); return; }
        }
        catch (Exception ex)
        {
            // Transient read errors (file locked, disk unavailable): leave the file untouched.
            AppLog.Error("Не удалось загрузить контакты", ex);
            SetDelivery("Контакты недоступны", false); SetHint(ex.Message + " Файл контактов не изменён; перезапустите Voicecon."); return;
        }
        if (contacts.UnreadableFriends > 0)
            notice = Join(notice, $"Не удалось прочитать контактов: {contacts.UnreadableFriends}. Они сохранены в файле, но недоступны для звонков. Попросите у этих друзей код заново.");
        try { StartMailbox(); StartPresence(); RefreshContacts(); }
        catch (Exception ex) { AppLog.Error("Не удалось запустить доставку звонков", ex); SetDelivery("Доставка звонков недоступна", false); SetHint(ex.Message); return; }
        if (notice is not null) SetHint(notice);
    }
    private static string Join(string? first, string second) => first is null ? second : first + " " + second;
    private bool ConfirmContactsReset(ContactBookCorruptException error) =>
        MessageBox.Show(
            "Файл контактов не удалось открыть.\n" + error.Message +
            "\n\nСоздать новый профиль контактов?\n\n" +
            "• Старый файл будет сохранён рядом как резервная копия, ничего не удаляется.\n" +
            "• Ваш код друга изменится: отправьте его друзьям заново и добавьте их коды повторно.\n\n" +
            "«Нет» — оставить всё как есть; входящие звонки будут недоступны до следующего запуска.",
            "Voicecon · контакты", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void StartMailbox()
    {
        if (contacts is null || previewMode) return;
        mailbox?.Dispose(); mailbox = new CallMailbox(contacts, contacts.OwnCard(settings.Name, settings.SignalingServer));
        var current = mailbox;
        mailbox.ConnectivityChanged += ready => Dispatch(() => { if (mailbox == current) SetDelivery(ready ? "В сети · входящие доступны" : "Нет связи с сервисом доставки", ready); });
        mailbox.Received += (friend, signal) => Dispatch(async () =>
        {
            if (mailbox != current) return;
            try { await OnSignal(friend, signal); } catch (Exception ex) { AppLog.Error("Ошибка обработки входящего сигнала", ex); SetHint("Звонок: " + ex.Message); }
        });
        SetDelivery("Подключаем доставку звонков…", false); mailbox.Start();
    }
    private void StartPresence()
    {
        if (contacts is null || previewMode) return;
        presence?.Dispose();
        presence = new PresenceService(contacts.OwnCard(settings.Name, settings.SignalingServer));
        presence.Changed += _ => Dispatch(() => { contactsTimer.Stop(); contactsTimer.Start(); });
        presence.Start(contacts.Friends, settings.ShowOnlineStatus);
    }
    private void SetDelivery(string text, bool ready) { deliveryText = text; deliveryReady = ready; UpdateSelfCard(); }
    private void UpdateSelfCard() => selfCard.Set(contacts?.Id ?? "me", settings.Name, deliveryText, deliveryReady);
    private void Dispatch(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed && !quitting) action(); })); } catch (InvalidOperationException) { }
    }

    private PresenceInfo PresenceOf(string friendId) => presence?.Get(friendId) ?? new(PresenceStatus.Unknown, null);
    private static string Describe(PresenceInfo info) => info.Status switch
    {
        PresenceStatus.Online => "в сети",
        PresenceStatus.InCall => "в сети · в звонке",
        PresenceStatus.Offline when info.LastSeen is { } seen => "не в сети · был(а) " + FormatSeen(seen),
        _ => "не в сети"
    };
    private static string FormatSeen(DateTimeOffset seen)
    {
        var local = seen.ToLocalTime(); var today = DateTime.Today;
        return local.Date == today ? "в " + local.ToString("HH:mm") : local.Date == today.AddDays(-1) ? "вчера в " + local.ToString("HH:mm") : local.ToString("d MMM");
    }

    private void RefreshContacts()
    {
        if (previewMode) return;
        var activeKey = room is { IsDisposed: false, HasStarted: true } ? active?.Key : null;
        var groupEntries = (groups?.Groups ?? []).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g =>
        {
            var online = g.Members.Count(m => PresenceOf(m).Status is PresenceStatus.Online or PresenceStatus.InCall);
            var count = g.Members.Length + 1;
            var subtitle = activeKey == "g:" + g.Id ? "идёт звонок" : $"{count} {Plural(count, "участник", "участника", "участников")}" + (online > 0 ? $" · {online} в сети" : "");
            return new ContactEntry(ContactKind.Group, g.Id, g.Name, subtitle, PresenceStatus.Unknown, activeKey == "g:" + g.Id);
        }).ToList();
        var friendEntries = (contacts?.Friends ?? []).Select(f =>
        {
            var info = PresenceOf(f.Id); var inCall = activeKey == "f:" + f.Id || room is { IsDisposed: false } r && r.People.Any(p => p.Name == f.Name && p.Connected) && activeKey is not null;
            return new ContactEntry(ContactKind.Friend, f.Id, f.Name, inCall ? "в звонке с вами" : Describe(info), info.Status, inCall);
        }).OrderByDescending(e => e.Status is PresenceStatus.Online or PresenceStatus.InCall).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var wanted = selected?.Key ?? (settings.LastConversation.Length > 0 ? settings.LastConversation : null);
        contactList.SetEntries(groupEntries, friendEntries, wanted);
        if (selected is null || contactList.SelectedKey != selected.Key)
        {
            var entry = contactList.Entries.FirstOrDefault(e => e.Key == wanted) ?? contactList.Entries.FirstOrDefault(e => e.Kind == ContactKind.Friend) ?? contactList.Entries.FirstOrDefault();
            if (entry is not null) { contactList.Select(entry.Key, false); SelectConversation(entry); }
            else if (selected?.Kind != ContactKind.Friend || selected.Id != "manual") { selected = null; LoadHistory(); }
        }
        UpdateSelfCard(); UpdateHeader();
    }
    private static string Plural(int n, string one, string few, string many) => (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };

    private void SelectConversation(ContactEntry entry)
    {
        var conversation = new Conversation(entry.Key, entry.Kind, entry.Id, entry.Name);
        if (selected?.Key == conversation.Key) { selected = conversation; UpdateHeader(); RefreshControls(); return; }
        selected = conversation; historyLimit = 300;
        if (!previewMode && settings.LastConversation != entry.Key) { settings.LastConversation = entry.Key; if (entry.Kind == ContactKind.Friend) settings.LastFriendId = entry.Id; SaveSettings(); }
        LoadHistory(); UpdateHeader(); RefreshControls();
    }

    private FriendCard? Friend(string id) => contacts?.Friends.FirstOrDefault(f => f.Id == id);

    private void ShowContactMenu(ContactEntry entry, Point point)
    {
        var menu = ThemeMenuRenderer.CreateMenu();
        var inCall = room is { IsDisposed: false, HasStarted: true };
        menu.Items.Add(entry.Kind == ContactKind.Group ? "Позвонить группе" : "Позвонить", null, async (_, _) => await Run(CallSelectedAsync)).Enabled = !inCall;
        if (entry.Kind == ContactKind.Friend && inCall && room!.IsHost && room.CanInvite && Friend(entry.Id) is { } invitee)
            menu.Items.Add("Пригласить в текущий звонок", null, async (_, _) => await Run(() => CallFriendAsync(invitee)));
        if (entry.Kind == ContactKind.Group)
        {
            menu.Items.Add("Изменить группу…", null, (_, _) => EditGroup(entry.Id));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Удалить группу", null, (_, _) =>
            {
                if (MessageBox.Show(this, $"Удалить группу «{entry.Name}»? История её чата останется на этом ПК.", "Voicecon", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                groups?.Remove(entry.Id); if (selected?.Key == entry.Key) selected = null; RefreshContacts();
            });
        }
        else
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Удалить из друзей", null, (_, _) =>
            {
                if (MessageBox.Show(this, $"Удалить {entry.Name} из друзей? Звонки от него перестанут приходить. История чата останется на этом ПК.", "Voicecon", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                contacts?.Remove(entry.Id); presence?.SetFriends(contacts?.Friends ?? []); if (selected?.Key == entry.Key) selected = null; RefreshContacts();
            });
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(point);
    }

    // ---------------- Calls ----------------
    private Task CallSelectedAsync()
    {
        if (selected is null) throw new InvalidOperationException("Сначала добавьте друга и выберите его в списке слева.");
        if (selected.Kind == ContactKind.Group)
            return groups?.Find(selected.Id) is { } group ? CallGroupAsync(group) : throw new InvalidOperationException("Группа не найдена.");
        return Friend(selected.Id) is { } friend ? CallFriendAsync(friend) : throw new InvalidOperationException("Друг не найден.");
    }

    private void RequireDelivery()
    {
        if (mailbox is not { Connected: true }) throw new InvalidOperationException("Сервис доставки звонков ещё не подключён. Проверьте интернет или используйте разовый код в настройках для разработчиков.");
        if (incomingCall is not null) throw new InvalidOperationException("Сначала ответьте на входящий звонок.");
    }

    private async Task CallFriendAsync(FriendCard friend)
    {
        RequireDelivery();
        if (room is { IsDisposed: false, HasStarted: true } && !room.IsHost) throw new InvalidOperationException("Новых участников приглашает организатор. Завершите звонок, чтобы позвонить другому.");
        if (outgoingCalls.Values.Any(o => o.Friend.Id == friend.Id)) return;
        if (room is not { IsDisposed: false, HasStarted: true }) EnsureRoom(new Conversation("f:" + friend.Id, ContactKind.Friend, friend.Id, friend.Name));
        await InviteAsync(friend);
        SetStatus("Вызываем " + friend.Name + "…", "У друга прозвучит звонок, если Voicecon работает и он добавил ваш контакт.");
    }

    private async Task CallGroupAsync(ChatGroup group)
    {
        RequireDelivery();
        if (room is { IsDisposed: false, HasStarted: true }) throw new InvalidOperationException("Сначала завершите текущий звонок.");
        var members = group.Members.Select(Friend).OfType<FriendCard>().Take(VoiceRoom.Capacity - 1).ToList();
        if (members.Count == 0) throw new InvalidOperationException("В группе нет друзей из вашего списка. Добавьте участников через «Изменить группу».");
        EnsureRoom(new Conversation("g:" + group.Id, ContactKind.Group, group.Id, group.Name));
        room!.SetGroup(group.Id, group.Name);
        var failed = new List<string>();
        foreach (var member in members)
        {
            try { await InviteAsync(member); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) { failed.Add(member.Name); AppLog.Info("Не удалось вызвать участника группы: " + ex.Message); }
        }
        if (outgoingCalls.Count == 0) { EndRoomOnly(); throw new InvalidOperationException("Не удалось вызвать ни одного участника группы."); }
        SetStatus($"Вызываем группу «{group.Name}»…", failed.Count > 0 ? "Не удалось вызвать: " + string.Join(", ", failed) : null);
    }

    private async Task InviteAsync(FriendCard friend)
    {
        var (slot, code) = await room!.CreateInviteSlotAsync();
        var id = Guid.NewGuid().ToString("N");
        outgoingCalls[id] = new OutgoingCall(friend, id, slot, DateTimeOffset.UtcNow.AddSeconds(70));
        try { await mailbox!.SendAsync(friend, CallSignal.Create("offer", id, code, room.GroupId, room.GroupTitle)); }
        catch { outgoingCalls.Remove(id); room.CancelInvite(slot); if (outgoingCalls.Count == 0 && room.ConnectedCount == 0) EndRoomOnly(); throw; }
        RefreshControls();
    }

    private async Task OnSignal(FriendCard friend, CallSignal signal)
    {
        if (signal.Kind == "offer")
        {
            if (signal.Code is null) return; InviteCode.Decode(signal.Code);
            if (incomingCall is not null || room is { IsDisposed: false, HasStarted: true } || busy)
            { await mailbox!.SendAsync(friend, CallSignal.Create("busy", signal.Id)); return; }
            incomingCall = new(friend, signal.Id, signal.Code, DateTimeOffset.UtcNow.AddSeconds(45), signal.Group, signal.Title);
            incomingTitle.Text = signal.Group is null ? friend.Name + " звонит вам" : $"{friend.Name} зовёт в группу «{signal.Title ?? "Группа"}»";
            SetStatus("Вам звонит " + friend.Name, null); if (!previewMode) PlayRingtone();
            tray?.ShowBalloonTip(6000, "Входящий звонок", friend.Name + " звонит в Voicecon", ToolTipIcon.Info);
            RefreshControls(); AnimateIncoming();
        }
        else if (signal.Kind == "cancel" && incomingCall is { } pending && pending.Id == signal.Id && pending.Friend.Id == friend.Id)
        { ClearIncoming(); SetStatus("Вызов отменён", friend.Name + " завершил вызов."); }
        else if (outgoingCalls.TryGetValue(signal.Id, out var sent) && sent.Friend.Id == friend.Id)
        {
            if (signal.Kind == "answer" && signal.Code is not null && room is { IsDisposed: false })
            { room.AcceptAnswer(sent.Slot, signal.Code); sent.Answered = true; SetStatus("Подключаем " + friend.Name + "…"); }
            else if (signal.Kind is "reject" or "busy")
            {
                outgoingCalls.Remove(signal.Id); room?.CancelInvite(sent.Slot);
                SetHint(friend.Name + (signal.Kind == "busy" ? " сейчас занят." : " отклонил звонок."));
                if (outgoingCalls.Count == 0 && room is { ConnectedCount: 0 }) { EndRoomOnly(); SetStatus(signal.Kind == "busy" ? "Друг занят" : "Звонок отклонён", friend.Name); }
            }
        }
        RefreshControls();
    }

    private async Task AcceptIncomingAsync()
    {
        if (incomingCall is not { } pending || mailbox is null) return;
        ClearIncoming(); EndRoomOnly();
        var conversation = pending.Group is { } groupId && groups is not null
            ? ConversationForGroup(groups.Upsert(groupId, pending.Title ?? "Группа", [pending.Friend.Id]))
            : new Conversation("f:" + pending.Friend.Id, ContactKind.Friend, pending.Friend.Id, pending.Friend.Name);
        EnsureRoom(conversation); RefreshContacts();
        try { var answer = await room!.UseCodeAsync(pending.Code); await mailbox.SendAsync(pending.Friend, CallSignal.Create("answer", pending.Id, answer)); SetStatus("Подключаемся…"); }
        catch { EndRoomOnly(); await SendQuiet(pending.Friend, CallSignal.Create("reject", pending.Id)); throw; }
    }
    private static Conversation ConversationForGroup(ChatGroup group) => new("g:" + group.Id, ContactKind.Group, group.Id, group.Name);
    private void RejectIncoming()
    {
        if (incomingCall is { } pending) _ = SendQuiet(pending.Friend, CallSignal.Create("reject", pending.Id));
        ClearIncoming(); SetStatus("Звонок отклонён");
    }
    private async Task SendQuiet(FriendCard friend, CallSignal signal)
    { try { if (mailbox is not null) await mailbox.SendAsync(friend, signal); } catch { } }
    private void ClearIncoming() { incomingCall = null; sounds.StopRing(); UiMotion.Cancel(incomingPanel); RefreshControls(); }
    private void CancelOutgoing()
    {
        foreach (var call in outgoingCalls.Values.ToArray())
        {
            if (!call.Answered) _ = SendQuiet(call.Friend, CallSignal.Create("cancel", call.Id));
            room?.CancelInvite(call.Slot);
        }
        outgoingCalls.Clear();
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
        foreach (var expired in outgoingCalls.Values.Where(o => now > o.Deadline).ToArray())
        {
            outgoingCalls.Remove(expired.Id); if (!expired.Answered) _ = SendQuiet(expired.Friend, CallSignal.Create("cancel", expired.Id)); room?.CancelInvite(expired.Slot);
            SetHint(expired.Friend.Name + " не ответил.");
        }
        if (outgoingCalls.Count == 0 && room is { IsDisposed: false, HasStarted: true, ConnectedCount: 0, IsHost: true } && !room.WaitingForAnswer && callStarted is null)
        { EndRoomOnly(); SetStatus("Нет ответа", "Друг не принял звонок или его приложение недоступно."); }
        RefreshControls();
    }
    private void UpdateCallFeedback()
    {
        sounds.VolumePercent = settings.Volume;
        if (previewMode) return;
        callFeedback ??= new CallAudioFeedback(sounds);
        var connected = room is { IsDisposed: false } current ? current.ConnectedCount : 0;
        if (lastConnectedCount > 0 && connected == 0 && settings.NotificationSounds) sounds.PlayCallEnded(OutputDevice);
        if (connected > 0 && callStarted is null) callStarted = DateTimeOffset.UtcNow;
        lastConnectedCount = connected;
        var calling = outgoingCalls.Values.FirstOrDefault(o => !o.Answered)?.Id;
        callFeedback.Update(calling, connected, settings.NotificationSounds, OutputDevice, Environment.TickCount64);
    }
    private void Cue(NotificationCue cue) { if (!previewMode && settings.NotificationSounds) sounds.PlayCue(cue, OutputDevice); }
    private void SetVoiceMode(VoiceMode mode)
    {
        if (settings.AudioMode == mode) return;
        settings.AudioMode = mode; room?.ApplyAudioSettings(); SaveSettings();
        SetHint(mode switch { VoiceMode.Normal => "Обычный режим: голос 24 кбит/с, лучшее качество.", VoiceMode.Economy => "Суперэконом: 8 кбит/с, минимум трафика.", _ => "Игровой режим: 16 кбит/с, меньше нагрузки на сеть во время игры." });
    }
    private void PlayRingtone() { lastRing = Environment.TickCount64; sounds.PlayRing(settings.Ringtone, settings.RingtoneFile, OutputDevice); }
    private int OutputDevice => (speakers.SelectedItem as Device)?.Index ?? -1;

    private void EnsureRoom(Conversation conversation)
    {
        if (room is { IsDisposed: false }) return;
        SaveSettings(); latency.Clear(); chatHistory.Clear(); callStarted = null;
        active = conversation;
        if (selected?.Key != conversation.Key) { selected = conversation; contactList.Select(conversation.Key, false); historyLimit = 300; LoadHistory(); }
        room = new VoiceRoom(settings, (microphones.SelectedItem as Device)?.Index ?? -1, OutputDevice, localOnly: previewMode, enableAudio: !previewMode); room.SetMuted(isMuted, isDeafened);
        presence?.SetInCall(true);
        var current = room;
        room.Status += text => { if (room == current) SetStatus(text == "Голосовая связь установлена" ? "На связи" : text); };
        room.Changed += () =>
        {
            if (room != current) return;
            if (room.IsDisposed || room.ConnectedCount == 0 && callStarted is not null) { StopAllVideo(); }
            if (room.IsDisposed) { EndRoomOnly(); RefreshContacts(); }
            SyncMute(); RefreshControls();
        };
        room.ChatReceived += line => { if (room == current) OnChat(line); };
        room.VideoReceived += (id, name, bytes) => { if (room == current && !current.IsDisposed) ReceiveVideo(id, name, bytes); };
        room.VideoStopped += (id, kind, sequence) => { if (room == current) StopRemoteVideo(id, kind, sequence); };
        room.ParticipantLeft += id => { if (room == current) RemoveVideoParticipant(id); };
        room.PeerDisconnected += (_, name) => { if (room != current) return; if (current.ConnectedCount > 0) { Cue(NotificationCue.ParticipantLeft); SetHint(name + " вышел из звонка."); } };
        room.ParticipantJoined += id =>
        {
            if (room != current) return;
            // The host keys each invitee by its invitation slot: once connected, that outgoing call is done.
            foreach (var done in outgoingCalls.Values.Where(o => o.Slot == id).ToArray()) outgoingCalls.Remove(done.Id);
            contactsTimer.Stop(); contactsTimer.Start();
        };
        room.StreamsChanged += () => { if (room == current) { CloseEndedStreams(); RefreshControls(); } };
        room.StreamStarted += (_, name, kind) => { if (room == current) { Cue(NotificationCue.StreamStarted); SetHint(name + (kind == 1 ? " показывает экран" : " включил камеру") + " — нажмите «Смотреть», чтобы открыть."); } };
        room.ViewerChanged += (peer, kind, started) =>
        {
            if (room != current) return;
            Cue(started ? NotificationCue.ViewerJoined : NotificationCue.ViewerLeft);
            var who = current.Participants.FirstOrDefault(p => p.Id == peer)?.Name ?? "Участник";
            SetHint(who + (started ? " смотрит " : " перестал смотреть ") + (kind == 1 ? "вашу демонстрацию." : "вашу камеру."));
        };
        room.ConversationAnnounced += (groupId, title) =>
        {
            if (room != current || groups is null || incomingCallFriend is not { } host) return;
            var group = groups.Upsert(groupId, title, [host]); active = ConversationForGroup(group);
            selected = active; historyLimit = 300; RefreshContacts(); contactList.Select(active.Key, false); LoadHistory();
        };
        room.Error += error => { if (room == current) { EndCall(); SetStatus("Звонок остановлен", error); } };
        RefreshContacts();
    }
    private string? incomingCallFriend => active?.Kind == ContactKind.Friend ? active.Id : null;

    private void EndRoomOnly()
    {
        StopAllVideo(); var old = room; room = null; old?.Dispose();
        outgoingCalls.Clear(); callStarted = null; active = null; presence?.SetInCall(false);
        RefreshContacts(); RefreshControls();
    }
    private void EndCall()
    {
        var wasConnected = lastConnectedCount > 0 || room is { ConnectedCount: > 0 };
        CancelOutgoing(); if (incomingCall is not null) RejectIncoming(); EndRoomOnly();
        lastConnectedCount = 0;
        if (wasConnected && settings.NotificationSounds && !previewMode) sounds.PlayCallEnded(OutputDevice);
        lastSent = lastReceived = 0; SetStatus(wasConnected ? "Звонок завершён" : "Вызов отменён"); RefreshControls();
    }

    // ---------------- Chat ----------------
    private void OnChat(VoiceRoom.ChatLine line)
    {
        chatHistory.Add(line); if (chatHistory.Count > 200) chatHistory.RemoveAt(0);
        var record = new ChatRecord(line.Id, line.Name, line.Text, line.IsMine, line.Time);
        if (active is not null)
        {
            try { chats?.Append(active.Key, record); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("Не удалось сохранить сообщение", ex); }
        }
        if (selected?.Key == active?.Key) { chatView.Add(record); ScrollChatToEnd(); }
        if (!line.IsMine && settings.MessageSounds) Cue(NotificationCue.Message);
    }
    private void SendMessage()
    {
        try
        {
            if (room?.CanChat != true || selected?.Key != active?.Key) throw new InvalidOperationException("Чат доступен во время звонка с этим собеседником.");
            room.SendChat(chatInput.Text); chatInput.Clear();
        }
        catch (Exception ex) { SetHint(ex.Message); }
    }
    private void LoadHistory()
    {
        IReadOnlyList<ChatRecord> records = selected is null ? [] : previewMode ? previewChat : chats?.LoadRecent(selected.Key, historyLimit + 1) ?? [];
        var more = records.Count > historyLimit; if (more) records = records.Skip(records.Count - historyLimit).ToList();
        olderMessages.Visible = more;
        if (selected is not null && selected.Key == active?.Key && chats is null) records = records.Concat(chatHistory.Select(line => new ChatRecord(line.Id, line.Name, line.Text, line.IsMine, line.Time))).ToList();
        UpdateChatEmpty(); chatView.SetRecords(records); ScrollChatToEnd();
    }
    private void UpdateChatEmpty() => chatView.EmptyText = selected is null ? "Добавьте друга кнопкой + слева — и здесь появится ваша переписка."
        : $"Здесь будет история переписки с «{selected.Title}».\nСообщения хранятся только на ваших ПК и доступны во время звонка.";
    private void ScrollChatToEnd() => chatHost.ScrollToEnd();
    private void OpenEmoji()
    {
        var menu = ThemeMenuRenderer.CreateMenu(new Font("Segoe UI Emoji", 15));
        foreach (var value in new[] { "😀", "😂", "😎", "❤️", "👍", "🔥", "🎮", "🎯", "💀", "😅", "👀", "✅", "🙏", "😴", "🤝", "💬" }) menu.Items.Add(value, null, (_, _) => { chatInput.SelectedText = value; chatInput.Focus(); });
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose)); menu.Show(emoji, new Point(0, -menu.PreferredSize.Height - 4));
    }

    // ---------------- Friends & groups dialogs ----------------
    private void ShowOwnCard()
    {
        if (contacts is null) { SetHint("Контакты ещё загружаются."); return; }
        Clipboard.SetText(contacts.OwnCard(settings.Name, settings.SignalingServer).Encode()); SetHint("Ваш код друга скопирован. Отправьте его другу — и добавьте его код кнопкой +.");
    }
    private void AddFriend()
    {
        var code = contacts?.OwnCard(settings.Name, settings.SignalingServer).Encode();
        using var dialog = new AddFriendDialog(code) { Icon = Icon };
        if (dialog.ShowDialog(this) != DialogResult.OK || contacts is null) return;
        try
        {
            var friend = FriendCard.Decode(dialog.Code); contacts.Add(friend); presence?.SetFriends(contacts.Friends);
            RefreshContacts(); contactList.Select("f:" + friend.Id);
            SetHint("Контакт добавлен: " + friend.Name + ". Для звонков он тоже должен добавить ваш код.");
        }
        catch (Exception ex) { SetHint(ex.Message); }
    }
    private void CreateGroup()
    {
        if (groups is null || contacts is null) { SetHint("Группы станут доступны после загрузки контактов."); return; }
        if (contacts.Friends.Count == 0) { SetHint("Сначала добавьте друзей кнопкой +."); return; }
        using var dialog = new GroupDialog("Новая группа", "", contacts.Friends, []) { Icon = Icon };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var group = groups.Create(dialog.GroupName, dialog.Members); RefreshContacts(); contactList.Select("g:" + group.Id);
    }
    private void EditGroup(string id)
    {
        if (groups?.Find(id) is not { } group || contacts is null) return;
        using var dialog = new GroupDialog("Изменить группу", group.Name, contacts.Friends, group.Members) { Icon = Icon };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        groups.Update(group with { Name = dialog.GroupName, Members = dialog.Members.ToArray() }); RefreshContacts();
    }
    private void ShowInviteMenu()
    {
        if (room is not { IsDisposed: false, IsHost: true } || contacts is null) return;
        var inRoom = room.People.Select(p => p.Name).ToHashSet();
        var menu = ThemeMenuRenderer.CreateMenu();
        foreach (var friend in contacts.Friends.Where(f => !outgoingCalls.Values.Any(o => o.Friend.Id == f.Id)).OrderByDescending(f => PresenceOf(f.Id).Status is PresenceStatus.Online))
        {
            var info = PresenceOf(friend.Id);
            var item = menu.Items.Add($"{friend.Name}  ·  {Describe(info)}", null, async (_, _) => await Run(() => CallFriendAsync(friend)));
            item.Enabled = room.CanInvite && !inRoom.Contains(friend.Name);
        }
        if (menu.Items.Count == 0) menu.Items.Add("Нет друзей для приглашения").Enabled = false;
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(inviteButton, new Point(0, inviteButton.Height + 4));
    }
    private void ShowPeerVolume(CallStrip.PersonChip person, Point point)
    {
        if (room is not { IsDisposed: false } current) return;
        var audio = current.Participants.FirstOrDefault(p => p.Id == person.Id); if (audio is null) return;
        var popup = new PeerVolumePopup(person.Id, person.Name, current.PingTo(person.Id), audio.VoicePercent, audio.SharePercent, (voice, share) => current.SetParticipantVolume(person.Id, voice, share));
        popup.Closed += (_, _) => BeginInvoke(new Action(popup.Dispose));
        popup.Show(point);
    }

    // ---------------- Mute / state ----------------
    private void ToggleMute() { isMuted = !isMuted; SyncMute(); RefreshControls(); Cue(isMuted ? NotificationCue.MicrophoneOff : NotificationCue.MicrophoneOn); }
    private void ToggleDeafen() { isDeafened = !isDeafened; SyncMute(); RefreshControls(); Cue(isDeafened ? NotificationCue.HeadphonesOff : NotificationCue.HeadphonesOn); }
    private void SyncMute() => room?.SetMuted(isMuted, isDeafened);

    private void RefreshControls()
    {
        if (IsDisposed) return;
        var started = room is { IsDisposed: false, HasStarted: true };
        var connected = room is { IsDisposed: false } r && r.ConnectedCount > 0;
        var ringing = outgoingCalls.Count > 0 && !connected;
        var here = selected is not null && active is not null && selected.Key == active.Key;
        SetShown(callButton, !started && selected is not null);
        callButton.Text = selected?.Kind == ContactKind.Group ? "Позвонить группе" : "Позвонить";
        callButton.Enabled = !busy && incomingCall is null && (previewMode || mailbox is { Connected: true });
        SetShown(endButton, started || ringing);
        endButton.Text = connected ? "Завершить звонок" : "Отменить вызов";
        var sharing = capture.ScreenActive || capture.CameraActive;
        SetShown(shareButton, connected);
        shareButton.Text = sharing ? "Остановить показ" : "Демо / камера"; shareButton.Tone = sharing ? ButtonTone.Live : ButtonTone.Secondary; shareButton.Glyph = sharing ? UiIcon.Stop : UiIcon.Screen;
        SetShown(inviteButton, started && room!.IsHost && room.CanInvite && contacts is { Friends.Count: > 0 });
        var canChat = room?.CanChat == true && here;
        chatInput.Enabled = sendChat.Enabled = emoji.Enabled = canChat;
        chatInput.PlaceholderText = canChat ? "Сообщение… (Enter — отправить, Shift+Enter — новая строка)" : started && !here ? "Вы в звонке с другой беседой — выберите её слева" : "Чат доступен во время звонка";
        acceptCall.Enabled = rejectCall.Enabled = !busy;
        if (incomingRow is not null) incomingRow.Height = incomingCall is null ? 0 : 108 * DeviceDpi / 96f;
        incomingPanel.Visible = incomingCall is not null;
        UpdateCallFeedback(); UpdateUpdateControls(); UpdateHeader(); UpdateStrip();
        mute.Selected = mute.Crossed = isMuted; mute.AccessibleName = isMuted ? "Микрофон выключен" : "Микрофон включён"; mute.Invalidate();
        deafen.Selected = deafen.Crossed = isDeafened; deafen.AccessibleName = isDeafened ? "Звук выключен" : "Звук включён"; deafen.Invalidate();
        tips.SetToolTip(mute, (isMuted ? "Включить микрофон" : "Выключить микрофон") + ShortcutSuffix(settings.MicrophoneHotkey));
        tips.SetToolTip(deafen, (isDeafened ? "Включить звук" : "Выключить весь звук") + ShortcutSuffix(settings.HeadphonesHotkey));
        foreach (var button in new Control[] { callButton, endButton, shareButton, inviteButton }) button.Invalidate();
        FitActions(); UpdateVideoState();
    }
    /// <summary>Visibility that is also readable while the form is hidden (Control.Visible reports false then).</summary>
    private static void SetShown(Control control, bool shown) { control.Visible = shown; control.Tag = shown; }
    private static string ShortcutSuffix(int chord) => chord == 0 ? "" : " · " + HotkeyBox.Format((Keys)chord);

    private void UpdateHeader()
    {
        if (selected is null) { header.Set("", "", contacts is { Friends.Count: 0 } ? "Нажмите + слева, чтобы добавить первого друга по коду" : "Выберите друга или группу слева", PresenceStatus.Unknown, false, false); return; }
        var live = room is { IsDisposed: false, HasStarted: true } && active?.Key == selected.Key;
        string subtitle;
        if (live)
        {
            var count = room!.ConnectedCount;
            subtitle = count > 0
                ? $"В звонке · {count + 1} {Plural(count + 1, "участник", "участника", "участников")}" + (callStarted is { } since ? " · " + FormatDuration(DateTimeOffset.UtcNow - since) : "")
                : callStatus.Length > 0 ? callStatus : "Соединяем…";
        }
        else if (selected.Kind == ContactKind.Group)
        {
            var group = groups?.Find(selected.Id); var names = group?.Members.Select(m => Friend(m)?.Name).OfType<string>().ToArray() ?? [];
            subtitle = names.Length > 0 ? "Вы, " + string.Join(", ", names) : previewMode ? "Вы, Алекс, Мира" : "Группа";
        }
        else subtitle = previewMode ? "в сети" : selected.Id == "manual" ? "Разовое подключение по коду" : Describe(PresenceOf(selected.Id));
        header.Set(selected.Id, selected.Title, subtitle, selected.Kind == ContactKind.Friend ? (previewMode ? PresenceStatus.Online : PresenceOf(selected.Id).Status) : PresenceStatus.Unknown, selected.Kind == ContactKind.Group, live);
    }
    private static string FormatDuration(TimeSpan span) => span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");

    private void UpdateStrip()
    {
        var chips = new List<CallStrip.Chip>();
        if (room is { IsDisposed: false } current && current.HasStarted)
        {
            if (capture.ScreenActive) chips.Add(new CallStrip.OwnStreamChip(1, current.ViewerCount(1), capture.ScreenSize.Height > 0 ? $"{capture.ScreenSize.Height}p · {capture.Fps} к/с" : ""));
            if (capture.CameraActive) chips.Add(new CallStrip.OwnStreamChip(2, current.ViewerCount(2), ""));
            foreach (var person in current.People) chips.Add(new CallStrip.PersonChip(person.Id, person.Name, person.Connected, person.Connected ? current.PingTo(person.Id) : null));
            foreach (var stream in current.RemoteStreams) chips.Add(new CallStrip.RemoteStreamChip(stream.PeerId, stream.Name, stream.Kind, streamWindows.ContainsKey(stream.PeerId + ":" + stream.Kind)));
        }
        else if (previewChips.Count > 0) chips.AddRange(previewChips);
        callStrip.SetChips(chips);
        var height = callStrip.DesiredHeight(Math.Max(200, callStrip.Width));
        if (stripRow is not null && Math.Abs(stripRow.Height - height) > 0.5f) { var start = stripRow.Height; UiMotion.Run(stripRow, this, 200, p => stripRow.Height = start + (height - start) * p); }
    }

    private void UpdateAudioOptions()
    {
        settings.PushToTalk = ptt.Checked; settings.TalkKey = (talkKey.SelectedItem as KeyChoice)?.Code ?? 0x56; settings.NoiseGate = gate.Checked; settings.NoiseSuppression = noise.Checked;
        settings.Volume = volume.Value; sounds.VolumePercent = settings.Volume; room?.ApplyAudioSettings(); talkKey.Enabled = ptt.Checked; volumeValue.Text = $"{volume.Value}%";
    }
    private void SaveSettings()
    {
        if (previewMode) return;
        var oldName = settings.Name;
        settings.Name = string.IsNullOrWhiteSpace(name.Text) ? "Игрок" : name.Text.Trim();
        settings.Microphone = (microphones.SelectedItem as Device)?.Name ?? settings.Microphone;
        settings.Speakers = (speakers.SelectedItem as Device)?.Name ?? settings.Speakers;
        if (oldName != settings.Name) UpdateSelfCard();
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
    private void SetStatus(string title, string? description = null)
    {
        callStatus = title; UpdateHeader();
        if (description is not null) SetHint(description);
    }
    private void SetHint(string text)
    {
        hint.Text = text; hint.Visible = text.Length > 0;
        UiMotion.Run(hint, this, 260, p => { hint.ForeColor = UiTheme.Blend(UiTheme.Current.Background, UiTheme.Current.Muted, p); });
    }
    private void UpdateStats()
    {
        SampleLatency(); process.Refresh(); var now = DateTime.UtcNow; var elapsed = Math.Max(0.01, (now - lastSample).TotalSeconds);
        var helpers = MediaRuntime.Resources(); var totalCpu = process.TotalProcessorTime + helpers.Cpu; var cpu = Math.Max(0, (totalCpu - lastCpu).TotalSeconds) / elapsed / Environment.ProcessorCount * 100;
        var sent = (room?.SentBytes ?? 0) + (room?.VideoSentBytes ?? 0); var received = (room?.ReceivedBytes ?? 0) + (room?.VideoReceivedBytes ?? 0);
        metrics.Text = settings.ShowMetrics ? $"CPU {cpu:0.0}%  ·  {(process.WorkingSet64 + helpers.Memory) / 1048576} МБ  ·  ↑{Math.Max(0, sent - lastSent) * 8 / elapsed / 1000:0} ↓{Math.Max(0, received - lastReceived) * 8 / elapsed / 1000:0} кбит/с" : "";
        lastSample = now; lastCpu = totalCpu; lastSent = sent; lastReceived = received;
        if (room is { IsDisposed: false, ConnectedCount: > 0 }) { UpdateHeader(); UpdateStrip(); }
    }
    private void HideToTray()
    {
        UiMotion.FinishAll(); Hide();
        if (balloonShown) return; balloonShown = true;
        tray?.ShowBalloonTip(3000, "Voicecon остаётся на связи", "Входящие звонки продолжат поступать. Для полного выхода используйте меню значка.", ToolTipIcon.Info);
    }
    private void ShowWindow() { windowRequested = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
    public void DeliverInstanceCommand(string command) => Dispatch(() => { if (command == "exit") { quitting = true; Close(); } else ShowWindow(); });
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) presence?.PublishOffline(TimeSpan.FromSeconds(1));
        else if (e.Mode == PowerModes.Resume) _ = Task.Delay(5000).ContinueWith(_ => presence?.Resume());
    }
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
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); if (previewMode) return;
        var warm = new System.Windows.Forms.Timer { Interval = 1500 };
        warm.Tick += (_, _) => { warm.Dispose(); WarmSettings(); }; warm.Start();
    }
    protected override void OnResizeBegin(EventArgs e) { timer.Stop(); UiMotion.FinishAll(); base.OnResizeBegin(e); }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); Invalidate(true); if (!previewMode) timer.Start(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); PlaceDrawer(false); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && settingsOpen) { CloseSettings(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!previewMode && !quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; SaveSettings(); settingsTimer.Stop(); PersistSettings(); HideToTray(); return; }
        SaveSettings(); settingsTimer.Stop(); PersistSettings(); timer.Stop(); CancelOutgoing(); sounds.StopRing(); presence?.PublishOffline(TimeSpan.FromSeconds(1.5));
        mailbox?.Dispose(); StopAllVideo(); room?.Dispose(); UnregisterShortcuts(); tray?.Dispose(); base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            UiMotion.Cancel(themeTrack); DisposeUpdates(); timer.Dispose(); keyPoll.Dispose(); settingsTimer.Dispose(); contactsTimer.Dispose(); tips.Dispose(); StopAllVideo(); capture.Dispose();
            sounds.Dispose(); mailbox?.Dispose(); presence?.Dispose(); room?.Dispose(); contacts?.Dispose(); tray?.Dispose(); process.Dispose(); ownedIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed record Device(int Index, string Name) { public override string ToString() => Name; }
    private sealed record KeyChoice(string Name, int Code) { public override string ToString() => Name; }
    private sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record Conversation(string Key, ContactKind Kind, string Id, string Title);
    private sealed record PendingCall(FriendCard Friend, string Id, string Code, DateTimeOffset Deadline, string? Group = null, string? Title = null);
    private sealed class OutgoingCall(FriendCard friend, string id, string slot, DateTimeOffset deadline)
    {
        public FriendCard Friend { get; } = friend; public string Id { get; } = id; public string Slot { get; } = slot; public DateTimeOffset Deadline { get; } = deadline; public bool Answered { get; set; }
    }
}
