using System.Runtime.InteropServices;
using NAudio.Wave;
namespace Voicecon;

public enum NotificationCue
{
    MicrophoneOn, MicrophoneOff, HeadphonesOn, HeadphonesOff, CallConnected,
    ScreenShareOn, ScreenShareOff, CameraOn, CameraOff, ParticipantLeft, ViewerJoined, ViewerLeft, Message, StreamStarted
}

/// <summary>Immutable stereo PCM clip in the notification output's native format.</summary>
public sealed class NotificationClip
{
    internal readonly float[] Data;
    public ReadOnlyMemory<float> Samples => Data;
    public int Frames => Data.Length / 2;
    public NotificationClip(ReadOnlySpan<float> samples)
    {
        if (samples.Length % 2 != 0) throw new ArgumentException("Stereo samples must be aligned.");
        Data = samples.ToArray();
        for (var i = 0; i < Data.Length; i++) Data[i] = float.IsFinite(Data[i]) ? Math.Clamp(Data[i], -0.8f, 0.8f) : 0;
        // A custom recording must also start and finish at zero; no DC step at EOF.
        var frames = Frames; var attack = Math.Min(480, frames / 2); var release = Math.Min(960, frames / 2);
        for (var i = 0; i < frames; i++)
        {
            var gain = Smooth(Math.Min(1, i / (double)Math.Max(1, attack))) * Smooth(Math.Min(1, (frames - 1 - i) / (double)Math.Max(1, release)));
            Data[i * 2] *= (float)gain; Data[i * 2 + 1] *= (float)gain;
        }
    }
    internal static double Smooth(double t) => t * t * (3 - 2 * t);
    public static NotificationClip FromPcm16Mono(byte[] pcm, int rate = 16000)
    {
        var source = MemoryMarshal.Cast<byte, short>(pcm); var frames = source.Length * 48000 / rate; var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var position = i * rate / 48000d; var at = (int)position; var fraction = (float)(position - at);
            var sample = (source[at] * (1 - fraction) + source[Math.Min(source.Length - 1, at + 1)] * fraction) / 32768f;
            samples[i * 2] = samples[i * 2 + 1] = sample;
        }
        return new NotificationClip(samples);
    }
}

/// <summary>Two bounded sound slots, latest command wins. Silence never signals EOF to the device.</summary>
public sealed class NotificationMixer : IWaveProvider
{
    public const int SampleRate = 48000, TransitionFrames = 480;
    private readonly object gate = new();
    private readonly Slot cue = new(), ring = new();
    private NotificationClip? nextCue, nextRing;
    private bool changeCue, changeRing;
    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 2);
    public volatile float MasterGain = 1;
    public void SetCue(NotificationClip? clip) { lock (gate) { nextCue = clip; changeCue = true; } }
    public void SetRing(NotificationClip? clip) { lock (gate) { nextRing = clip; changeRing = true; } }
    public void Clear() { lock (gate) { cue.Clear(); ring.Clear(); nextCue = nextRing = null; changeCue = changeRing = false; } }
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count || count % 4 != 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (gate)
        {
            if (changeCue) { cue.Replace(nextCue); nextCue = null; changeCue = false; }
            if (changeRing) { ring.Replace(nextRing); nextRing = null; changeRing = false; }
            var output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, count));
            var gain = float.IsFinite(MasterGain) ? Math.Clamp(MasterGain, 0, 3) : 1;
            if (cue.Idle && ring.Idle) { output.Clear(); return count; }
            for (var i = 0; i < output.Length; i += 2)
            {
                var c = cue.Next(); var r = ring.Next();
                output[i] = AudioLevels.Limit((c.Left + r.Left) * gain * 32767);
                output[i + 1] = AudioLevels.Limit((c.Right + r.Right) * gain * 32767);
            }
        }
        return count;
    }
    private sealed class Slot
    {
        private NotificationClip? clip; private int position, tailPosition = TransitionFrames;
        private readonly float[] tail = new float[TransitionFrames * 2];
        public bool Idle => clip is null && tailPosition >= TransitionFrames;
        public void Clear() { clip = null; position = 0; tailPosition = TransitionFrames; Array.Clear(tail); }
        public void Replace(NotificationClip? next)
        {
            // Render the outgoing waveform into a fixed 10 ms fade, including any earlier fade.
            // This preserves phase continuity even when replacements arrive faster than the fade.
            for (var i = 0; i < TransitionFrames; i++)
            {
                var previous = Next(); var gain = (float)(1 - NotificationClip.Smooth(i / (double)(TransitionFrames - 1)));
                tail[i * 2] = previous.Left * gain; tail[i * 2 + 1] = previous.Right * gain;
            }
            clip = next; position = 0; tailPosition = 0;
        }
        public (float Left, float Right) Next()
        {
            float left = 0, right = 0;
            if (clip is not null)
            {
                if (position < clip.Frames) { left = clip.Data[position * 2]; right = clip.Data[position * 2 + 1]; position++; }
                else clip = null;
            }
            if (tailPosition < TransitionFrames) { left += tail[tailPosition * 2]; right += tail[tailPosition * 2 + 1]; tailPosition++; }
            return (left, right);
        }
    }
}
