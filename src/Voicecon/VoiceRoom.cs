using System.Text.Json;
using SIPSorcery.Net;

namespace Voicecon;

/// <summary>Host routes only control messages; every voice stream is direct peer-to-peer.</summary>
public sealed class VoiceRoom : IDisposable
{
    public const int Capacity = 6;
    /// <summary>Capabilities this build announces to every peer. Peers without "watch" are treated as legacy (always sent video).</summary>
    public const string Capabilities = "watch,conversation";
    private readonly Settings settings;
    private readonly SynchronizationContext context;
    private readonly Dictionary<string, Member> members = new();
    private readonly Dictionary<string, string> roster = new();
    private readonly HashSet<Member> pendings = [];
    private readonly int microphone, speakers;
    private readonly bool localOnly, enableAudio;
    private string id = Guid.NewGuid().ToString("N"), hostId = "";
    private Member? hostLink, lastSlot;
    private bool disposed;
    private bool muted, deafened;
    public bool IsHost { get; private set; }
    public bool HasStarted { get; private set; }
    public AudioEngine? Audio { get; private set; }
    /// <summary>Group conversation this room belongs to (set by the host, delivered to guests).</summary>
    public string? GroupId { get; private set; }
    public string? GroupTitle { get; private set; }
    public string SelfId => id;
    public event Action? Changed;
    public event Action<string>? Status;
    public event Action<string>? Error;
    public event Action<ChatLine>? ChatReceived;
    public event Action<string, string, byte[]>? VideoReceived;
    public event Action<string, byte, uint>? VideoStopped;
    public event Action<string>? ParticipantLeft;
    public event Action<string>? ParticipantJoined;
    /// <summary>A connected participant left (not a pending invitation that expired).</summary>
    public event Action<string, string>? PeerDisconnected;
    /// <summary>Remote stream list or own viewer counts changed.</summary>
    public event Action? StreamsChanged;
    /// <summary>Someone started (true) or stopped (false) watching one of our streams.</summary>
    public event Action<string, byte, bool>? ViewerChanged;
    /// <summary>A peer announced a new screen (1) or camera (2) stream.</summary>
    public event Action<string, string, byte>? StreamStarted;
    /// <summary>Host told us which group conversation this call belongs to.</summary>
    public event Action<string, string>? ConversationAnnounced;
    public long VideoSentBytes => members.Values.Sum(m => Interlocked.Read(ref m.Session.VideoSentBytes));
    public long VideoReceivedBytes => members.Values.Sum(m => Interlocked.Read(ref m.Session.VideoReceivedBytes));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShareAudioPlayer> sharePlayers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> implicitStreams = new();
    private readonly Dictionary<byte, StreamAnnouncement> ownStreams = [];
    private uint outgoingShareStream;
    public void StartShareAudio(uint stream) { outgoingShareStream = stream; foreach (var m in members.Values.Where(m => m.Connected)) Send(m, new Message { Type = "share-audio-start", AudioStream = stream }); }
    public void StopShareAudio() { var old = outgoingShareStream; outgoingShareStream = 0; if (old != 0) foreach (var m in members.Values.Where(m => m.Connected)) Send(m, new Message { Type = "share-audio-stop", AudioStream = old }); }
    /// <summary>Screen sound goes only to people watching the screen (and to older clients, which always receive it).</summary>
    public void SendShareAudio(byte[] bytes) { foreach (var target in screenTargets) target.SendShareAudio(bytes); }
    private readonly HashSet<string> chatSeen = [];
    public bool CanChat => !disposed && ConnectedCount > 0 && (IsHost || hostLink is { Joined: true });
    public int ConnectedCount => members.Values.Count(m => m.Connected);
    public bool CanInvite => (!HasStarted || IsHost) && members.Count < Capacity - 1;
    public bool WaitingForAnswer => pendings.Count > 0;
    public IReadOnlyList<string> ParticipantNames => new[] { settings.Name + " (вы)" }
        .Concat(roster.Where(p => p.Key != id).Select(p => p.Value + (members.TryGetValue(p.Key, out var m) && m.Connected ? " · в сети" : " · соединяем…"))).ToList();
    public sealed record ParticipantAudio(string Id, string Name, int VoicePercent, int SharePercent) { public override string ToString() => Name; }
    public IReadOnlyList<ParticipantAudio> Participants => members.Values.Where(m => m.Connected).Select(m => new ParticipantAudio(m.Id, m.Name, m.VoiceVolume, m.ShareVolume)).ToArray();
    /// <summary>Everyone in the room as seen locally: connected peers plus roster entries still connecting.</summary>
    public IReadOnlyList<(string Id, string Name, bool Connected)> People =>
        roster.Where(p => p.Key != id).Select(p => (p.Key, p.Value, members.TryGetValue(p.Key, out var m) && m.Connected))
            .Concat(members.Values.Where(m => m.Connected && !roster.ContainsKey(m.Id)).Select(m => (m.Id, m.Name, true))).ToArray();
    public sealed record RemoteStream(string PeerId, string Name, byte Kind, int Width, int Height, int Fps, bool Watching);
    public IReadOnlyList<RemoteStream> RemoteStreams => members.Values.Where(m => m.Connected)
        .SelectMany(m => m.Streams.Values.Select(s => new RemoteStream(m.Id, m.Name, s.Kind, s.Width, s.Height, s.Fps, m.WatchingTheirs.Contains(s.Kind)))).ToArray();
    public int ViewerCount(byte kind) => members.Values.Count(m => m.Connected && m.Modern && m.Watching.Contains(kind));
    public IReadOnlyList<(string Name, string Route)> ConnectionRoutes
    {
        get
        {
            var active = members.Values.Where(m => m.Connected).ToArray();
            return active.Select(m =>
            (
                active.Count(other => other.Name == m.Name) > 1 ? m.Name + " · " + m.Id[..4] : m.Name,
                m.Session.ConnectionRoute ?? "Определяется"
            )).ToArray();
        }
    }
    public void SetParticipantVolume(string peerId, int voicePercent, int sharePercent)
    {
        if (!members.TryGetValue(peerId, out var member) || disposed) return;
        member.VoiceVolume = Math.Clamp(voicePercent, 0, AudioLevels.MaximumPercent); member.ShareVolume = Math.Clamp(sharePercent, 0, AudioLevels.MaximumPercent);
        Audio?.Jitter.SetPeerVolume(peerId, member.VoiceVolume);
        if (sharePlayers.TryGetValue(peerId, out var player)) player.Volume = AudioLevels.Gain(settings.Volume) * AudioLevels.Gain(member.ShareVolume);
    }
    public long SentBytes => members.Values.Sum(m => m.Session.SentBytes);
    public long ReceivedBytes => members.Values.Sum(m => m.Session.ReceivedBytes);

