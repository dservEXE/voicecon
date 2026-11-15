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
    private readonly short[] frame = new short[VoiceJitterBuffer.MaxFrameSamples];
    private readonly short[] silence = new short[VoiceJitterBuffer.MaxFrameSamples];
    private int fill, gateHangover, frameLength = VoiceJitterBuffer.FrameSamples, appliedComplexity = -1;
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
    /// <summary>Samples per Opus frame (20 or 60 ms at 16 kHz). Longer frames send fewer packets, so far less header traffic.</summary>
    public volatile int TargetFrameSamples = VoiceJitterBuffer.FrameSamples;
    /// <summary>Opus encoder effort 0-10; higher is noticeably cleaner at low bitrates for a little more CPU.</summary>
    public volatile int TargetComplexity = 9;
    /// <summary>Applies a call mode: bitrate, frame length and encoder effort.</summary>
    public void ApplyMode(VoiceMode mode) => (TargetBitrate, TargetFrameSamples, TargetComplexity) = Profile(mode);
    public static (int Bitrate, int FrameSamples, int Complexity) Profile(VoiceMode mode) => mode switch
    {
        // Gaming keeps 20 ms packets for the lowest delay and a light encoder.
        VoiceMode.Gaming => (16000, VoiceJitterBuffer.FrameSamples, 5),
        // Economy: 12 kbit/s keeps wideband speech; 60 ms packets cut packet headers (about 50 bytes each) threefold.
        VoiceMode.Economy => (12000, VoiceJitterBuffer.FrameSamples * 3, 10),
        _ => (24000, VoiceJitterBuffer.FrameSamples, 9),
    };
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
        encoder.Bitrate = 24000; encoder.Complexity = 9; encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
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
                    if (fill == frameLength) { EncodeFrame(); fill = 0; frameLength = NextFrameLength(); }
                }
            }
            catch (Exception ex) { stopped = true; Error?.Invoke("Ошибка звука: " + ex.Message); }
        }
    }

    private int NextFrameLength() => Math.Clamp(TargetFrameSamples / VoiceJitterBuffer.FrameSamples, 1, 3) * VoiceJitterBuffer.FrameSamples;
    private void EncodeFrame()
    {
        // Keep encoder changes on the capture thread; changing Opus bitrate does not alter frame duration.
        var targetBitrate = TargetBitrate;
        if (appliedBitrate != targetBitrate)
        {
            encoder.Bitrate = targetBitrate;
            appliedBitrate = targetBitrate;
        }
        var complexity = Math.Clamp(TargetComplexity, 0, 10);
        if (appliedComplexity != complexity) { encoder.Complexity = complexity; appliedComplexity = complexity; }
        var pcm = frame.AsSpan(0, frameLength);
        if (NoiseSuppression) suppressor.Process(pcm);
        inputGain.Process(pcm, InputGain);
        double energy = 0;
        foreach (var sample in pcm) energy += (double)sample * sample;
        InputLevel = (float)Math.Sqrt(energy / frameLength) / 32768;
        // The gate holds for about 240 ms whatever the frame length.
        if (InputLevel >= 0.004f) gateHangover = 240 * 16 / frameLength;
        else if (gateHangover > 0) gateHangover--;
        var keyDown = !PushToTalk || (GetAsyncKeyState(TalkKey) & 0x8000) != 0;
        Transmitting = !Muted && !Deafened && keyDown && (!NoiseGate || gateHangover > 0);
        // Encoding zero PCM lets Opus enter DTX without leaking the previous voice frame.
        var size = encoder.Encode((Transmitting ? frame : silence).AsSpan(0, frameLength), frameLength, encoded.AsSpan(), encoded.Length);
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
