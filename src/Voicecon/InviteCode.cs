using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;

namespace Voicecon;

public static class InviteCode
{
    private const int MaxBytes = 64 * 1024;
    public static string Encode(RTCSessionDescriptionInit description)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, description.type.ToString(), description.sdp));
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.SmallestSize, true)) compressor.Write(json);
        return "VC1." + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static RTCSessionDescriptionInit Decode(string code)
    {
        try
        {
            if (code.Length > 24_000) throw new FormatException();
            var clean = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
            if (!clean.StartsWith("VC1.", StringComparison.Ordinal)) throw new FormatException();
            var b64 = clean[4..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
            using var input = new MemoryStream(Convert.FromBase64String(b64));
            using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[2048];
            int count;
            while ((count = decompressor.Read(buffer)) != 0)
            {
                if (output.Length + count > MaxBytes) throw new FormatException();
                output.Write(buffer, 0, count);
            }
            var payload = JsonSerializer.Deserialize<Payload>(output.ToArray());
            if (payload?.Version != 1 || payload.Type is not ("offer" or "answer") ||
                payload.Sdp is null || !payload.Sdp.StartsWith("v=0") ||
                !payload.Sdp.Contains("a=fingerprint:sha-256 ") || !payload.Sdp.Contains("m=audio"))
                throw new FormatException();
            return new RTCSessionDescriptionInit
            {
                type = payload.Type == "offer" ? RTCSdpType.offer : RTCSdpType.answer,
                sdp = payload.Sdp
            };
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or JsonException or IOException)
        {
            throw new FormatException("Код не распознан. Скопируйте весь код, начиная с VC1.", e);
        }
    }

    private sealed record Payload(int Version, string Type, string Sdp);
}