    public VoiceRoom(Settings settings, int microphone, int speakers, bool localOnly = false, bool enableAudio = true)
    {
        this.settings = settings; this.microphone = microphone; this.speakers = speakers;
        this.localOnly = localOnly; this.enableAudio = enableAudio;
        context = SynchronizationContext.Current ?? throw new InvalidOperationException("VoiceRoom requires a UI synchronization context.");
    }

    /// <summary>Marks this room as a call into a saved group (host only, before inviting).</summary>
    public void SetGroup(string groupId, string title) { GroupId = groupId; GroupTitle = title; }

    /// <summary>Creates an invitation slot. Several can be pending at once (group calls).</summary>
    public async Task<(string Slot, string Code)> CreateInviteSlotAsync()
    {
        if (!CanInvite) throw new InvalidOperationException("В комнате может быть до 6 человек. Новых участников приглашает организатор.");
        IsHost = true; HasStarted = true; hostId = id;
        roster[id] = settings.Name;
        var slot = CreateMember(Guid.NewGuid().ToString("N"), "Друг", true);
        pendings.Add(slot); lastSlot = slot;
        try { return (slot.Id, await slot.Session.CreateInviteAsync()); }
        catch { Remove(slot.Id); throw; }
    }
    public async Task<string> CreateInviteAsync() => (await CreateInviteSlotAsync()).Code;

    /// <summary>Applies a friend's answer to a specific invitation slot.</summary>
    public void AcceptAnswer(string slot, string code)
    {
        var description = InviteCode.Decode(code);
        if (description.type != RTCSdpType.answer) throw new InvalidOperationException("Нужен ответ друга на ваше приглашение.");
        if (!IsHost || !members.TryGetValue(slot, out var member) || !pendings.Contains(member)) throw new InvalidOperationException("Приглашение уже недействительно.");
        member.Session.AcceptAnswer(description); ArmTimeout(member);
    }

