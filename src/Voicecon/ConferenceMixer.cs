using NAudio.Wave;

namespace Voicecon;

public sealed class ConferenceMixer : IWaveProvider, IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, VoiceJitterBuffer> sources = new();
    private readonly Dictionary<string, float> gains = new();
    public void SetPeerVolume(string id, int percent) { lock (sync) gains[id] = AudioLevels.Gain(percent); }
    private byte[] scratch = new byte[4096];
    private int[] sum = new int[2048];
    public float Volume { get; set; } = 1;
    public bool Deafened { get; set; }
    public WaveFormat WaveFormat { get; } = new(VoiceJitterBuffer.SampleRate, 16, 1);
    public long Lost { get { lock (sync) return sources.Values.Sum(s => s.Lost); } }
    public long Received { get { lock (sync) return sources.Values.Sum(s => s.Received); } }

    public void AddPeer(string id)
    {
        lock (sync) if (!sources.ContainsKey(id)) sources[id] = new();
    }
    public void RemovePeer(string id)
    {
        lock (sync) { if (sources.Remove(id, out var old)) old.Dispose(); gains.Remove(id); }
    }
    public void Receive(string id, ushort sequence, byte[] payload)
    {
        lock (sync) if (sources.TryGetValue(id, out var source)) source.Add(sequence, payload);
    }
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (sync)
        {
            if (scratch.Length < count) { scratch = new byte[count]; sum = new int[count / 2]; }
            Array.Clear(sum);
            foreach (var pair in sources)
            {
                pair.Value.Read(scratch, 0, count); var peerGain = gains.GetValueOrDefault(pair.Key, 1f);
                for (var i = 0; i < count / 2; i++) sum[i] += (int)(BitConverter.ToInt16(scratch, i * 2) * peerGain);
            }
            var gain = Deafened ? 0 : Volume;
            for (var i = 0; i < count / 2; i++)
            {
                var value = AudioLevels.Limit(sum[i] * gain);
                buffer[offset + i * 2] = (byte)value; buffer[offset + i * 2 + 1] = (byte)(value >> 8);
            }
        }
        return count;
    }
    public void Dispose() { lock (sync) { foreach (var pair in sources) pair.Value.Dispose(); sources.Clear(); gains.Clear(); } }
}
