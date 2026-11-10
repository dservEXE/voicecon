using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace Voicecon;

public sealed class AudioEngine : IDisposable
{
    private readonly WaveInEvent capture;
    private readonly WaveOutEvent playback;
    private readonly IOpusEncoder encoder;
    private readonly Action<byte[]> send;
    private readonly byte[] encoded = new byte[1275];
    private readonly short[] frame = new short[VoiceJitterBuffer.FrameSamples];
    private readonly short[] silence = new short[VoiceJitterBuffer.FrameSamples];
    private int fill, gateHangover;
    private volatile bool stopped;
    private int appliedBitrate = 24000;
    private readonly object encodeLock = new();
    private readonly NoiseSuppressor suppressor = new();
    private readonly InputGainProcessor inputGain = new();
    public volatile float InputGain = 1;
    public ConferenceMixer Jitter { get; } = new();
    public volatile bool Muted, Deafened, PushToTalk, NoiseGate = true;
    public volatile bool NoiseSuppression = true;
    public volatile int TargetBitrate = 24000;
    public int TalkKey = 0x56;
    public float InputLevel;
    public bool Transmitting;
    public event Action<string>? Error;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    public AudioEngine(int microphone, int speakers, Action<byte[]> send)
    {
        this.send = send;
        encoder = OpusCodecFactory.CreateEncoder(VoiceJitterBuffer.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 24000; encoder.Complexity = 2;
        encoder.UseDTX = true; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 5;
        capture = new WaveInEvent
        {
            DeviceNumber = microphone, WaveFormat = new WaveFormat(VoiceJitterBuffer.SampleRate, 16, 1),
            BufferMilliseconds = 20, NumberOfBuffers = 3
        };
        playback = new WaveOutEvent { DeviceNumber = speakers, DesiredLatency = 60, NumberOfBuffers = 3 };
        capture.DataAvailable += OnCapture;
        capture.RecordingStopped += (_, e) => { if (!stopped && e.Exception is not null) Error?.Invoke("Микрофон отключён: " + e.Exception.Message); };
        playback.PlaybackStopped += (_, e) => { if (!stopped && e.Exception is not null) Error?.Invoke("Наушники отключены: " + e.Exception.Message); };
    }

    public void Start()
    {
        playback.Init(Jitter); playback.Play(); capture.StartRecording();
    }

    private void OnCapture(object? sender, WaveInEventArgs e)
    {
        lock (encodeLock)
        {
            if (stopped) return;
            try
            {
                var samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                foreach (var sample in samples)
                {
                    frame[fill++] = sample;
                    if (fill == frame.Length) { EncodeFrame(); fill = 0; }
                }
            }
            catch (Exception ex) { stopped = true; Error?.Invoke("Ошибка звука: " + ex.Message); }
        }
    }

    private void EncodeFrame()
    {
        // Keep encoder changes on the capture thread; changing Opus bitrate does not alter frame duration.
        var targetBitrate = TargetBitrate;
        if (appliedBitrate != targetBitrate)
        {
            encoder.Bitrate = targetBitrate;
            appliedBitrate = targetBitrate;
        }
        if (NoiseSuppression) suppressor.Process(frame);
        inputGain.Process(frame, InputGain);
        double energy = 0;
        foreach (var sample in frame) energy += (double)sample * sample;
        InputLevel = (float)Math.Sqrt(energy / frame.Length) / 32768;
        if (InputLevel >= 0.004f) gateHangover = 12;
        else if (gateHangover > 0) gateHangover--;
        var keyDown = !PushToTalk || (GetAsyncKeyState(TalkKey) & 0x8000) != 0;
        Transmitting = !Muted && !Deafened && keyDown && (!NoiseGate || gateHangover > 0);
        // Encoding zero PCM lets Opus enter DTX without leaking the previous voice frame.
        var size = encoder.Encode((Transmitting ? frame : silence).AsSpan(), frame.Length, encoded.AsSpan(), encoded.Length);
        send(encoded.AsSpan(0, size).ToArray());
    }

    public void Dispose()
    {
        stopped = true;
        capture.DataAvailable -= OnCapture;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
        try { playback.Stop(); } catch { }
        playback.Dispose();
        lock (encodeLock) encoder.Dispose();
        Jitter.Dispose();
    }
}