    public async Task<string?> UseCodeAsync(string code)
    {
        var description = InviteCode.Decode(code);
        if (description.type == RTCSdpType.answer)
        {
            var slot = lastSlot is not null && pendings.Contains(lastSlot) ? lastSlot : pendings.LastOrDefault();
            if (!IsHost || slot is null) throw new InvalidOperationException("Сначала создайте приглашение, затем вставьте ответ на него.");
            slot.Session.AcceptAnswer(description);
            ArmTimeout(slot);
            return null;
        }
        if (HasStarted) throw new InvalidOperationException("Чтобы войти в другую комнату, сначала завершите текущий звонок.");
        IsHost = false; HasStarted = true;
        hostLink = CreateMember("@host", "Организатор", true);
        var result = await hostLink.Session.AnswerAsync(description);
        ArmTimeout(hostLink);
        return result;
    }

    public void CancelInvite()
    {
        if (!IsHost) return;
        var slot = lastSlot is not null && pendings.Contains(lastSlot) ? lastSlot : pendings.LastOrDefault();
        if (slot is null) return;
        Remove(slot.Id); Changed?.Invoke();
    }
    public void CancelInvite(string slot)
    {
        if (!IsHost || !members.TryGetValue(slot, out var member) || !pendings.Contains(member)) return;
        Remove(slot); Changed?.Invoke();
    }

    private Member CreateMember(string key, string name, bool hostConnection)
    {
        var member = new Member(key, name, new CallSession(settings, localOnly), hostConnection);
        members.Add(key, member);
        member.Session.Status += text => Dispatch(() => Status?.Invoke(text));
        member.Session.StateChanged += state => Dispatch(() => OnState(member, state));
        member.Session.ControlReady += () => Dispatch(() => OnReady(member));
        member.Session.ControlMessage += text => Dispatch(async () =>
        {
            try { await OnMessage(member, text); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { AppLog.Error("Ошибка служебного сообщения участника", ex); if (!disposed) Error?.Invoke("Ошибка подключения участника: " + ex.Message); }
        });
        member.Session.PacketReceived += (seq, data) => Audio?.Jitter.Receive(member.Id, seq, data);
        member.Session.ShareAudioReceived += bytes =>
        {
            lock (member)
            {
                if (!enableAudio || disposed || !member.Connected || bytes.Length < 7 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) != member.ShareStream || member.ShareStream == 0) return;
                try { var player = sharePlayers.GetOrAdd(member.Id, _ => new ShareAudioPlayer(speakers)); player.Volume = AudioLevels.Gain(settings.Volume) * AudioLevels.Gain(member.ShareVolume); player.Deafened = deafened; player.Add(bytes); }
                catch (Exception ex) { Dispatch(() => Status?.Invoke("Не удалось воспроизвести звук демонстрации: " + ex.Message)); }
            }
        };
        member.Session.VideoFrame += bytes =>
        {
            // Older clients stream without announcing: surface their stream once so the user can choose to watch.
            if (bytes.Length > 0 && !member.Modern && implicitStreams.TryAdd(member.Id + ":" + bytes[0], 0))
            {
                var kind = bytes[0];
                Dispatch(() =>
                {
                    if (member.Streams.ContainsKey(kind)) return;
                    member.Streams[kind] = new StreamAnnouncement(kind, 0, 0, 0);
                    StreamStarted?.Invoke(member.Id, member.Name, kind); StreamsChanged?.Invoke();
                });
            }
            VideoReceived?.Invoke(member.Id, member.Name, bytes);
        };
        return member;
    }

    private void SetShareStream(Member member, uint stream)
    {
        lock (member) { if (member.ShareStream == stream) return; member.ShareStream = stream; if (sharePlayers.TryRemove(member.Id, out var player)) player.Dispose(); }
    }
    private void Dispatch(Action action)
    {
        context.Post(_ => { if (!disposed) action(); }, null);
    }

