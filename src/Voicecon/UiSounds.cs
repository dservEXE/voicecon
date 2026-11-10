using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace Voicecon;

/// <summary>One persistent output for cues and ringing. Button presses never reset the device.</summary>
public sealed class UiSounds : IDisposable
{
    private static readonly NotificationClip[] cues = Enum.GetValues<NotificationCue>().Select(CreateClip).ToArray();
    private static readonly Lazy<NotificationClip> bell = new(() => NotificationClip.FromPcm16Mono(CreateRingtone(false)));
    private static readonly Lazy<NotificationClip> softRing = new(CreateWaterRing);
    private static readonly Lazy<NotificationClip> borisRing = new(() => CreateEmbeddedRingtone("Voicecon.Ringtone.Boris"));
    private static readonly Lazy<NotificationClip> birdMachineRing = new(() => CreateEmbeddedRingtone("Voicecon.Ringtone.BirdMachine"));
    private static readonly Lazy<NotificationClip> dial = new(CreateDial);
    private static readonly NotificationClip callEnded = CreateCallEnded();
    public void PlayDial(int outputDevice) => Start(dial.Value, outputDevice, true);
    public void PlayConnected(int outputDevice) => PlayCue(NotificationCue.CallConnected, outputDevice);
    public void PlayCallEnded(int outputDevice) => Start(callEnded, outputDevice, false);
    public static void WarmRingtones() => _ = Task.Run(() => { _ = borisRing.Value; _ = birdMachineRing.Value; });
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
    public void PlayCue(NotificationCue cue, int outputDevice) => Start(cues[(int)cue], outputDevice, false);
    public void PlayRing(string kind, string customFile, int outputDevice)
    {
        var clip = kind switch { "boris" => borisRing.Value, "birdmachine" => birdMachineRing.Value, "soft" => softRing.Value, _ => bell.Value };
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
                        using WaveStream source = Path.GetExtension(file.Name).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? new Mp3FileReaderBase(file.FullName, format => new AcmMp3FrameDecompressor(format)) : new WaveFileReader(file.FullName);
                        ISampleProvider samples = source.ToSampleProvider();
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
    private static NotificationClip CreateEmbeddedRingtone(string resourceName)
    {
        try
        {
            using var stream = typeof(UiSounds).Assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException(resourceName);
            using var source = new Mp3FileReaderBase(stream, format => new AcmMp3FrameDecompressor(format));
            ISampleProvider samples = source.ToSampleProvider();
            if (samples.WaveFormat.Channels is not (1 or 2)) throw new InvalidDataException();
            if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
            if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
            var data = new float[48000 * 2 * 8]; var count = 0;
            while (count < data.Length)
            {
                var read = samples.Read(data, count, data.Length - count);
                if (read <= 0) break;
                count += read;
            }
            if (count == 0) throw new InvalidDataException();
            Array.Resize(ref data, count);
            for (var i = 0; i < data.Length; i++) data[i] *= 0.42f;
            return new NotificationClip(data);
        }
        catch { return bell.Value; }
    }
    // ---- Sound design: soft, muffled "water drop" cues (ASMR-like), no clicks. ----
    // Each cue is built from droplets: a sine whose pitch glides quickly (the bubble resonance), with a gentle attack and
    // exponential decay, then low-pass filtered (muffled) and given a short watery echo. Peaks are normalised per cue.
    private sealed class Synth(int frames)
    {
        public readonly float[] Data = new float[frames * 2];
        public int Frames { get; } = frames;
        /// <summary>Global pitch: everything sits about a fifth lower than the design notes, for a deeper, rounder sound.</summary>
        private const double Depth = 0.68;
        public Synth Drop(double startMs, double from, double to, double durationMs, double level, double pan = 0)
        {
            from *= Depth; to *= Depth;
            var start = (int)(startMs * 48); var length = (int)(durationMs * 48); double phase = 0;
            for (var i = 0; i < length && start + i < Frames; i++)
            {
                var t = i / (double)length;
                var frequency = from * Math.Pow(to / from, 1 - Math.Pow(1 - t, 3));
                phase += 2 * Math.PI * frequency / 48000;
                var envelope = NotificationClip.Smooth(Math.Min(1, i / 300d)) * Math.Exp(-t * 4.0);
                // Fundamental, a soft second harmonic and a sub-octave "body" that gives the drop its depth.
                var sample = (Math.Sin(phase) + 0.12 * Math.Sin(2 * phase + 0.4) + 0.42 * Math.Sin(phase * 0.5)) * level * envelope;
                Data[(start + i) * 2] += (float)(sample * (1 - Math.Max(0, pan))); Data[(start + i) * 2 + 1] += (float)(sample * (1 + Math.Min(0, pan)));
            }
            return this;
        }
        public Synth Hum(double startMs, double frequency, double durationMs, double level)
        {
            frequency *= Depth;
            var start = (int)(startMs * 48); var length = (int)(durationMs * 48);
            for (var i = 0; i < length && start + i < Frames; i++)
            {
                var t = i / (double)length; var envelope = Math.Sin(Math.PI * t); envelope *= envelope;
                var wobble = 1 + 0.004 * Math.Sin(2 * Math.PI * 5.5 * i / 48000);
                var sample = Math.Sin(2 * Math.PI * frequency * wobble * i / 48000) * level * envelope;
                Data[(start + i) * 2] += (float)sample; Data[(start + i) * 2 + 1] += (float)sample;
            }
            return this;
        }
        /// <summary>Two-pass one-pole low-pass (zero phase): rounds every edge, the "under water" colour.</summary>
        public Synth Muffle(double cutoff)
        {
            var a = 1 - Math.Exp(-2 * Math.PI * cutoff * 0.82 / 48000);
            for (var channel = 0; channel < 2; channel++)
            {
                double y = 0; for (var i = 0; i < Frames; i++) { y += a * (Data[i * 2 + channel] - y); Data[i * 2 + channel] = (float)y; }
                y = 0; for (var i = Frames - 1; i >= 0; i--) { y += a * (Data[i * 2 + channel] - y); Data[i * 2 + channel] = (float)y; }
            }
            return this;
        }
        /// <summary>Short feedback echoes, slightly different per ear: a small wet room.</summary>
        public Synth Wet(double amount = 0.22)
        {
            foreach (var (delayMs, channel) in new[] { (19.0, 0), (23.0, 1), (31.0, 0), (37.0, 1) })
            {
                var delay = (int)(delayMs * 48);
                for (var i = delay; i < Frames; i++) Data[i * 2 + channel] += (float)(Data[(i - delay) * 2 + channel] * amount * 1.12);
            }
            return this;
        }
        public NotificationClip Build(double peak, double silentTailMs = 40)
        {
            var tail = (int)(silentTailMs * 48); var fade = 960; var end = Math.Max(0, Frames - tail);
            for (var i = 0; i < Frames; i++)
            {
                double gain = i >= end ? 0 : i >= end - fade ? NotificationClip.Smooth((end - i) / (double)fade) : 1;
                Data[i * 2] = (float)(Data[i * 2] * gain); Data[i * 2 + 1] = (float)(Data[i * 2 + 1] * gain);
            }
            var max = Data.Max(v => Math.Abs(v));
            if (max > 0) for (var i = 0; i < Data.Length; i++) Data[i] = (float)(Data[i] / max * peak);
            return new NotificationClip(Data);
        }
    }
    private static NotificationClip CreateCallEnded() =>
        new Synth(18720).Drop(0, 620, 420, 140, 0.10, -0.15).Drop(125, 470, 290, 200, 0.09, 0.15).Hum(0, 196, 300, 0.02).Muffle(1900).Wet().Build(0.11);
    private static NotificationClip CreateClip(NotificationCue cue) => cue switch
    {
        // Microphone cues keep the historical 155 ms length; on rises, off falls.
        NotificationCue.MicrophoneOn => new Synth(7440).Drop(0, 520, 900, 92, 0.12).Muffle(2400).Wet(0.18).Build(0.11),
        NotificationCue.MicrophoneOff => new Synth(7440).Drop(0, 780, 430, 92, 0.12).Muffle(2200).Wet(0.18).Build(0.11),
        NotificationCue.HeadphonesOn => new Synth(11520).Drop(0, 330, 560, 115, 0.11, -0.2).Drop(80, 440, 760, 125, 0.09, 0.2).Muffle(1700).Wet().Build(0.11),
        NotificationCue.HeadphonesOff => new Synth(11520).Drop(0, 560, 340, 115, 0.11, 0.2).Drop(80, 420, 240, 135, 0.09, -0.2).Muffle(1600).Wet().Build(0.11),
        NotificationCue.CallConnected => new Synth(18720).Hum(0, 220, 360, 0.025).Drop(0, 350, 520, 125, 0.10, -0.25).Drop(90, 440, 660, 130, 0.09).Drop(185, 520, 880, 170, 0.09, 0.25).Muffle(2100).Wet().Build(0.12),
        NotificationCue.ScreenShareOn => new Synth(14400).Drop(0, 600, 900, 85, 0.09, -0.3).Drop(60, 700, 1050, 90, 0.08).Drop(120, 820, 1300, 135, 0.08, 0.3).Muffle(2600).Wet(0.24).Build(0.11),
        NotificationCue.ScreenShareOff => new Synth(14400).Drop(0, 1100, 800, 85, 0.09, 0.3).Drop(60, 900, 640, 90, 0.08).Drop(120, 700, 440, 150, 0.08, -0.3).Muffle(2300).Wet(0.24).Build(0.11),
        NotificationCue.CameraOn => new Synth(11520).Drop(0, 480, 760, 90, 0.10).Drop(70, 640, 1020, 120, 0.08, 0.2).Muffle(2300).Wet().Build(0.10),
        NotificationCue.CameraOff => new Synth(11520).Drop(0, 760, 480, 90, 0.10).Drop(70, 560, 360, 125, 0.08, -0.2).Muffle(2100).Wet().Build(0.10),
        NotificationCue.ParticipantLeft => new Synth(14400).Drop(0, 520, 360, 140, 0.10).Drop(110, 400, 260, 170, 0.09).Muffle(1700).Wet().Build(0.10),
        NotificationCue.ViewerJoined => new Synth(9600).Drop(0, 900, 1400, 70, 0.07, -0.2).Drop(55, 1200, 1700, 90, 0.05, 0.2).Muffle(3000).Wet(0.26).Build(0.075),
        NotificationCue.ViewerLeft => new Synth(9600).Drop(0, 1300, 850, 80, 0.07, 0.2).Drop(55, 1000, 680, 100, 0.05, -0.2).Muffle(2600).Wet(0.26).Build(0.07),
        NotificationCue.Message => new Synth(7200).Drop(0, 1050, 1450, 60, 0.06).Muffle(1900).Wet(0.2).Build(0.06),
        NotificationCue.StreamStarted => new Synth(12480).Drop(0, 640, 980, 100, 0.08, -0.2).Drop(90, 880, 1320, 140, 0.07, 0.2).Muffle(2500).Wet(0.24).Build(0.09),
        _ => new Synth(4800).Build(0.01)
    };
    // Compatibility helper used by device-free PCM checks.
    public static byte[] CreatePcm(bool microphoneEnabled)
    {
        var source = Clip(microphoneEnabled ? NotificationCue.MicrophoneOn : NotificationCue.MicrophoneOff).Samples.Span;
        var pcm = new byte[source.Length / 6 * 2];
        for (var i = 0; i < pcm.Length / 2; i++) { var sample = (short)Math.Round(source[i * 6] * 32767); pcm[i * 2] = (byte)sample; pcm[i * 2 + 1] = (byte)(sample >> 8); }
        return pcm;
    }
    /// <summary>Ringback: a soft, rounded under-water tone for one second, then two seconds of silence.</summary>
    private static NotificationClip CreateDial()
    {
        var synth = new Synth(48000 * 3);
        synth.Hum(0, 425, 950, 0.07).Hum(0, 212.5, 950, 0.025);
        for (var i = 0; i < 48000; i++)
        {
            var tremolo = 0.82 + 0.18 * Math.Sin(2 * Math.PI * 4 * i / 48000);
            synth.Data[i * 2] *= (float)tremolo; synth.Data[i * 2 + 1] *= (float)tremolo;
        }
        return synth.Muffle(1400).Build(0.075, 2000);
    }
    /// <summary>"Мягкий звонок": a slow bubbling arpeggio that repeats.</summary>
    private static NotificationClip CreateWaterRing()
    {
        var synth = new Synth(48000 * 2);
        double[] notes = [392, 494, 587, 784, 587, 494];
        for (var i = 0; i < notes.Length; i++) synth.Drop(i * 170, notes[i] * 0.85, notes[i] * 1.12, 260, 0.09, i % 2 == 0 ? -0.3 : 0.3);
        synth.Hum(0, 196, 1300, 0.02);
        return synth.Muffle(2100).Wet(0.25).Build(0.12, 300);
    }
    public static byte[] CreateRingtone(bool soft)
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
