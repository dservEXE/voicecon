using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace Voicecon;

/// <summary>One persistent output for cues and ringing. Button presses never reset the device.</summary>
public sealed class UiSounds : IDisposable
{
    private static readonly NotificationClip[] cues = Enum.GetValues<NotificationCue>().Select(CreateClip).ToArray();
    private static readonly Lazy<NotificationClip> bell = new(() => NotificationClip.FromPcm16Mono(CreateRingtone(false)));
    private static readonly Lazy<NotificationClip> softRing = new(() => NotificationClip.FromPcm16Mono(CreateRingtone(true)));
    private static readonly Lazy<NotificationClip> dial = new(CreateDial);
    public void PlayDial(int outputDevice) => Start(dial.Value, outputDevice, true);
    public void PlayConnected(int outputDevice) => PlayCue(NotificationCue.CallConnected, outputDevice);
    private readonly object gate = new();
    private readonly NotificationMixer mixer = new();
    public int VolumePercent { set => mixer.MasterGain = AudioLevels.Gain(value); }
    private readonly Func<int, IWavePlayer> createPlayer;
    private IWavePlayer? player;
    private bool disposed, failed;
    private int device = int.MinValue;
    private string customPath = ""; private DateTime customStamp; private long customLength; private NotificationClip? custom;
    public UiSounds(Func<int, IWavePlayer>? outputFactory = null)
        => createPlayer = outputFactory ?? (index => new WaveOutEvent { DeviceNumber = index, DesiredLatency = 60, NumberOfBuffers = 3 });
    public void Play(bool microphoneEnabled, int outputDevice) => PlayCue(microphoneEnabled ? NotificationCue.MicrophoneOn : NotificationCue.MicrophoneOff, outputDevice);
    public void PlayHeadphones(bool headphonesEnabled, int outputDevice) => PlayCue(headphonesEnabled ? NotificationCue.HeadphonesOn : NotificationCue.HeadphonesOff, outputDevice);
    private void PlayCue(NotificationCue cue, int outputDevice) => Start(cues[(int)cue], outputDevice, false);
    public void PlayRing(string kind, string customFile, int outputDevice)
    {
        var clip = kind == "soft" ? softRing.Value : bell.Value;
        if (kind == "custom")
        {
            try
            {
                lock (gate)
                {
                    if (disposed) return;
                    var file = new FileInfo(customFile);
                    if (file.Length is <= 0 or > 10 * 1024 * 1024) throw new IOException();
                    if (custom is null || customPath != file.FullName || customStamp != file.LastWriteTimeUtc || customLength != file.Length)
                    {
                        using var source = new WaveFileReader(file.FullName); ISampleProvider samples = source.ToSampleProvider();
                        if (samples.WaveFormat.Channels is not (1 or 2)) throw new InvalidDataException("Нужен моно или стерео WAV.");
                        if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
                        if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
                        var data = new List<float>(); var chunk = new float[4096]; int read;
                        while (data.Count < 48000 * 2 * 30 && (read = samples.Read(chunk, 0, Math.Min(chunk.Length, 48000 * 2 * 30 - data.Count))) > 0) data.AddRange(chunk.AsSpan(0, read).ToArray());
                        if (data.Count == 0) throw new InvalidDataException();
                        custom = new NotificationClip(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data)); customPath = file.FullName; customStamp = file.LastWriteTimeUtc; customLength = file.Length;
                    }
                    clip = custom;
                }
            }
            catch { } // Invalid custom recordings fall back to the built-in bell.
        }
        Start(clip, outputDevice, true);
    }
    public void Stop() { mixer.SetCue(null); mixer.SetRing(null); }
    public void StopCue() => mixer.SetCue(null);
    public void StopRing() => mixer.SetRing(null);
    private void Start(NotificationClip clip, int outputDevice, bool ring)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                if (player is null || device != outputDevice || failed || player.PlaybackState == PlaybackState.Stopped)
                {
                    CloseOutput(); mixer.Clear();
                    player = createPlayer(outputDevice); device = outputDevice; failed = false;
                    player.PlaybackStopped += OnStopped; player.Init(mixer);
                }
                if (ring) mixer.SetRing(clip); else mixer.SetCue(clip);
                if (player.PlaybackState != PlaybackState.Playing) player.Play();
            }
            catch { CloseOutput(); mixer.Clear(); } // A notification must never prevent a mute action.
        }
    }
    private void OnStopped(object? sender, StoppedEventArgs e)
    { lock (gate) { if (!disposed && ReferenceEquals(sender, player)) failed = true; } }
    private void CloseOutput()
    {
        var old = player; player = null;
        if (old is null) return;
        old.PlaybackStopped -= OnStopped;
        try { old.Stop(); } catch { }
        try { old.Dispose(); } catch { }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; mixer.Clear(); CloseOutput(); custom = null; } }

    public static NotificationClip Clip(NotificationCue cue) => cues[(int)cue];
    private static NotificationClip CreateClip(NotificationCue cue)
    {
        var frames = cue == NotificationCue.CallConnected ? 18720 : cue is NotificationCue.MicrophoneOn or NotificationCue.MicrophoneOff ? 7440 : 11520;
        var data = new float[frames * 2];
        void Note(double startMs, double durationMs, params double[] frequencies)
        {
            var start = (int)(startMs * 48); var length = (int)(durationMs * 48);
            for (var i = 0; i < length && start + i < frames; i++)
            {
                var attack = NotificationClip.Smooth(Math.Min(1, i / 288d));
                var release = NotificationClip.Smooth(Math.Min(1, (length - 1 - i) / 576d));
                double value = 0; foreach (var f in frequencies) value += Math.Sin(i * 2 * Math.PI * f / 48000);
                var sample = (float)(value / frequencies.Length * 0.115 * attack * release);
                data[(start + i) * 2] += sample; data[(start + i) * 2 + 1] += sample;
            }
        }
        switch (cue)
        {
            case NotificationCue.MicrophoneOn: Note(0, 55, 880); Note(68, 70, 1174.66); break;
            case NotificationCue.MicrophoneOff: Note(0, 60, 659.25); Note(73, 60, 440); break;
            case NotificationCue.HeadphonesOn: Note(0, 80, 261.63, 392); Note(100, 105, 392, 523.25); break;
            case NotificationCue.HeadphonesOff: Note(0, 80, 196, 293.66); Note(110, 90, 146.83, 220); break;
            case NotificationCue.CallConnected: Note(0, 75, 392); Note(90, 80, 587.33); Note(185, 150, 783.99, 987.77); break;
        }
        return new NotificationClip(data);
    }
    // Compatibility helper used by device-free PCM checks.
    public static byte[] CreatePcm(bool microphoneEnabled)
    {
        var source = Clip(microphoneEnabled ? NotificationCue.MicrophoneOn : NotificationCue.MicrophoneOff).Samples.Span;
        var pcm = new byte[source.Length / 6 * 2];
        for (var i = 0; i < pcm.Length / 2; i++) { var sample = (short)Math.Round(source[i * 6] * 32767); pcm[i * 2] = (byte)sample; pcm[i * 2 + 1] = (byte)(sample >> 8); }
        return pcm;
    }
    private static NotificationClip CreateDial()
    {
        var samples = new float[48000 * 2 * 3];
        for (var i = 0; i < 48000; i++)
        {
            var fade = NotificationClip.Smooth(Math.Min(1, Math.Min(i, 47999 - i) / 720d));
            var value = (float)(Math.Sin(i * 2 * Math.PI * 425 / 48000) * 0.085 * fade);
            samples[i * 2] = samples[i * 2 + 1] = value;
        }
        return new NotificationClip(samples);
    }    public static byte[] CreateRingtone(bool soft)
    {
        var pcm = new byte[16000 * 2 * 2];
        for (var i = 0; i < 16000 * 2; i++)
        {
            var time = i / 16000d; double value;
            if (soft) value = (Math.Sin(2 * Math.PI * 523 * time) + Math.Sin(2 * Math.PI * 659 * time)) * Math.Pow(Math.Max(0, 1 - time % 0.65 / 0.65), 2) * 0.065;
            else
            {
                var strike = time % 0.043; var envelope = Math.Exp(-strike * 60) * Math.Min(1, strike / 0.001);
                value = (Math.Sin(2 * Math.PI * 1050 * time) + Math.Sin(2 * Math.PI * 1460 * time) * 0.7 + Math.Sin(2 * Math.PI * 2340 * time) * 0.3) * envelope * 0.10;
                if (time % 1 > 0.75) value = 0;
            }
            value *= Math.Clamp(Math.Min(i, 31999 - i) / 240d, 0, 1);
            var sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue); pcm[i * 2] = (byte)sample; pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }
}