    private void OnState(Member member, RTCPeerConnectionState state)
    {
        if (!members.ContainsValue(member)) return;
        if (state == RTCPeerConnectionState.connected)
        {
            member.Connected = true;
            ParticipantJoined?.Invoke(member.Id);
            UpdateTargets();
            if (enableAudio)
            {
                try
                {
                    if (Audio is null)
                    {
                        Audio = new AudioEngine(microphone, speakers, BroadcastAudio);
                        Audio.Error += text => Dispatch(() => Error?.Invoke(text));
                        ApplyAudioSettings();
                        Audio.Jitter.AddPeer(member.Id); Audio.Start();
                    }
                    else Audio.Jitter.AddPeer(member.Id);
                }
                catch (Exception ex) { Error?.Invoke("Не удалось включить звук: " + ex.Message); }
            }
            Status?.Invoke("Голосовая связь установлена");
        }
        else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
        {
            var lostHost = !IsHost && member == hostLink;
            Remove(member.Id);
            if (lostHost) { Status?.Invoke("Организатор завершил звонок или соединение потеряно."); Dispose(); }
            else if (IsHost) PublishRoster();
        }
        else if (state == RTCPeerConnectionState.disconnected) Status?.Invoke("Связь прервалась, пробуем восстановить…");
        Changed?.Invoke();
    }

    private void OnReady(Member member)
    {
        Send(member, new Message { Type = "caps", Text = Capabilities });
        if (outgoingShareStream != 0) Send(member, new Message { Type = "share-audio-start", AudioStream = outgoingShareStream });
        foreach (var stream in ownStreams.Values) Send(member, new Message { Type = "video-start", VideoKind = stream.Kind, Width = stream.Width, Height = stream.Height, Fps = stream.Fps });
        if (!member.HostConnection) return;
        if (IsHost) Send(member, new Message { Type = "welcome", You = member.Id, From = id, Name = settings.Name, Code = GroupId, Text = GroupTitle });
        else Send(member, new Message { Type = "hello", Name = settings.Name });
    }

