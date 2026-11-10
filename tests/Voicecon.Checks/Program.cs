using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Concentus;
using Concentus.Enums;
using SIPSorcery.Net;
using Voicecon;

internal static class Program
{
    private static int result;
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { result = 1; Console.Error.WriteLine(e.Exception); Application.ExitThread(); };
        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        EventHandler? start = null;
        start = async (_, _) =>
        {
            Application.Idle -= start;
            try
            {
                if (args.Contains("--update-checks")) { await UpdateChecks.Check(); return; }
                if (args.Contains("--session-features")) { SessionFeatureChecks.Check(); return; }
                if (args.Contains("--notification-checks")) { await NotificationChecks.Check(); return; }
                if (args.Contains("--modern-checks")) { await ModernChecks.Check(); return; }
                if (args.Contains("--render-ui")) { UiRenders.Render(); return; }
                if (args.Contains("--ui-benchmark")) { UiPaintChecks.Benchmark(); return; }
                if (args.Contains("--ui-checks")) { UiPaintChecks.CheckRepaints(); return; }
                if (args.Contains("--mailbox-check")) { await FeatureChecks.CheckMailbox(); return; }
                if (args.Contains("--call-flow-check")) { await FeatureChecks.CheckCallFlow(); return; }
                FeatureChecks.CheckIdentityAndVideo();
                if (args.Contains("--resilience")) { ResilienceChecks.Check(); return; }
                if (args.Contains("--network")) { await CheckPublicStun(); return; }
                if (args.Contains("--benchmark")) { await CheckBenchmark(); return; }
                ResilienceChecks.Check(); CheckCodes(); CheckNoise(); CheckJitter();
                await CheckTransport(); await CheckConference();
                Console.WriteLine("ALL CHECKS PASSED");
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { Application.ExitThread(); }
        };
        Application.Idle += start; Application.Run(); return result;
    }
    private static async Task CheckPublicStun()
    {
        using var peer = new CallSession(new Settings());
        var code = await peer.CreateInviteAsync(); var sdp = InviteCode.Decode(code).sdp;
        Assert(sdp.Contains("a=candidate:"), "Invitation needs network candidates");
        Assert(sdp.Contains(" typ srflx"), "Public STUN must discover a server-reflexive route");
        Console.WriteLine($"PASS public STUN discovery; invite length {code.Length} characters; no addresses logged");
    }
    private static async Task CheckBenchmark()
    {
        using var encoder = OpusCodecFactory.CreateEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 24000; encoder.Complexity = 2; encoder.UseInbandFEC = true; encoder.UseDTX = true;
        var filter = new NoiseSuppressor(); using var mixer = new ConferenceMixer();
        var ids = Enumerable.Range(0, 5).Select(i => i.ToString()).ToArray(); foreach (var id in ids) mixer.AddPeer(id);
        var input = new short[320]; var output = new byte[1275]; var pcm = new byte[640];
        using var process = Process.GetCurrentProcess(); var cpuStart = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
        for (var frame = 0; frame < 300; frame++)
        {
            for (var i = 0; i < input.Length; i++) input[i] = (short)(Math.Sin((frame * 320 + i) * 2 * Math.PI * 220 / 16000) * 5000 + Math.Sin((frame * 320 + i) * 2 * Math.PI * 940 / 16000) * 1800);
            filter.Process(input);
            var length = encoder.Encode(input.AsSpan(), 320, output.AsSpan(), output.Length); var packet = output[..length];
            foreach (var id in ids) mixer.Receive(id, (ushort)frame, packet);
            mixer.Read(pcm, 0, pcm.Length);
            var delay = (frame + 1) * 20 - clock.Elapsed.TotalMilliseconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
        }
        process.Refresh(); var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
        Console.WriteLine($"BENCHMARK synthetic 6-person audio: {cpu:0.00}% total CPU; {process.WorkingSet64 / 1048576.0:0.0} MB working set; {clock.Elapsed.TotalSeconds:0.0} s; {Environment.ProcessorCount} logical CPUs. Includes noise suppression, 1 encode, 5 decode/mix; excludes network and hardware capture.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate, string label, int seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException(label); await Task.Delay(40); }
    }
    private static void CheckCodes()
    {
        var description = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVP 111\r\na=fingerprint:sha-256 AB:CD\r\n" };
        var code = InviteCode.Encode(description); var decoded = InviteCode.Decode("\n " + code + "\r\n");
        Assert(decoded.sdp == description.sdp && decoded.type == description.type, "Invite roundtrip");
        foreach (var bad in new[] { "", "VC1.bad", "wrong-code", new string('x', 25000) })
        {
            var rejected = false; try { InviteCode.Decode(bad); } catch (FormatException) { rejected = true; }
            Assert(rejected, "Malformed invite must be rejected");
        }
        using var bytes = new MemoryStream();
        using (var zip = new BrotliStream(bytes, CompressionLevel.SmallestSize, true)) zip.Write(Encoding.UTF8.GetBytes(new string('a', 200_000)));
        var bomb = "VC1." + Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bounded = false; try { InviteCode.Decode(bomb); } catch (FormatException) { bounded = true; }
        Assert(bounded, "Decompression must be bounded"); Console.WriteLine("PASS invite parsing, malformed codes, decompression limit");
    }
    private static void CheckNoise()
    {
        var filter = new NoiseSuppressor(); var random = new Random(42); var frame = new short[320];
        double before = 0, after = 0, voiceBefore = 0, voiceAfter = 0;
        var watch = Stopwatch.StartNew();
        for (var f = 0; f < 400; f++)
        {
            for (var i = 0; i < frame.Length; i++) frame[i] = (short)((random.NextDouble() * 2 - 1) * 240 + (f >= 300 ? Math.Sin((f * 320 + i) * Math.PI * 2 * 1000 / 16000) * 6000 : 0));
            if (f is >= 200 and < 300) before += frame.Sum(x => (double)x * x);
            if (f >= 320) voiceBefore += frame.Sum(x => (double)x * x);
            filter.Process(frame);
            if (f is >= 200 and < 300) after += frame.Sum(x => (double)x * x);
            if (f >= 320) voiceAfter += frame.Sum(x => (double)x * x);
        }
        var reduction = 10 * Math.Log10(before / after); var retention = Math.Sqrt(voiceAfter / voiceBefore);
        Console.WriteLine($"Noise diagnostic: reduction {reduction:F2} dB, retention {retention:F3}");
        Assert(reduction > 5, "Stationary background should be reduced by >5 dB"); Assert(retention > 0.7, "Speech-band tone should be retained");
        Console.WriteLine($"PASS noise: {reduction:0.0} dB reduction; {retention:P0} tone retention; {watch.ElapsedMilliseconds} ms for 8 s audio");
    }
    private static byte[] Packet(IOpusEncoder encoder, int phase)
    {
        var pcm = new short[320]; for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin((phase * 320 + i) * Math.PI * 2 * 700 / 16000) * 6000);
        var output = new byte[1275]; var length = encoder.Encode(pcm.AsSpan(), 320, output.AsSpan(), output.Length); return output[..length];
    }
    private static void CheckJitter()
    {
        using var encoder = OpusCodecFactory.CreateEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 24000; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 10;
        using var jitter = new VoiceJitterBuffer();
        jitter.Add(65535, Packet(encoder, 1)); jitter.Add(65534, Packet(encoder, 0)); jitter.Add(0, Packet(encoder, 2));
        var pcm = new byte[640]; jitter.Read(pcm, 0, pcm.Length); jitter.Read(pcm, 0, pcm.Length); jitter.Read(pcm, 0, pcm.Length);
        Assert(jitter.Lost == 0, "Sequence rollover/reordering must not lose packets");
        jitter.Add(2, Packet(encoder, 4)); jitter.Read(pcm, 0, pcm.Length);
        Assert(jitter.Lost == 1 && jitter.Recovered == 1, "Gap should invoke FEC");
        jitter.Read(pcm, 0, pcm.Length); Assert(pcm.Any(b => b != 0), "Decoded audio should be nonzero");
        jitter.Deafened = true; jitter.Add(3, Packet(encoder, 5)); jitter.Read(pcm, 0, pcm.Length);
        Assert(pcm.All(b => b == 0), "Deafen must silence output");
        for (ushort i = 4; i < 4000; i++) jitter.Add(i, Packet(encoder, i));
        jitter.Read(pcm, 0, pcm.Length);
        Console.WriteLine("PASS Opus, rollover, reordering, FEC, deafen, bounded queue");
    }
    private static async Task CheckTransport()
    {
        using var a = new CallSession(new Settings(), true); using var b = new CallSession(new Settings(), true);
        var receivedA = 0; var receivedB = 0; var data = "";
        a.PacketReceived += (_, _) => Interlocked.Increment(ref receivedA); b.PacketReceived += (_, _) => Interlocked.Increment(ref receivedB);
        b.ControlMessage += text => data = text;
        var offer = await a.CreateInviteAsync(); var answer = await b.AnswerAsync(InviteCode.Decode(offer)); a.AcceptAnswer(InviteCode.Decode(answer));
        await Until(() => a.State == RTCPeerConnectionState.connected && b.State == RTCPeerConnectionState.connected, "Encrypted transport connection");
        for (var i = 0; i < 10; i++) { a.Send([0xF8, 0xFF, 0xFE]); b.Send([0xF8, 0xFF, 0xFE]); a.SendControl("hello"); await Task.Delay(30); }
        await Until(() => receivedA > 0 && receivedB > 0 && data == "hello", "Bidirectional encrypted media + data channel");
        a.ProbeLatency(); b.ProbeLatency(); await Until(() => a.Latency.Milliseconds.HasValue && b.Latency.Milliseconds.HasValue, "RTT over real encrypted channel");
        byte[]? video = null; byte[]? sound = null; b.VideoFrame += packet => video = packet; b.ShareAudioReceived += packet => sound = packet;
        var fake = new byte[180000]; Random.Shared.NextBytes(fake); var wrapped = H264Packet.Wrap(fake, 1, 123, new Size(2560, 1440), true);
        for (var i = 0; i < 20 && video is null; i++) { a.SendVideo(wrapped); a.SendShareAudio([1, 0, 0, 0, 5, 0, 0xF8, 0xFF, 0xFE]); await Task.Delay(100); }
        await Until(() => video is not null && sound is not null, "Fragmented video + separate share-audio channel"); Assert(video!.SequenceEqual(wrapped) && sound!.Length == 9, "Exact fragmented transport");
        Console.WriteLine("PASS encrypted RTT, 180 KB native-resolution fragmented video, independent screen-audio channel; no playback or capture");        Console.WriteLine($"PASS native WebRTC DTLS/SRTP: received {receivedA}/{receivedB} packets + control channel");
    }
    private static async Task CheckConference()
    {
        using var host = new VoiceRoom(new Settings { Name = "Host" }, -1, -1, true, false);
        using var a = new VoiceRoom(new Settings { Name = "Alice" }, -1, -1, true, false);
        using var b = new VoiceRoom(new Settings { Name = "Bob" }, -1, -1, true, false);
        var errors = new List<string>(); foreach (var room in new[] { host, a, b }) room.Error += errors.Add;
        await host.CreateInviteAsync(); host.CancelInvite();
        Assert(host.CanInvite && !host.WaitingForAnswer, "Cancelled invite releases the slot");
        var invite = await host.CreateInviteAsync(); var answer = await a.UseCodeAsync(invite); await host.UseCodeAsync(answer!);
        await Until(() => host.ConnectedCount == 1 && a.ConnectedCount == 1 && host.CanInvite, "First conference guest");
        invite = await host.CreateInviteAsync(); answer = await b.UseCodeAsync(invite); await host.UseCodeAsync(answer!);
        await Until(() => host.ConnectedCount == 2 && a.ConnectedCount == 2 && b.ConnectedCount == 2, "3-person automatic mesh", 35);
        Assert(errors.Count == 0, string.Join("\n", errors));
        Assert(a.ParticipantNames.Count == 3 && b.ParticipantNames.Count == 3, "Conference roster");
        var chats = new List<VoiceRoom.ChatLine>[] { [], [], [] };
        host.ChatReceived += chats[0].Add; a.ChatReceived += chats[1].Add; b.ChatReceived += chats[2].Add;
        a.SendChat("Привет 😎 🎮");
        await Until(() => chats.All(c => c.Count == 1), "Encrypted chat reaches all participants");
        Assert(chats[1][0].IsMine && !chats[0][0].IsMine && chats[2][0].Name == "Alice" && chats.All(c => c[0].Text == "Привет 😎 🎮"), "Chat attribution and emoji");
        host.SendChat("Команда готова ✅"); await Until(() => chats.All(c => c.Count == 2), "Host chat");
        Console.WriteLine("PASS encrypted group chat, emoji, sender attribution, own-message echo");
        Console.WriteLine("PASS 3-person room: all 3 peer pairs connected automatically, roster propagated");
        using var c = new VoiceRoom(new Settings { Name = "Chris" }, -1, -1, true, false);
        using var d = new VoiceRoom(new Settings { Name = "Dana" }, -1, -1, true, false);
        using var e = new VoiceRoom(new Settings { Name = "Evan" }, -1, -1, true, false);
        foreach (var guest in new[] { c, d, e })
        {
            guest.Error += errors.Add;
            invite = await host.CreateInviteAsync(); answer = await guest.UseCodeAsync(invite); await host.UseCodeAsync(answer!);
            await Until(() => host.CanInvite || host.ConnectedCount == 5, "Host accepts next guest");
        }
        await Until(() => new[] { host, a, b, c, d, e }.All(r => r.ConnectedCount == 5), "6-person full mesh", 45);
        Assert(errors.Count == 0, string.Join("\n", errors)); Assert(!host.CanInvite, "Room capacity is enforced");
        Console.WriteLine("PASS 6-person room: all 15 peer pairs connected; capacity enforced");
        var adjusted = host.Participants.First(); host.SetParticipantVolume(adjusted.Id, 250, 35);
        Assert(host.Participants.First(p => p.Id == adjusted.Id) is { VoicePercent: 250, SharePercent: 35 } && a.Participants.All(p => p.VoicePercent == 100 && p.SharePercent == 100), "Participant settings are local and independent");
        host.SetParticipantVolume(adjusted.Id, 900, -5); Assert(host.Participants.First(p => p.Id == adjusted.Id) is { VoicePercent: 300, SharePercent: 0 }, "Participant bounds");
        Console.WriteLine("PASS independent participant voice/share volume, 0–300% bounds, no changes to other callers");
        var videoSeen = new HashSet<string>();
        foreach (var pair in new[] { (a, "a"), (b, "b"), (c, "c"), (d, "d"), (e, "e") }) pair.Item1.VideoReceived += (_, _, bytes) => { if (bytes.Length > 6) { lock (videoSeen) videoSeen.Add(pair.Item2); } };
        using (var bitmap = new Bitmap(160, 90))
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue);
            var packet = VideoCodec.Encode(bitmap, 1, 42, 640, 360);
            for (var repeat = 0; repeat < 20 && videoSeen.Count < 5; repeat++) { host.SendVideo(packet); await Task.Delay(100); }
            await Until(() => videoSeen.Count == 5, "Encrypted video reaches all 5 peers");
        }
        var stopped = 0; a.VideoStopped += (_, kind, sequence) => { if (kind == 1 && sequence == 43) stopped++; }; host.StopVideo(1, 43);
        await Until(() => stopped == 1, "Reliable video stop signal");
        Console.WriteLine("PASS binary video channel: synthetic frame delivered to 5 peers; reliable stop signal; no desktop/camera captured");
        b.Dispose();
        await Until(() => host.ConnectedCount == 4 && a.ConnectedCount == 4, "Conference guest departure", 35);
        Assert(host.CanInvite, "Host can reinvite after departure");
        host.Dispose(); await Until(() => a.IsDisposed, "Host departure ends room", 35);
        Console.WriteLine("PASS guest departure, room remains usable, host departure closes room");
    }
}
