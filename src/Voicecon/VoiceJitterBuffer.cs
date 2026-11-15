using Concentus;
using NAudio.Wave;

namespace Voicecon;

/// <summary>Bounded, sequence-aware playout queue (about 60 ms of audio before it starts). Playback drives the clock.
/// Packets may carry 20 to 60 ms; a loss is concealed for the length of the packet that went missing.</summary>
public sealed class VoiceJitterBuffer : IWaveProvider, IDisposable
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 320;
    public const int MaxFrameSamples = FrameSamples * 3;
    private const int StartSamples = FrameSamples * 3;
    /// <summary>Samples in an Opus packet at <paramref name="rate"/>; 20 ms if the packet cannot be read.</summary>
    public static int PacketSamples(ReadOnlySpan<byte> packet, int rate)
    {
        try { if (packet.Length > 0) { var n = Concentus.Structs.OpusPacketInfo.GetNumSamples(packet, rate); if (n > 0 && n <= rate * 120 / 1000) return n; } }
        catch (Exception) { }
        return rate / 50;
    }
    /// <summary>RTP timestamp step for a packet (Opus RTP always counts at 48 kHz).</summary>
    public static uint Duration48k(ReadOnlySpan<byte> packet) => (uint)PacketSamples(packet, 48000);
    private readonly object sync = new();
    private readonly Dictionary<ushort, byte[]> packets = new();
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
    private readonly short[] decoded = new short[FrameSamples * 6];
    private readonly byte[] pcm = new byte[FrameSamples * 12];
    private int pcmOffset, pcmLength, misses, lastSamples = FrameSamples, bufferedSamples;
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
                packets.Clear(); next = sequence; started = false; bufferedSamples = 0;
            }
            if (packets.TryAdd(sequence, payload)) { Interlocked.Increment(ref received); bufferedSamples += PacketSamples(payload, SampleRate); }
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
            if (!started && (bufferedSamples >= StartSamples || packets.Count >= 3)) { started = true; misses = 0; }
            if (started)
            {
                if (packets.Remove(next, out packet)) bufferedSamples = Math.Max(0, bufferedSamples - PacketSamples(packet, SampleRate));
                if (packet is null)
                {
                    Interlocked.Increment(ref lost);
                    packets.TryGetValue(unchecked((ushort)(next + 1)), out fec);
                    conceal = ++misses <= 10;
                    if (!conceal) { started = false; haveFirst = false; packets.Clear(); bufferedSamples = 0; }
                }
                else misses = 0;
                next++;
            }
        }
        var samples = FrameSamples;
        try
        {
            if (packet is not null) { samples = decoder.Decode(packet.AsSpan(), decoded.AsSpan(), FrameSamples * 6); if (samples > 0) lastSamples = Math.Min(samples, MaxFrameSamples); }
            else if (fec is not null)
            {
                // The lost packet had the length of its neighbours; FEC rebuilds it from the next one.
                samples = decoder.Decode(fec.AsSpan(), decoded.AsSpan(), lastSamples, true);
                Interlocked.Increment(ref recovered);
            }
            else if (conceal) samples = decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), lastSamples);
            else { Array.Clear(decoded); samples = lastSamples; }
        }
        catch { Array.Clear(decoded); samples = lastSamples; }
        if (samples <= 0) { Array.Clear(decoded); samples = FrameSamples; }
        var gain = Deafened ? 0 : Volume;
        for (var i = 0; i < samples; i++) decoded[i] = (short)Math.Clamp(decoded[i] * gain, short.MinValue, short.MaxValue);
        pcmLength = samples * 2; pcmOffset = 0;
        Buffer.BlockCopy(decoded, 0, pcm, 0, pcmLength);
    }

    public void Dispose() => decoder.Dispose();
}