    private async Task OnMessage(Member source, string json)
    {
        var message = JsonSerializer.Deserialize<Message>(json);
        if (message is null) return;
        switch (message.Type)
        {
            case "caps":
                source.Modern = (message.Text ?? "").Split(',').Contains("watch");
                UpdateTargets(); break;
            case "share-audio-start" when message.AudioStream != 0:
                SetShareStream(source, message.AudioStream); break;
            case "share-audio-stop" when message.AudioStream == source.ShareStream:
                SetShareStream(source, 0); break;
            case "video-start" when message.VideoKind is 1 or 2:
                var fresh = !source.Streams.ContainsKey(message.VideoKind);
                source.Streams[message.VideoKind] = new StreamAnnouncement(message.VideoKind, Math.Clamp(message.Width, 0, 7680), Math.Clamp(message.Height, 0, 4320), Math.Clamp(message.Fps, 0, 240));
                if (fresh) StreamStarted?.Invoke(source.Id, source.Name, message.VideoKind);
                StreamsChanged?.Invoke(); break;
            case "video-stop" when message.VideoKind is 1 or 2:
                source.Streams.Remove(message.VideoKind); source.WatchingTheirs.Remove(message.VideoKind); implicitStreams.TryRemove(source.Id + ":" + message.VideoKind, out _);
                VideoStopped?.Invoke(source.Id, message.VideoKind, message.VideoSequence); StreamsChanged?.Invoke(); break;
            case "watch" when message.VideoKind is 1 or 2:
                var watching = message.Text == "1";
                if (watching ? source.Watching.Add(message.VideoKind) : source.Watching.Remove(message.VideoKind))
                {
                    if (watching) source.Session.RequireKey(message.VideoKind);
                    UpdateTargets(); ViewerChanged?.Invoke(source.Id, message.VideoKind, watching); StreamsChanged?.Invoke();
                }
                break;
            case "welcome" when !IsHost && source == hostLink:
                if (!ValidId(message.You) || !ValidId(message.From)) return;
                id = message.You!; hostId = message.From!;
                members.Remove(source.Id);
                Audio?.Jitter.RemovePeer(source.Id);
                source.Id = hostId; source.Name = CleanName(message.Name);
                members[hostId] = source;
                if (source.Connected) Audio?.Jitter.AddPeer(hostId);
                roster[hostId] = source.Name; roster[id] = settings.Name;
                source.Joined = true;
                if (ValidId(message.Code)) { GroupId = message.Code; GroupTitle = GroupBook.Clean(message.Text ?? "Группа"); ConversationAnnounced?.Invoke(GroupId!, GroupTitle); }
                Send(source, new Message { Type = "hello", Name = settings.Name });
                UpdateTargets();
                break;
            case "hello" when IsHost && source.HostConnection:
                source.Name = CleanName(message.Name); source.Joined = true; roster[source.Id] = source.Name;
                pendings.Remove(source);
                PublishRoster();
                break;
            case "roster" when !IsHost && source == hostLink:
                if (message.Peers is null || message.Peers.Count > Capacity || !message.Peers.ContainsKey(id)) return;
                foreach (var existing in members.Values.Where(m => m != hostLink && !message.Peers.ContainsKey(m.Id)).ToList()) Remove(existing.Id);
                roster.Clear();
                foreach (var entry in message.Peers.Where(p => ValidId(p.Key))) roster[entry.Key] = CleanName(entry.Value);
                foreach (var target in roster.Keys.Where(key => key != id && key != hostId && !members.ContainsKey(key)).ToArray())
                {
                    if (disposed || members.ContainsKey(target) || !roster.ContainsKey(target)) continue;
                    if (string.CompareOrdinal(id, target) < 0)
                    {
                        var link = CreateMember(target, roster[target], false);
                        var offer = await link.Session.CreateInviteAsync();
                        Route(target, offer); ArmTimeout(link);
                    }
                }
                break;
            case "signal" when IsHost && source.HostConnection && source.Joined:
                if (message.To is not null && members.TryGetValue(message.To, out var targetMember) && targetMember.Joined)
                    Send(targetMember, new Message { Type = "signal", From = source.Id, To = targetMember.Id, Code = message.Code });
                break;
            case "signal" when !IsHost && source == hostLink:
                if (message.From is null || message.From == id || !roster.ContainsKey(message.From) || message.Code is null) return;
                var description = InviteCode.Decode(message.Code);
                if (!members.TryGetValue(message.From, out var remote))
                {
                    if (description.type != RTCSdpType.offer || members.Count >= Capacity - 1) return;
                    remote = CreateMember(message.From, roster[message.From], false);
                }
                if (description.type == RTCSdpType.offer)
                    Route(remote.Id, await remote.Session.AnswerAsync(description));
                else remote.Session.AcceptAnswer(description);
                ArmTimeout(remote);
                break;
            case "chat" when IsHost && source.HostConnection && source.Joined:
                if (ValidChat(message.Text) && Guid.TryParse(message.ChatId, out _) && Environment.TickCount64 - source.LastChat >= 120)
                {
                    source.LastChat = Environment.TickCount64;
                    PublishChat(message.ChatId!, source.Id, source.Name, message.Text!);
                }
                break;
            case "chat-delivery" when !IsHost && source == hostLink:
                if (ValidChat(message.Text) && Guid.TryParse(message.ChatId, out _) && message.From is not null && roster.ContainsKey(message.From))
                    DeliverChat(message.ChatId!, message.From, roster[message.From], message.Text!);
                break;
        }
        Changed?.Invoke();
    }
    public (string Name, double? Ping)[] SampleLatency() { var active = members.Values.Where(m => m.Connected).ToArray(); foreach (var m in active) m.Session.ProbeLatency(); return active.Select(m => (active.Count(other => other.Name == m.Name) > 1 ? m.Name + " · " + m.Id[..4] : m.Name, m.Session.Latency.Milliseconds)).ToArray(); }
    public double? PingTo(string peerId) => members.TryGetValue(peerId, out var m) ? m.Session.Latency.Milliseconds : null;

