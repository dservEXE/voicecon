using System.Buffers.Binary;
namespace Voicecon;
public sealed class VideoAssembler
{
    private readonly Dictionary<byte, Assembly> active = [];
    public byte[]? Add(byte[] part)
    {
        if (part.Length < 15 || part.Length > 16_015 || part[0] != 0x81 || part[1] is not (1 or 2)) return null;
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(part.AsSpan(2)); var index = BinaryPrimitives.ReadUInt16LittleEndian(part.AsSpan(6)); var count = BinaryPrimitives.ReadUInt16LittleEndian(part.AsSpan(8)); var size = BinaryPrimitives.ReadInt32LittleEndian(part.AsSpan(10));
        if (size is < 15 or > H264Packet.Limit || count != (size + 15999) / 16000 || index >= count) return null;
        var offset = index * 16000; if (part.Length - 14 != Math.Min(16000, size - offset)) return null;
        if (index == 0) active[part[1]] = new(seq, new byte[size]);
        if (!active.TryGetValue(part[1], out var a) || a.Sequence != seq || a.Data.Length != size || a.Next != index) return null;
        part.AsSpan(14).CopyTo(a.Data.AsSpan(offset)); a.Next++;
        if (a.Next != count) return null; active.Remove(part[1]); return a.Data;
    }
    public static IEnumerable<byte[]> Split(byte[] frame)
    {
        var count = (frame.Length + 15999) / 16000;
        for (var i = 0; i < count; i++)
        {
            var length = Math.Min(16000, frame.Length - i * 16000); var p = new byte[14 + length]; p[0] = 0x81; p[1] = frame[0]; frame.AsSpan(1, 4).CopyTo(p.AsSpan(2));
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)i); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), (ushort)count); BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(10), frame.Length); frame.AsSpan(i * 16000, length).CopyTo(p.AsSpan(14)); yield return p;
        }
    }
    private sealed class Assembly(uint seq, byte[] data) { public uint Sequence = seq; public byte[] Data = data; public int Next; }
}
