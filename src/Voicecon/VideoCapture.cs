using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Voicecon;

public static class VideoCodec
{
    private static readonly ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    public static byte[] Encode(Bitmap source, byte kind, uint sequence, int maxWidth, int maxHeight)
    {
        var ratio = Math.Min(1, Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        var width = Math.Max(2, (int)(source.Width * ratio)); var height = Math.Max(2, (int)(source.Height * ratio));
        while (true)
        {
            using var frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(frame)) { graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear; graphics.DrawImage(source, 0, 0, width, height); }
            using var output = new MemoryStream();
            using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 48L);
            frame.Save(output, jpegCodec, parameters);
            if (output.Length <= 47_000)
            {
                var packet = new byte[output.Length + 5]; packet[0] = kind; BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), sequence); output.GetBuffer().AsSpan(0, (int)output.Length).CopyTo(packet.AsSpan(5)); return packet;
            }
            width = (int)(width * 0.78); height = (int)(height * 0.78);
            if (width < 160 || height < 90) throw new InvalidOperationException("Не удалось ограничить размер видеокадра.");
        }
    }
    public static uint Sequence(byte[] packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1, 4));
    public static Bitmap Decode(byte[] packet)
    {
        if (packet.Length is < 16 or > 48000 || packet[0] is not (1 or 2) || !ValidJpeg(packet.AsSpan(5))) throw new FormatException("Недопустимый кадр видео.");
        using var stream = new MemoryStream(packet, 5, packet.Length - 5, false); using var image = Image.FromStream(stream, true, true);
        return new Bitmap(image);
    }
    private static bool ValidJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[0] != 0xFF || data[1] != 0xD8) return false;
        var at = 2;
        while (at + 8 < data.Length)
        {
            if (data[at++] != 0xFF) return false; while (at < data.Length && data[at] == 0xFF) at++;
            if (at >= data.Length) return false; var marker = data[at++]; if (marker is 0xD9 or 0xDA) return false;
            if (at + 2 > data.Length) return false; var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at, 2)); if (length < 2 || at + length > data.Length) return false;
            if (marker is 0xC0 or 0xC2)
            {
                if (length < 8) return false; var h = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 3, 2)); var w = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 5, 2)); return w is > 0 and <= 1280 && h is > 0 and <= 720;
            }
            at += length;
        }
        return false;
    }
}

