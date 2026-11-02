using NAudio.Dsp;

namespace Voicecon;

/// <summary>Small streaming spectral suppressor: 512-point FFT, 10 ms hop at 16 kHz.</summary>
public sealed class NoiseSuppressor
{
    private const int Size = 512, Hop = 160;
    private readonly float[] history = new float[Size], overlap = new float[Size], weight = new float[Size];
    private readonly float[] window = new float[Size], noise = new float[Size / 2 + 1], gains = new float[Size / 2 + 1];
    private readonly Complex[] bins = new Complex[Size];
    private float previousInput, previousOutput;

    public NoiseSuppressor()
    {
        for (var i = 0; i < Size; i++) window[i] = MathF.Sqrt(0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / Size));
        Array.Fill(noise, 0.0000002f); Array.Fill(gains, 1f);
    }

    public void Process(Span<short> frame)
    {
        for (var start = 0; start < frame.Length; start += Hop)
        {
            double energy = 0;
            for (var i = 0; i < Hop; i++) energy += (double)frame[start + i] * frame[start + i];
            var likelySpeech = energy / Hop > 500 * 500;
            Array.Copy(history, Hop, history, 0, Size - Hop);
            for (var i = 0; i < Hop; i++)
            {
                var input = frame[start + i] / 32768f;
                // 100 Hz first-order high-pass removes low rumble and DC.
                var filtered = input - previousInput + 0.962f * previousOutput;
                previousInput = input; previousOutput = filtered;
                history[Size - Hop + i] = filtered;
            }
            for (var i = 0; i < Size; i++) { bins[i].X = history[i] * window[i]; bins[i].Y = 0; }
            FastFourierTransform.FFT(true, 9, bins);
            for (var i = 0; i <= Size / 2; i++)
            {
                var power = bins[i].X * bins[i].X + bins[i].Y * bins[i].Y;
                // Learn quiet background, freeze the estimate during speech and loud transients.
                if (!likelySpeech) noise[i] = MathF.Max(1e-10f, noise[i] * 0.98f + power * 0.02f);
                var target = MathF.Sqrt(Math.Clamp(1 - 1.6f * noise[i] / MathF.Max(power, 1e-10f), 0.04f, 1));
                var gain = gains[i] = gains[i] * 0.6f + target * 0.4f;
                bins[i].X *= gain; bins[i].Y *= gain;
                if (i > 0 && i < Size / 2) { bins[Size - i].X *= gain; bins[Size - i].Y *= gain; }
            }
            FastFourierTransform.FFT(false, 9, bins);
            for (var i = 0; i < Size; i++)
            {
                overlap[i] += bins[i].X * window[i]; weight[i] += window[i] * window[i];
            }
            for (var i = 0; i < Hop; i++)
                frame[start + i] = (short)Math.Clamp(overlap[i] / MathF.Max(weight[i], 0.001f) * 32768, short.MinValue, short.MaxValue);
            Array.Copy(overlap, Hop, overlap, 0, Size - Hop); Array.Clear(overlap, Size - Hop, Hop);
            Array.Copy(weight, Hop, weight, 0, Size - Hop); Array.Clear(weight, Size - Hop, Hop);
        }
    }
}
