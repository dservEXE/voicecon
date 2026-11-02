using Concentus;
using NAudio.Wave;

namespace Voicecon;

/// <summary>Bounded, sequence-aware 40 ms playout queue. Playback drives the clock.</summary>
public sealed class VoiceJitterBuffer : IWaveProvider, IDisposable
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 320;
    private readonly object sync = new();
    private readonly Dictionary<ushort, byte[]> packets = new();
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
    private readonly short[] decoded = new short[FrameSamples * 6];
    private readonly byte[] pcm = new byte[FrameSamples * 12];
    private int pcmOffset, pcmLength, misses;
    private ushort next;
    private bool started, haveFirst;
    private long lost, received, recovered;
    public float Volume { get; set; } = 1;
    public bool Deafened { get; set; }
    public long Lost => Interlocked.Read(ref lost);
    public long Received => Interlocked.Read(ref received);
    public long Recovered => Interlocked.Read(ref recovered);
    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    public void Add(ushort sequence, byte[] payload)
    {
        if (payload.Length is 0 or > 1275) return;
        lock (sync)
        {
            if (!haveFirst) { next = sequence; haveFirst = true; }
            var distance = (short)(sequence - next);
            if (distance < 0)
            {
                if (!started && distance >= -5) next = sequence;
                else return;
            }
            if (distance > 20 || packets.Count >= 12)
            {
                packets.Clear(); next = sequence; started = false;
            }
            if (packets.TryAdd(sequence, payload)) Interlocked.Increment(ref received);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var written = 0;
        while (written < count)
        {
            if (pcmOffset >= pcmLength) DecodeFrame();
            var take = Math.Min(count - written, pcmLength - pcmOffset);
            Buffer.BlockCopy(pcm, pcmOffset, buffer, offset + written, take);
            pcmOffset += take; written += take;
        }
        return count;
    }

    private void DecodeFrame()
    {
        byte[]? packet = null, fec = null;
        bool conceal = false;
        lock (sync)
        {
            if (!started && packets.Count >= 3) { started = true; misses = 0; }
            if (started)
            {
                packets.Remove(next, out packet);
                if (packet is null)
                {
                    Interlocked.Increment(ref lost);
                    packets.TryGetValue(unchecked((ushort)(next + 1)), out fec);
                    conceal = ++misses <= 10;
                    if (!conceal) { started = false; haveFirst = false; packets.Clear(); }
                }
                else misses = 0;
                next++;
            }
        }
        var samples = FrameSamples;
        try
        {
            if (packet is not null) samples = decoder.Decode(packet.AsSpan(), decoded.AsSpan(), FrameSamples * 6);
            else if (fec is not null)
            {
                samples = decoder.Decode(fec.AsSpan(), decoded.AsSpan(), FrameSamples, true);
                Interlocked.Increment(ref recovered);
            }
            else if (conceal) samples = decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), FrameSamples);
            else Array.Clear(decoded);
        }
        catch { Array.Clear(decoded); samples = FrameSamples; }
        var gain = Deafened ? 0 : Volume;
        for (var i = 0; i < samples; i++) decoded[i] = (short)Math.Clamp(decoded[i] * gain, short.MinValue, short.MaxValue);
        pcmLength = samples * 2; pcmOffset = 0;
        Buffer.BlockCopy(decoded, 0, pcm, 0, pcmLength);
    }

    public void Dispose() => decoder.Dispose();
}