    // ---- Streams: announce, watch, send only to viewers ----
    /// <summary>Announces our own stream so peers can choose to watch it.</summary>
    public void AnnounceStream(byte kind, int width, int height, int fps)
    {
        ownStreams[kind] = new StreamAnnouncement(kind, width, height, fps);
        foreach (var member in members.Values.Where(m => m.Connected)) Send(member, new Message { Type = "video-start", VideoKind = kind, Width = width, Height = height, Fps = fps });
        StreamsChanged?.Invoke();
    }
    public bool IsStreaming(byte kind) => ownStreams.ContainsKey(kind);
    /// <summary>Starts or stops receiving a peer's stream. Modern senders transmit only while someone watches.</summary>
    public void Watch(string peerId, byte kind, bool watch)
    {
        if (!members.TryGetValue(peerId, out var member) || !member.Connected) return;
        if (watch ? !member.WatchingTheirs.Add(kind) : !member.WatchingTheirs.Remove(kind)) return;
        Send(member, new Message { Type = "watch", VideoKind = kind, Text = watch ? "1" : "0" });
        StreamsChanged?.Invoke();
    }
    public void SendVideo(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        foreach (var target in bytes[0] == 2 ? cameraTargets : screenTargets) target.SendVideo(bytes);
    }
    public bool WantsVideo(byte kind) => (kind == 2 ? cameraTargets : screenTargets).Any(target => target.WantsVideo(kind));
    public void StopVideo(byte kind, uint sequence)
    {
        ownStreams.Remove(kind);
        foreach (var member in members.Values.Where(m => m.Connected)) { Send(member, new Message { Type = "video-stop", VideoKind = kind, VideoSequence = sequence }); member.Watching.Remove(kind); }
        UpdateTargets(); StreamsChanged?.Invoke();
    }

