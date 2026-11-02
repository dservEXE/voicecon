using System.Text.Json;
using SIPSorcery.Net;

namespace Voicecon;

/// <summary>Host routes only control messages; every voice stream is direct peer-to-peer.</summary>
public sealed class VoiceRoom : IDisposable
{
    public const int Capacity = 6;
    private readonly Settings settings;
    private readonly SynchronizationContext context;
    private readonly Dictionary<string, Member> members = new();
    private readonly Dictionary<string, string> roster = new();
    private readonly int microphone, speakers;
    private readonly bool localOnly, enableAudio;
    private string id = Guid.NewGuid().ToString("N"), hostId = "";
    private Member? hostLink, pending;
    private bool disposed;
    private bool muted, deafened;
    public bool IsHost { get; private set; }
    public bool HasStarted { get; private set; }
    public AudioEngine? Audio { get; private set; }
    public event Action? Changed;
    public event Action<string>? Status;
    public event Action<string>? Error;
    public event Action<ChatLine>? ChatReceived;
    public event Action<string, string, byte[]>? VideoReceived;
    public event Action<string, byte, uint>? VideoStopped;
    public event Action<string>? ParticipantLeft;
    public event Action<string>? ParticipantJoined;
    public long VideoSentBytes => members.Values.Sum(m => Interlocked.Read(ref m.Session.VideoSentBytes));
    public long VideoReceivedBytes => members.Values.Sum(m => Interlocked.Read(ref m.Session.VideoReceivedBytes));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShareAudioPlayer> sharePlayers = new();
    private uint outgoingShareStream;
    public void StartShareAudio(uint stream) { outgoingShareStream = stream; foreach (var m in members.Values.Where(m => m.Connected)) Send(m, new Message { Type = "share-audio-start", AudioStream = stream }); }
    public void StopShareAudio() { var old = outgoingShareStream; outgoingShareStream = 0; if (old != 0) foreach (var m in members.Values.Where(m => m.Connected)) Send(m, new Message { Type = "share-audio-stop", AudioStream = old }); }
    public void SendShareAudio(byte[] bytes) { foreach (var target in sendTargets) target.SendShareAudio(bytes); }
    private readonly HashSet<string> chatSeen = [];
    public bool CanChat => !disposed && ConnectedCount > 0 && (IsHost || hostLink is { Joined: true });
    public int ConnectedCount => members.Values.Count(m => m.Connected);
    public bool CanInvite => (!HasStarted || IsHost) && pending is null && members.Count < Capacity - 1;
    public bool WaitingForAnswer => pending is not null;
    public IReadOnlyList<string> ParticipantNames => new[] { settings.Name + " (вы)" }
        .Concat(roster.Where(p => p.Key != id).Select(p => p.Value + (members.TryGetValue(p.Key, out var m) && m.Connected ? " · в сети" : " · соединяем…"))).ToList();
    public sealed record ParticipantAudio(string Id, string Name, int VoicePercent, int SharePercent) { public override string ToString() => Name; }
    public IReadOnlyList<ParticipantAudio> Participants => members.Values.Where(m => m.Connected).Select(m => new ParticipantAudio(m.Id, m.Name, m.VoiceVolume, m.ShareVolume)).ToArray();
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

    public async Task<string> CreateInviteAsync()
    {
        if (!CanInvite) throw new InvalidOperationException("Сначала завершите подключение предыдущего участника. В комнате может быть до 6 человек.");
        IsHost = true; HasStarted = true; hostId = id;
        roster[id] = settings.Name;
        var slot = CreateMember(Guid.NewGuid().ToString("N"), "Друг", true);
        pending = slot;
        try { return await slot.Session.CreateInviteAsync(); }
        catch { Remove(slot.Id); pending = null; throw; }
    }

