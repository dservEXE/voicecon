using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
namespace Voicecon;
public static class H264Packet
{
    public const int Header = 14, Limit = 2 * 1024 * 1024;
    public static bool IsH264(byte[] p) => p.Length > Header && p[5] == (byte)'H' && p[6] == (byte)'2' && p[7] == (byte)'6' && p[8] == (byte)'4';
    public static bool Valid(byte[] p) => IsH264(p) && p.Length <= Limit && p[0] is 1 or 2 && p[13] <= 1 && Width(p) is >= 2 and <= 7680 && Height(p) is >= 2 and <= 4320 && (long)Width(p) * Height(p) <= 33_177_600;
    public static int Width(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(9));
    public static int Height(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(11));
    public static byte[] Wrap(byte[] data, byte kind, uint sequence, Size size, bool key)
    {
        var p = new byte[data.Length + Header]; p[0] = kind; BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), sequence); "H264"u8.CopyTo(p.AsSpan(5));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), (ushort)size.Width); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), (ushort)size.Height); p[13] = key ? (byte)1 : (byte)0; data.CopyTo(p, Header); return p;
    }
}
// Splits Annex B access units at encoder-inserted AUD NALs. Bounded even for a malformed helper stream.
public sealed class AnnexBFrames(Action<byte[], bool> frame)
{
    private readonly List<byte> buffer = []; private int scan;
    public void Add(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) buffer.Add(b);
        for (var i = Math.Max(0, scan - 5); i + 5 < buffer.Count; i++)
        {
            var prefix = buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 1 ? 3 : buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 0 && buffer[i + 3] == 1 ? 4 : 0;
            if (prefix == 0 || (buffer[i + prefix] & 31) != 9) continue;
            if (i > 0) { Emit(buffer.GetRange(0, i).ToArray()); buffer.RemoveRange(0, i); i = 0; }
            i += prefix;
        }
        scan = buffer.Count;
        if (buffer.Count > H264Packet.Limit) throw new IOException("Слишком большой видеокадр.");
    }
    private void Emit(byte[] data)
    {
        var key = false; var picture = false;
        for (var i = 0; i + 4 < data.Length; i++) if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) { var type = data[i + 3] & 31; key |= type == 5; picture |= type is 1 or 5; }
        if (picture) frame(data, key);
    }
    public void Complete() { if (buffer.Count > 0) Emit(buffer.ToArray()); buffer.Clear(); scan = 0; }
}
public sealed class VideoEncoder : IDisposable
{
    private Process? process;
    private readonly Channel<byte[]> raw = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private int frames; private long since = Stopwatch.GetTimestamp();
    public double ActualFps { get; private set; }
    public string Backend { get; private set; } = "";
    public event Action<byte[], bool>? Encoded;
    public event Action<string>? Failed;
    public static Size OutputSize(Size source, int height)
    {
        var ratio = height <= 0 ? 1 : Math.Min(1, height / (double)source.Height);
        return new Size(Math.Max(2, (int)(source.Width * ratio) / 2 * 2), Math.Max(2, (int)(source.Height * ratio) / 2 * 2));
    }
    public static int DefaultBitrateKbps(Size output) => output.Height <= 720 ? 3000 : output.Height <= 1080 ? 6000 : 10000;
    public async Task StartAsync(Size source, Size output, int fps, int? desktopOutput = null, int? bitrateKbps = null)
    {
        var bitrate = Math.Clamp(bitrateKbps is > 0 ? bitrateKbps.Value : DefaultBitrateKbps(output), 300, 50000) * 1000;
        Exception? last = null;
        foreach (var encoder in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libopenh264" })
        {
            try
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
                if (desktopOutput.HasValue && encoder == "h264_nvenc" && source == output)
                    args.AddRange(["-f", "lavfi", "-i", $"ddagrab=output_idx={desktopOutput}:framerate={fps}:dup_frames=0"]);
                else if (desktopOutput.HasValue)
                    args.AddRange(["-f", "lavfi", "-i", $"ddagrab=output_idx={desktopOutput}:framerate={fps}:dup_frames=0,hwdownload,format=bgra", "-vf", $"scale={output.Width}:{output.Height}:flags=fast_bilinear,format=yuv420p"]);
                else args.AddRange(["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{source.Width}x{source.Height}", "-framerate", fps.ToString(), "-i", "pipe:0", "-vf", $"scale={output.Width}:{output.Height}:flags=fast_bilinear,format=yuv420p"]);
                args.AddRange(["-an", "-c:v", encoder, "-g", fps.ToString(), "-bf", "0", "-b:v", bitrate.ToString(), "-maxrate", bitrate.ToString(), "-bufsize", (bitrate / 2).ToString(), "-threads", "2"]);
                if (encoder == "h264_nvenc") args.AddRange(["-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1", "-forced-idr", "1"]);
                if (encoder == "h264_amf") args.AddRange(["-usage", "ultralowlatency"]);
                if (encoder == "libopenh264") args.AddRange(["-rc_mode", "bitrate", "-allow_skip_frames", "1"]);
                args.AddRange(["-bsf:v", "h264_metadata=aud=insert", "-flush_packets", "1", "-f", "h264", "pipe:1"]);
                process = MediaRuntime.Start(args, !desktopOutput.HasValue);
                var errors = process.StandardError.ReadToEndAsync();
                // Raw-input encoders need one synthetic black frame to initialise hardware; never a screen capture.
                if (!desktopOutput.HasValue) await process.StandardInput.BaseStream.WriteAsync(new byte[source.Width * source.Height * 4], stop.Token);
                await Task.Delay(180, stop.Token);
                if (process.HasExited) throw new IOException((await errors).Trim());
                Backend = encoder.Replace("h264_", "").Replace("libopenh264", "CPU");
                var p = process;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var parser = new AnnexBFrames((data, key) => { frames++; var seconds = Stopwatch.GetElapsedTime(since).TotalSeconds; if (seconds >= 1) { ActualFps = frames / seconds; frames = 0; since = Stopwatch.GetTimestamp(); } Encoded?.Invoke(data, key); });
                        var chunk = new byte[64 * 1024]; int read;
                        while ((read = await p.StandardOutput.BaseStream.ReadAsync(chunk, stop.Token)) > 0) parser.Add(chunk.AsSpan(0, read));
                        parser.Complete(); if (!stop.IsCancellationRequested) Failed?.Invoke("Видеокодировщик остановился: " + (await errors).Trim());
                    }
                    catch (Exception ex) { if (!stop.IsCancellationRequested) Failed?.Invoke(ex.Message); }
                });
                if (!desktopOutput.HasValue) _ = Task.Run(async () => { try { await foreach (var frame in raw.Reader.ReadAllAsync(stop.Token)) await p.StandardInput.BaseStream.WriteAsync(frame, stop.Token); } catch (Exception ex) { if (!stop.IsCancellationRequested) Failed?.Invoke(ex.Message); } });
                return;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { last = ex; MediaRuntime.Stop(process); process = null; }
        }
        throw new IOException("Не удалось запустить H.264: " + last?.Message);
    }
    public void Submit(byte[] bgra) { if (!stop.IsCancellationRequested) raw.Writer.TryWrite(bgra); }
    public void Dispose() { if (stop.IsCancellationRequested) return; stop.Cancel(); raw.Writer.TryComplete(); MediaRuntime.Stop(process); process = null; }
}
public sealed class VideoDecoder : IDisposable
{
    private Process? process; private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private bool waitingKey = true; private Size size; private readonly object gate = new();
    public event Action<Bitmap>? Frame; public string Error { get; private set; } = "";
    public void Submit(byte[] packet)
    {
        if (!H264Packet.Valid(packet) || stop.IsCancellationRequested) return;
        lock (gate)
        {
            var key = packet[13] == 1;
            if (waitingKey && !key) return;
            if (process is null)
            {
                size = new Size(H264Packet.Width(packet), H264Packet.Height(packet));
                var p = process = MediaRuntime.Start(["-hide_banner", "-loglevel", "error", "-avioflags", "direct", "-probesize", "32", "-analyzeduration", "0", "-flags", "low_delay", "-threads", "2", "-f", "h264", "-i", "pipe:0", "-an", "-threads", "2", "-f", "rawvideo", "-pix_fmt", "bgra", "-fps_mode", "passthrough", "pipe:1"], true);
                _ = p.StandardError.ReadToEndAsync().ContinueWith(t => Error = t.Result);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var bytes = new byte[checked(size.Width * size.Height * 4)];
                        while (!stop.IsCancellationRequested)
                        {
                            await p.StandardOutput.BaseStream.ReadExactlyAsync(bytes, stop.Token);
                            var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
                            var bits = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, image.PixelFormat);
                            try { Marshal.Copy(bytes, 0, bits.Scan0, bytes.Length); } finally { image.UnlockBits(bits); }
                            if (Frame is { } handler) handler(image); else image.Dispose();
                        }
                    }
                    catch (Exception) { }
                });
                _ = Task.Run(async () => { try { await foreach (var bytes in packets.Reader.ReadAllAsync(stop.Token)) await p.StandardInput.BaseStream.WriteAsync(bytes, stop.Token); } catch (Exception) { } });
            }
            if (size.Width != H264Packet.Width(packet) || size.Height != H264Packet.Height(packet)) return;
            waitingKey = false;
            if (!packets.Writer.TryWrite(packet[H264Packet.Header..])) waitingKey = true;
        }
    }
    public void Dispose() { lock (gate) { if (stop.IsCancellationRequested) return; stop.Cancel(); packets.Writer.TryComplete(); MediaRuntime.Stop(process); process = null; } }
}
