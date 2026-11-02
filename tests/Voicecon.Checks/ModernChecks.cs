using System.Reflection;
using System.Diagnostics;
using Voicecon;
internal static class ModernChecks
{
    private static void Require(bool condition, string text) { if (!condition) throw new Exception(text); }
    public static async Task Check()
    {
        foreach (var key in new[] { Keys.G, Keys.MButton, Keys.XButton1, Keys.XButton2, Keys.F12, Keys.Control | Keys.K }) Require(HotkeyBox.Valid(key), "Single and mouse binding accepted");
        foreach (var key in new[] { Keys.LButton, Keys.RButton, Keys.ControlKey, Keys.Escape }) Require(!HotkeyBox.Valid(key), "Unsafe binding rejected");
        using (var key = new HotkeyBox { Chord = Keys.G })
        {
            key.BeginCapture(); typeof(HotkeyBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(key, [new KeyEventArgs(Keys.Escape)]);
            Require(key.Chord == Keys.G && !key.Capturing, "Escape restores previous binding");
            key.BeginCapture(); typeof(HotkeyBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(key, [new KeyEventArgs(Keys.H)]);
            Require(key.Chord == Keys.H && !key.Capturing, "Single-key capture");
            key.BeginCapture(); typeof(HotkeyBox).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(key, [new MouseEventArgs(MouseButtons.XButton2, 1, 4, 4, 0)]);
            Require(key.Chord == Keys.XButton2 && HotkeyBox.Format(key.Chord) == "MOUSE5", "Mouse5 capture");
        }
        var a = new LatencyProbe(); var b = new LatencyProbe(); var query = a.Query()!; await Task.Delay(12); b.Handle(query, reply => a.Handle(reply, _ => { })); Require(a.Milliseconds is >= 10 and < 500, "Matching RTT echo");
        var history = new LatencyHistory(); var now = DateTimeOffset.UtcNow; for (var i = 0; i < 300; i++) history.Add(now.AddSeconds(i), [("peer", 20)]); Require(history.Points.Count == 121, "Latency history bounded at 2 minutes");
        using (var graph = new LatencyGraph(new LatencyHistory()) { Size = new Size(600, 300) }) using (var bitmap = new Bitmap(600, 300)) graph.DrawToBitmap(bitmap, new Rectangle(0, 0, 600, 300));
        var payload = new byte[250_000]; Random.Shared.NextBytes(payload); var frame = H264Packet.Wrap(payload, 1, 90, new Size(2560, 1440), true); var assembler = new VideoAssembler(); byte[]? joined = null;
        foreach (var part in VideoAssembler.Split(frame)) joined = assembler.Add(part) ?? joined; Require(joined!.SequenceEqual(frame), "Fragmented native-resolution frame roundtrip");
        var malicious = VideoAssembler.Split(frame).First(); BitConverter.GetBytes(int.MaxValue).CopyTo(malicious, 10); Require(assembler.Add(malicious) is null, "Oversized allocation rejected");
        Console.WriteLine("PASS single keys, MOUSE5 capture, Esc cancel, key restrictions, RTT matching, bounded history, empty graph, bounded video fragmentation");
        foreach (var fps in new[] { 30, 60, 120 })
        {
            using var encoder = new VideoEncoder(); using var decoder = new VideoDecoder(); var decoded = 0; var encoded = 0; string? error = null;
            var size = new Size(320, 180); var raw = new byte[size.Width * size.Height * 4]; for (var i = 0; i < raw.Length; i += 4) { raw[i] = 190; raw[i + 1] = 90; raw[i + 3] = 255; }
            encoder.Failed += message => error = message;
            decoder.Frame += image => { Require(image.Size == size, "Decoded dimensions"); image.Dispose(); Interlocked.Increment(ref decoded); };
            encoder.Encoded += (data, key) => { var packet = H264Packet.Wrap(data, 1, (uint)Interlocked.Increment(ref encoded), size, key); decoder.Submit(packet); };
            await encoder.StartAsync(size, size, fps);
            for (var i = 0; i < fps + 8; i++) { encoder.Submit(raw); await Task.Delay(Math.Max(1, 1000 / fps)); }
            var wait = Stopwatch.StartNew(); while (decoded < 2 && wait.ElapsedMilliseconds < 6000) await Task.Delay(40);
            Require(error is null && encoded >= 5 && decoded >= 2, $"H264 {fps} fps pipeline failed: {encoded} encoded, {decoded} decoded; {error}; decoder {decoder.Error}");
            Console.WriteLine($"PASS synthetic H.264 target {fps}: {encoded} encoded / {decoded} decoded; backend {encoder.Backend}; no capture devices used");
        }
    }
}