    public async Task<string?> UseCodeAsync(string code)
    {
        var description = InviteCode.Decode(code);
        if (description.type == RTCSdpType.answer)
        {
            if (!IsHost || pending is null) throw new InvalidOperationException("Сначала создайте приглашение, затем вставьте ответ на него.");
            pending.Session.AcceptAnswer(description);
            ArmTimeout(pending);
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
        if (!IsHost || pending is null) return;
        Remove(pending.Id); Changed?.Invoke();
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
            catch (Exception ex) { if (!disposed) Error?.Invoke("Ошибка подключения участника: " + ex.Message); }
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
        member.Session.VideoFrame += bytes => VideoReceived?.Invoke(member.Id, member.Name, bytes);
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
        if (outgoingShareStream != 0) Send(member, new Message { Type = "share-audio-start", AudioStream = outgoingShareStream });
        if (!member.HostConnection) return;
        if (IsHost)
        {
            Send(member, new Message { Type = "welcome", You = member.Id, From = id, Name = settings.Name });
        }
        else Send(member, new Message { Type = "hello", Name = settings.Name });
    }

    private async Task OnMessage(Member source, string json)
    {
        var message = JsonSerializer.Deserialize<Message>(json);
        if (message is null) return;
        switch (message.Type)
        {
            case "share-audio-start" when message.AudioStream != 0:
                SetShareStream(source, message.AudioStream); break;
            case "share-audio-stop" when message.AudioStream == source.ShareStream:
                SetShareStream(source, 0); break;
            case "video-stop" when message.VideoKind is 1 or 2:
                VideoStopped?.Invoke(source.Id, message.VideoKind, message.VideoSequence); break;
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
                Send(source, new Message { Type = "hello", Name = settings.Name });
                break;
            case "hello" when IsHost && source.HostConnection:
                source.Name = CleanName(message.Name); source.Joined = true; roster[source.Id] = source.Name;
                if (pending == source) pending = null;
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
    public void SendVideo(byte[] bytes) { foreach (var target in sendTargets) target.SendVideo(bytes); }
    public bool WantsVideo(byte kind) => sendTargets.Any(target => target.WantsVideo(kind));
    public void StopVideo(byte kind, uint sequence)
    {
        foreach (var member in members.Values.Where(m => m.Connected)) Send(member, new Message { Type = "video-stop", VideoKind = kind, VideoSequence = sequence });
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
        ChatReceived?.Invoke(new ChatLine(chatId, name, text, from == id, DateTimeOffset.Now));
    }
    public sealed record ChatLine(string Id, string Name, string Text, bool IsMine, DateTimeOffset Time);

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
    private volatile CallSession[] sendTargets = [];
    private void UpdateTargets() => sendTargets = members.Values.Where(m => m.Connected).Select(m => m.Session).ToArray();
    public void ApplyAudioSettings()
    {
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
        await Task.Delay(TimeSpan.FromSeconds(45));
        if (disposed || !members.ContainsValue(member) || member.Connected) return;
        Remove(member.Id);
        Status?.Invoke("Не удалось подключить участника. Проверьте коды, брандмауэр или настройте TURN.");
        Changed?.Invoke();
    }
    private void Remove(string key)
    {
        if (!members.Remove(key, out var member)) return;
        if (pending == member) pending = null;
        roster.Remove(key); Audio?.Jitter.RemovePeer(key);
        SetShareStream(member, 0);
        ParticipantLeft?.Invoke(key);
        UpdateTargets(); member.Session.Dispose();
        if (members.Values.All(m => !m.Connected)) { Audio?.Dispose(); Audio = null; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; sendTargets = []; foreach (var member in members.Values) SetShareStream(member, 0); foreach (var player in sharePlayers.Values) player.Dispose(); sharePlayers.Clear();
        Audio?.Dispose(); Audio = null;
        foreach (var member in members.Values) member.Session.Dispose();
        members.Clear(); roster.Clear(); pending = null;
    }
    public bool IsDisposed => disposed;

    private sealed class Member(string id, string name, CallSession session, bool hostConnection)
    {
        public string Id = id, Name = name;
        public readonly CallSession Session = session;
        public readonly bool HostConnection = hostConnection;
        public bool Connected, Joined; public uint ShareStream; public int VoiceVolume = 100, ShareVolume = 100;
        public long LastChat;
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
        public Dictionary<string, string>? Peers { get; set; }
    }
}
