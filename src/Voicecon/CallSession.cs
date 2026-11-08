using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Text;

namespace Voicecon;

public sealed class CallSession : IDisposable
{
    private readonly RTCPeerConnection peer;
    private readonly CancellationTokenSource lifetime = new();
    private RTCDataChannel? control; public LatencyProbe Latency { get; } = new(); public void ProbeLatency() { if (State == RTCPeerConnectionState.connected && control?.readyState == RTCDataChannelState.open && Latency.Query() is { } query) SendControl(query); }
    private RTCDataChannel? video, shareAudio; public event Action<byte[]>? ShareAudioReceived;
    private readonly object videoLock = new();
    private readonly Dictionary<byte, uint> pendingVideo = [];
    public event Action<byte[]>? VideoFrame;
    public long VideoSentBytes, VideoReceivedBytes;
    private volatile bool disposed;
    private int controlOpened;
    private long sentBytes, receivedBytes;
    public event Action<string>? Status;
    public event Action<RTCPeerConnectionState>? StateChanged;
    public event Action? ControlReady;
    public event Action<string>? ControlMessage;
    public RTCPeerConnectionState State => peer.connectionState;
    public string? ConnectionRoute
    {
        get
        {
            if (disposed || State != RTCPeerConnectionState.connected) return null;
            try
            {
                var pair = peer.GetRtpChannel().NominatedEntry;
                if (pair is null || !pair.Nominated) return null;
                return pair.LocalCandidate.type == RTCIceCandidateType.relay || pair.RemoteCandidate.type == RTCIceCandidateType.relay
                    ? "TURN" : "P2P";
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return null; }
        }
    }
    public bool IsOffer { get; private set; }
    public long SentBytes => Interlocked.Read(ref sentBytes);
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);

    public CallSession(Settings settings, bool localOnly = false)
    {
        var route = localOnly ? "auto" : settings.ConnectionRoute;
        var useTurn = route is "auto" or "relay";
        if (!localOnly && route == "relay" && string.IsNullOrWhiteSpace(settings.TurnUrl))
            throw new InvalidOperationException("Для маршрута через TURN укажите сервер в настройках сети.");
        var servers = new List<RTCIceServer>();
        if (!localOnly && !string.IsNullOrWhiteSpace(settings.StunUrl)) servers.Add(new() { urls = settings.StunUrl.Trim() });
        if (!localOnly && useTurn && !string.IsNullOrWhiteSpace(settings.TurnUrl)) servers.Add(new()
        {
            urls = settings.TurnUrl.Trim(), username = settings.TurnUser, credential = settings.TurnPassword
        });
        peer = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = servers,
            iceTransportPolicy = route == "relay" && !localOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
        });
        peer.ondatachannel += channel => { if (channel.label == "voicecon-video") AttachVideo(channel); else if (channel.label == "voicecon-share-audio") AttachShareAudio(channel); else AttachControl(channel); };
        peer.addTrack(new MediaStreamTrack(new List<AudioFormat>
        {
            new(111, "opus", 48000, 2, "minptime=10;useinbandfec=1;usedtx=1;stereo=0;sprop-stereo=0;maxaveragebitrate=24000;maxplaybackrate=16000")
        }, MediaStreamStatusEnum.SendRecv));
        peer.onconnectionstatechange += state => StateChanged?.Invoke(state);
        peer.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            Interlocked.Add(ref receivedBytes, packet.Payload.Length);
            PacketReceived?.Invoke(packet.Header.SequenceNumber, packet.Payload);
        };
    }

    // Also used by the device-free encrypted transport check.
    public event Action<ushort, byte[]>? PacketReceived;
    public void Send(byte[] payload)
    {
        if (disposed || State != RTCPeerConnectionState.connected) return;
        peer.SendAudio(960, payload); // Opus RTP always uses a 48 kHz clock: 20 ms = 960.
        Interlocked.Add(ref sentBytes, payload.Length);
    }

    public async Task<string> CreateInviteAsync()
    {
        IsOffer = true;
        AttachControl(await peer.createDataChannel("voicecon-control"));
        AttachVideo(await peer.createDataChannel("voicecon-video", new RTCDataChannelInit { ordered = true }));
        AttachShareAudio(await peer.createDataChannel("voicecon-share-audio", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 }));
        await peer.setLocalDescription(peer.createOffer());
        await GatherAsync();
        return InviteCode.Encode(peer.createOffer());
    }
    private readonly VideoAssembler assembler = new();
    private readonly Dictionary<uint, (int Size, long Time)> videoFlight = [];
    private readonly HashSet<byte> needsKey = [];
    private void AttachShareAudio(RTCDataChannel channel)
    {
        shareAudio = channel; channel.onmessage += (_, _, bytes) => { if (!disposed && bytes.Length is >= 7 and <= 1281) { Interlocked.Add(ref VideoReceivedBytes, bytes.Length); ShareAudioReceived?.Invoke(bytes); } };
    }
    public void SendShareAudio(byte[] bytes)
    {
        if (disposed || bytes.Length is < 7 or > 1281 || shareAudio?.readyState != RTCDataChannelState.open || shareAudio.bufferedAmount > 1_000_000) return;
        try { shareAudio.send(bytes); Interlocked.Add(ref VideoSentBytes, bytes.Length); } catch (Exception ex) when (ex is InvalidOperationException or ApplicationException or System.Net.Sockets.SocketException) { }
    }
    private void AttachVideo(RTCDataChannel channel)
    {
        video = channel;
        channel.onmessage += (_, _, bytes) =>
        {
            if (bytes.Length == 6 && bytes[0] == 0x80)
            { lock (videoLock) { pendingVideo.Remove(bytes[1]); videoFlight.Remove(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(2))); } return; }
            if (bytes.Length > 0 && bytes[0] == 0x81) { lock (assembler) { var complete = assembler.Add(bytes); if (complete is null) return; bytes = complete; } }
            if (bytes.Length < 6 || bytes[0] is not (1 or 2) || (bytes.Length > 48000 && !H264Packet.Valid(bytes))) return;
            var reply = new byte[6]; reply[0] = 0x80; reply[1] = bytes[0]; bytes.AsSpan(1, 4).CopyTo(reply.AsSpan(2));
            try { if (!disposed && channel.readyState == RTCDataChannelState.open) channel.send(reply); } catch (InvalidOperationException) { }
            Interlocked.Add(ref VideoReceivedBytes, bytes.Length); VideoFrame?.Invoke(bytes);
        };
    }
    public bool SendVideo(byte[] bytes)
    {
        if (disposed || bytes.Length < 6 || bytes.Length > H264Packet.Limit || video?.readyState != RTCDataChannelState.open) return false;
        lock (videoLock)
        {
            var h264 = H264Packet.Valid(bytes); var key = h264 && bytes[13] == 1;
            var now = Environment.TickCount64;
            foreach (var expired in videoFlight.Where(p => now - p.Value.Time > 5000).Select(p => p.Key).ToArray()) videoFlight.Remove(expired);
            if (video.bufferedAmount > 512000 || videoFlight.Values.Sum(v => v.Size) > 1_500_000 || (!h264 && pendingVideo.ContainsKey(bytes[0]))) { if (h264) needsKey.Add(bytes[0]); return false; }
            if (h264 && needsKey.Contains(bytes[0]) && !key) return false;
            if (key) needsKey.Remove(bytes[0]);
            var sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));
            try
            {
                if (h264) { videoFlight[sequence] = (bytes.Length, now); foreach (var part in VideoAssembler.Split(bytes)) video.send(part); }
                else { pendingVideo[bytes[0]] = sequence; video.send(bytes); }
                Interlocked.Add(ref VideoSentBytes, bytes.Length); return true;
            }
            catch (Exception ex) when (ex is ApplicationException or InvalidOperationException or System.Net.Sockets.SocketException) { pendingVideo.Remove(bytes[0]); videoFlight.Remove(sequence); if (h264) needsKey.Add(bytes[0]); return false; }
        }
    }
    public bool WantsVideo(byte kind) => !disposed && video?.readyState == RTCDataChannelState.open;
    private void AttachControl(RTCDataChannel channel)
    {
        if (channel.label != "voicecon-control") return;
        control = channel;
        void Ready() { if (Interlocked.Exchange(ref controlOpened, 1) == 0) ControlReady?.Invoke(); }
        channel.onopen += Ready;
        channel.onmessage += (_, _, data) =>
        {
            if (data.Length <= 100_000) { var text = Encoding.UTF8.GetString(data); if (!Latency.Handle(text, SendControl)) ControlMessage?.Invoke(text); }
        };
        if (channel.readyState == RTCDataChannelState.open) Ready();
    }

    public void SendControl(string message)
    {
        if (!disposed && control?.readyState == RTCDataChannelState.open) control.send(message);
    }

    public async Task<string> AnswerAsync(RTCSessionDescriptionInit offer)
    {
        if (offer.type != RTCSdpType.offer) throw new InvalidOperationException("Нужно приглашение друга, а не ответ.");
        SetRemote(offer);
        await peer.setLocalDescription(peer.createAnswer());
        await GatherAsync();
        return InviteCode.Encode(peer.createAnswer());
    }

    public void AcceptAnswer(RTCSessionDescriptionInit answer)
    {
        if (!IsOffer || answer.type != RTCSdpType.answer) throw new InvalidOperationException("Вставьте ответ друга на ваше приглашение.");
        SetRemote(answer);
    }

    private void SetRemote(RTCSessionDescriptionInit description)
    {
        var result = peer.setRemoteDescription(description);
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException("Не удалось согласовать соединение: " + result);
    }

    private async Task GatherAsync()
    {
        Status?.Invoke("Ищем сетевые маршруты…");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(RTCIceGatheringState state) { if (state == RTCIceGatheringState.complete) completion.TrySetResult(); }
        peer.onicegatheringstatechange += Changed;
        try
        {
            Changed(peer.iceGatheringState);
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("Сбор сетевых маршрутов занял слишком долго. Проверьте интернет и адрес STUN в настройках сети.");
            }
        }
        finally { peer.onicegatheringstatechange -= Changed; }
        lifetime.Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        peer.Close("Звонок завершён"); peer.Dispose();
        lifetime.Dispose();
    }
}

