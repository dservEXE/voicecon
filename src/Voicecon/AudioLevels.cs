namespace Voicecon;
public static class AudioLevels
{
    public const int MaximumPercent = 300;
    public static float Gain(int percent) => Math.Clamp(percent, 0, MaximumPercent) / 100f;
    public static short Limit(float sample)
    {
        if (!float.IsFinite(sample)) return 0;
        var absolute = Math.Abs(sample); const float knee = 29490f, room = 32767f - knee;
        if (absolute > knee) { var excess = absolute - knee; absolute = knee + room * excess / (room + excess); }
        return (short)Math.Clamp(Math.CopySign(absolute, sample), short.MinValue, short.MaxValue);
    }
}
public sealed class InputGainProcessor
{
    private float current = 1;
    public void Process(Span<short> samples, float target)
    {
        target = float.IsFinite(target) ? Math.Clamp(target, 0, 3) : 1;
        var start = current;
        for (var i = 0; i < samples.Length; i++) samples[i] = AudioLevels.Limit(samples[i] * (start + (target - start) * (i + 1) / Math.Max(1, samples.Length)));
        current = target;
    }
}