public sealed class VideoCapture : IDisposable
{
    private VideoEncoder? screenEncoder, cameraEncoder;
    private WindowCapture? window;
    private MediaCapture? camera; private MediaFrameReader? reader;
    private int sequence, cameraBusy, screenGeneration, cameraGeneration;
    private long lastCamera;
    private volatile bool screenActive, cameraActive;
    private int fps = 30, resolution = 720;
    public bool ScreenActive => screenActive;
    public bool CameraActive => cameraActive;
    public string Details => string.Join(" · ", new[] { screenEncoder is { } s ? $"экран {s.ActualFps:0} fps / {s.Backend}" : null, cameraEncoder is { } c ? $"камера {c.ActualFps:0} fps / {c.Backend}" : null }.Where(s => s is not null));
    public event Action<byte[]>? Frame;
    public event Action<byte, uint>? Stopped;
    public event Action<string>? Error;
    public Func<byte, bool>? FrameNeeded { get; set; }
    public void SetQuality(string quality) { }
    public void Configure(int frames, int height) { fps = frames is 30 or 60 or 120 ? frames : 30; resolution = height is 720 or 1080 ? height : 0; }
    public async Task StartScreenAsync(ShareSource source)
    {
        StopScreen(); var generation = ++screenGeneration; var encoder = screenEncoder = new VideoEncoder(); screenActive = true;
        try
        {
            var native = source.Size;
            int? desktop = source.MonitorIndex >= 0 && Screen.AllScreens.Length == 1 ? 0 : null;
            if (desktop is null)
            {
                var capture = window = new WindowCapture(); capture.Start(source.Window, fps, source.Window == IntPtr.Zero ? source.MonitorBounds : null); native = capture.NativeSize;
                capture.Frame += bytes => { if (generation == screenGeneration) encoder.Submit(bytes); };
                capture.Closed += message => { if (generation != screenGeneration) return; StopScreen(); Error?.Invoke(message); };
            }
            var size = VideoEncoder.OutputSize(native, resolution);
            encoder.Encoded += (data, key) => { if (screenActive && generation == screenGeneration) Frame?.Invoke(H264Packet.Wrap(data, 1, NextSequence(), size, key)); };
            encoder.Failed += message => { if (generation != screenGeneration) return; StopScreen(); Error?.Invoke("Демонстрация остановлена: " + message); };
            await encoder.StartAsync(native, size, fps, desktop);
            if (generation != screenGeneration) encoder.Dispose();
        }
        catch { StopScreen(); throw; }
    }
    public void StopScreen()
    {
        var active = screenActive; screenActive = false; screenGeneration++; var old = window; window = null; old?.Dispose(); screenEncoder?.Dispose(); screenEncoder = null;
        if (active) Stopped?.Invoke(1, NextSequence());
    }
    public static async Task<IReadOnlyList<MediaFrameSourceGroup>> CamerasAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups.Where(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color && s.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)).ToList();
    }
    public async Task StartCameraAsync(MediaFrameSourceGroup group)
    {
        await StopCameraAsync(); var generation = ++cameraGeneration; var capture = camera = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings { SourceGroup = group, SharingMode = MediaCaptureSharingMode.ExclusiveControl, MemoryPreference = MediaCaptureMemoryPreference.Cpu, StreamingCaptureMode = StreamingCaptureMode.Video });
            if (generation != cameraGeneration) return;
            var source = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord);
            var format = source.SupportedFormats.Where(f => f.FrameRate.Denominator > 0 && f.FrameRate.Numerator / (double)f.FrameRate.Denominator <= fps + 0.5 && (resolution == 0 || f.VideoFormat.Height <= resolution)).OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height).ThenByDescending(f => f.FrameRate.Numerator / (double)f.FrameRate.Denominator).FirstOrDefault();
            if (format is not null) await source.SetFormatAsync(format);
            var native = source.CurrentFormat.VideoFormat; var size = VideoEncoder.OutputSize(new Size((int)native.Width, (int)native.Height), resolution);
            var encoder = cameraEncoder = new VideoEncoder();
            encoder.Encoded += (data, key) => { if (cameraActive && generation == cameraGeneration) Frame?.Invoke(H264Packet.Wrap(data, 2, NextSequence(), size, key)); };
            encoder.Failed += message => { if (generation != cameraGeneration) return; Error?.Invoke("Камера остановлена: " + message); _ = StopCameraAsync(); };
            await encoder.StartAsync(size, size, fps);
            if (generation != cameraGeneration) { encoder.Dispose(); return; }
            reader = await capture.CreateFrameReaderAsync(source, Windows.Media.MediaProperties.MediaEncodingSubtypes.Argb32, new BitmapSize { Width = (uint)size.Width, Height = (uint)size.Height });
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime; reader.FrameArrived += CameraFrame; cameraActive = true;
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("Windows не удалось запустить камеру.");
        }
        catch { await StopCameraAsync(); throw; }
    }
    private void CameraFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!cameraActive || Environment.TickCount64 - lastCamera < 1000.0 / fps || Interlocked.Exchange(ref cameraBusy, 1) != 0) return;
        lastCamera = Environment.TickCount64;
        try
        {
            using var frame = sender.TryAcquireLatestFrame(); using var software = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (software is null) return;
            using var converted = SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(converted.PixelWidth * converted.PixelHeight * 4)); converted.CopyToBuffer(buffer);
            var bytes = new byte[buffer.Length]; using (var data = DataReader.FromBuffer(buffer)) data.ReadBytes(bytes); cameraEncoder?.Submit(bytes);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Error?.Invoke("Камера остановлена: " + ex.Message); _ = StopCameraAsync(); }
        finally { Volatile.Write(ref cameraBusy, 0); }
    }
    public async Task StopCameraAsync()
    {
        var active = cameraActive; cameraActive = false; cameraGeneration++;
        cameraEncoder?.Dispose(); cameraEncoder = null; var old = reader; reader = null;
        if (old is not null) { old.FrameArrived -= CameraFrame; try { await old.StopAsync(); } catch { } old.Dispose(); }
        var oldCapture = camera; camera = null; oldCapture?.Dispose(); if (active) Stopped?.Invoke(2, NextSequence());
    }
    private uint NextSequence() => unchecked((uint)Interlocked.Increment(ref sequence));
    public void Dispose() { StopScreen(); _ = StopCameraAsync(); }
}