    public void SendChat(string text)
    {
        text = text.Trim();
        if (!CanChat) throw new InvalidOperationException("Чат доступен после подключения к звонку.");
        if (!ValidChat(text)) throw new InvalidOperationException("Сообщение должно содержать от 1 до 2000 символов.");
        var chatId = Guid.NewGuid().ToString("N");
        if (IsHost) PublishChat(chatId, id, settings.Name, text);
        else Send(hostLink!, new Message { Type = "chat", ChatId = chatId, Text = text });
    }
    private static bool ValidChat(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 2000 && !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
    private void PublishChat(string chatId, string from, string name, string text)
    {
        if (chatSeen.Contains(chatId)) return;
        DeliverChat(chatId, from, name, text);
        foreach (var member in members.Values.Where(m => m.Joined))
            Send(member, new Message { Type = "chat-delivery", ChatId = chatId, From = from, Text = text });
    }
    private void DeliverChat(string chatId, string from, string name, string text)
    {
        if (!chatSeen.Add(chatId)) return;
        if (chatSeen.Count > 512) { chatSeen.Clear(); chatSeen.Add(chatId); }
        ChatReceived?.Invoke(new ChatLine(chatId, name, text, from == id, DateTimeOffset.Now, from));
    }
    public sealed record ChatLine(string Id, string Name, string Text, bool IsMine, DateTimeOffset Time, string From = "");

    private static bool ValidId(string? value) => value is not null && Guid.TryParseExact(value, "N", out _);
    private static string CleanName(string? value) => string.IsNullOrWhiteSpace(value) ? "Друг" : new string(value.Where(c => !char.IsControl(c)).Take(24).ToArray());
    private static void Send(Member member, Message message) => member.Session.SendControl(JsonSerializer.Serialize(message));
    private void Route(string target, string code)
    {
        if (hostLink is not null) Send(hostLink, new Message { Type = "signal", To = target, Code = code });
    }
    private void PublishRoster()
    {
        foreach (var member in members.Values.Where(m => m.Joined))
            Send(member, new Message { Type = "roster", Peers = new(roster) });
        Changed?.Invoke();
    }
    private void BroadcastAudio(byte[] payload)
    {
        // Immutable snapshot avoids touching the UI-owned dictionary on the audio thread.
        foreach (var session in sendTargets) session.Send(payload);
    }
    private volatile CallSession[] sendTargets = [], screenTargets = [], cameraTargets = [];
    private void UpdateTargets()
    {
        var connected = members.Values.Where(m => m.Connected).ToArray();
        sendTargets = connected.Select(m => m.Session).ToArray();
        // Legacy peers (no "watch" capability) always receive video, as before 0.7.
        screenTargets = connected.Where(m => !m.Modern || m.Watching.Contains(1)).Select(m => m.Session).ToArray();
        cameraTargets = connected.Where(m => !m.Modern || m.Watching.Contains(2)).Select(m => m.Session).ToArray();
    }
    public void ApplyAudioSettings()
    {
        foreach (var member in members.Values) member.Session.SetVoiceMode(settings.AudioMode);
        if (Audio is not null) Audio.TargetBitrate = settings.AudioMode switch { VoiceMode.Gaming => 16000, VoiceMode.Economy => 8000, _ => 24000 };
        foreach (var pair in sharePlayers) { pair.Value.Deafened = deafened; if (members.TryGetValue(pair.Key, out var member)) pair.Value.Volume = AudioLevels.Gain(settings.Volume) * AudioLevels.Gain(member.ShareVolume); }
        if (Audio is null) return;
        Audio.InputGain = AudioLevels.Gain(settings.MicrophoneGain);
        foreach (var m in members.Values) Audio.Jitter.SetPeerVolume(m.Id, m.VoiceVolume);
        Audio.PushToTalk = settings.PushToTalk; Audio.TalkKey = settings.TalkKey;
        Audio.NoiseGate = settings.NoiseGate; Audio.NoiseSuppression = settings.NoiseSuppression;
        Audio.Jitter.Volume = settings.Volume / 100f;
        Audio.Muted = muted; Audio.Deafened = deafened; Audio.Jitter.Deafened = deafened;
    }
    public void SetMuted(bool microphoneMuted, bool allSoundMuted)
    {
        muted = microphoneMuted; deafened = allSoundMuted; foreach (var player in sharePlayers.Values) player.Deafened = deafened; ApplyAudioSettings();
    }
    private async void ArmTimeout(Member member)
    {
        // async void: any exception here would otherwise crash the UI thread.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45));
            if (disposed || !members.ContainsValue(member) || member.Connected) return;
            Remove(member.Id);
            Status?.Invoke("Не удалось подключить участника. Проверьте коды, брандмауэр или настройте TURN.");
            Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.Error("Ошибка тайм-аута подключения участника", ex); }
    }
    private void Remove(string key)
    {
        if (!members.Remove(key, out var member)) return;
        pendings.Remove(member); if (lastSlot == member) lastSlot = null;
        roster.Remove(key); Audio?.Jitter.RemovePeer(key);
        SetShareStream(member, 0);
        implicitStreams.TryRemove(key + ":1", out _); implicitStreams.TryRemove(key + ":2", out _);
        var hadStreams = member.Streams.Count > 0 || member.Watching.Count > 0;
        ParticipantLeft?.Invoke(key);
        if (member.Connected) PeerDisconnected?.Invoke(key, member.Name);
        UpdateTargets(); member.Session.Dispose();
        if (hadStreams) StreamsChanged?.Invoke();
        if (members.Values.All(m => !m.Connected)) { Audio?.Dispose(); Audio = null; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; sendTargets = screenTargets = cameraTargets = []; foreach (var member in members.Values) SetShareStream(member, 0); foreach (var player in sharePlayers.Values) player.Dispose(); sharePlayers.Clear();
        Audio?.Dispose(); Audio = null;
        foreach (var member in members.Values) member.Session.Dispose();
        members.Clear(); roster.Clear(); pendings.Clear(); lastSlot = null; ownStreams.Clear();
    }
    public bool IsDisposed => disposed;

    private sealed record StreamAnnouncement(byte Kind, int Width, int Height, int Fps);
    private sealed class Member(string id, string name, CallSession session, bool hostConnection)
    {
        public string Id = id, Name = name;
        public readonly CallSession Session = session;
        public readonly bool HostConnection = hostConnection;
        public bool Connected, Joined, Modern; public uint ShareStream; public int VoiceVolume = 100, ShareVolume = 100;
        public long LastChat;
        /// <summary>Our streams this member watches.</summary>
        public readonly HashSet<byte> Watching = [];
        /// <summary>This member's streams that we watch.</summary>
        public readonly HashSet<byte> WatchingTheirs = [];
        public readonly Dictionary<byte, StreamAnnouncement> Streams = [];
    }
    private sealed class Message
    {
        public string? Type { get; set; }
        public string? You { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Name { get; set; }
        public string? Code { get; set; }
        public string? Text { get; set; }
        public string? ChatId { get; set; }
        public byte VideoKind { get; set; }
        public uint VideoSequence { get; set; } public uint AudioStream { get; set; }
        public int Width { get; set; } public int Height { get; set; } public int Fps { get; set; }
        public Dictionary<string, string>? Peers { get; set; }
    }
}
