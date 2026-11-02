using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voicecon;

public sealed record FriendCard(string Id, string Name, string Inbox, string PublicKey, string Server)
{
    public string Encode() => "VCF1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static FriendCard Decode(string input)
    {
        try
        {
            var code = string.Concat(input.Where(c => !char.IsWhiteSpace(c)));
            if (!code.StartsWith("VCF1.") || code.Length > 4000) throw new FormatException();
            var text = code[5..].Replace('-', '+').Replace('_', '/'); text = text.PadRight((text.Length + 3) / 4 * 4, '=');
            var card = JsonSerializer.Deserialize<FriendCard>(Convert.FromBase64String(text)) ?? throw new FormatException();
            if (card.Name.Length is < 1 or > 48 || card.Inbox.Length != 39 || !card.Inbox.StartsWith("vc-") || card.Inbox[3..].Any(c => !char.IsAsciiHexDigit(c))) throw new FormatException();
            if (card.Id != ContactBook.KeyId(card.PublicKey)) throw new FormatException();
            using var key = ECDiffieHellman.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(card.PublicKey), out _);
            if (key.KeySize != 256 || !Uri.TryCreate(card.Server, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new FormatException();
            return card with { Name = new string(card.Name.Where(c => !char.IsControl(c)).ToArray()), Server = card.Server.TrimEnd('/') };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new FormatException("Не удалось прочитать код друга. Нужен полный код VCF1.…", ex); }
    }
}

public sealed class ContactBook : IDisposable
{
    private readonly ECDiffieHellman key;
    private readonly string path;
    private readonly string inbox;
    private readonly object sync = new();
    public string Id { get; }
    public string PublicKey { get; }
    public List<FriendCard> Friends { get; } = [];
    public ContactBook(string? directory = null)
    {
        path = Path.Combine(directory ?? Path.GetDirectoryName(Settings.FilePath)!, "contacts.json");
        key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new IOException("Не удалось прочитать контакты.");
            var secret = ProtectedData.Unprotect(Convert.FromBase64String(data.ProtectedKey), null, DataProtectionScope.CurrentUser);
            try { key.ImportPkcs8PrivateKey(secret, out _); } finally { CryptographicOperations.ZeroMemory(secret); }
            inbox = data.Inbox;
            foreach (var card in data.Friends) Friends.Add(FriendCard.Decode(card.Encode()));
        }
        else inbox = "vc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()); Id = KeyId(PublicKey);
        if (!File.Exists(path)) Save();
    }
    public static string KeyId(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey))).ToLowerInvariant()[..32];
    public FriendCard OwnCard(string name, string server) => new(Id, name.Trim()[..Math.Min(name.Trim().Length, 48)], inbox, PublicKey, server.TrimEnd('/'));
    public void Add(FriendCard friend)
    {
        if (friend.Id == Id) throw new InvalidOperationException("Это ваш собственный код.");
        lock (sync) { Friends.RemoveAll(f => f.Id == friend.Id); Friends.Add(friend); Save(); }
    }
    public void Remove(string id) { lock (sync) { Friends.RemoveAll(f => f.Id == id); Save(); } }
    private byte[] SharedKey(FriendCard friend)
    {
        using var remote = ECDiffieHellman.Create(); remote.ImportSubjectPublicKeyInfo(Convert.FromBase64String(friend.PublicKey), out _);
        lock (sync) return key.DeriveKeyFromHash(remote.PublicKey, HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes("Voicecon signaling v1"), null);
    }
    public string Seal(FriendCard friend, CallSignal signal)
    {
        var shared = SharedKey(friend); var plain = JsonSerializer.SerializeToUtf8Bytes(signal); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        try
        {
            using var aes = new AesGcm(shared, 16); aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(Id + friend.Id));
            return JsonSerializer.Serialize(new Envelope(Id, Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher)));
        }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }
    public (FriendCard Friend, CallSignal Signal)? Open(string message)
    {
        if (message.Length > 4096) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(message); if (envelope is null) return null;
            FriendCard? friend; lock (sync) friend = Friends.FirstOrDefault(f => f.Id == envelope.F); if (friend is null) return null;
            var shared = SharedKey(friend);
            try
            {
                var cipher = Convert.FromBase64String(envelope.C); var plain = new byte[cipher.Length];
                using var aes = new AesGcm(shared, 16);
                aes.Decrypt(Convert.FromBase64String(envelope.N), cipher, Convert.FromBase64String(envelope.T), plain, Encoding.ASCII.GetBytes(friend.Id + Id));
                var signal = JsonSerializer.Deserialize<CallSignal>(plain);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (signal is null || !Guid.TryParse(signal.Id, out _) || signal.Time < now - 90 || signal.Time > now + 90 || signal.Kind is not ("offer" or "answer" or "reject" or "cancel" or "busy")) return null;
                return (friend, signal);
            }
            finally { CryptographicOperations.ZeroMemory(shared); }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException) { return null; }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var secret = key.ExportPkcs8PrivateKey();
        try
        {
            var protectedKey = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
            var data = JsonSerializer.Serialize(new Stored(inbox, Convert.ToBase64String(protectedKey), Friends.ToArray()));
            File.WriteAllText(path + ".tmp", data); File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public void Dispose() { lock (sync) key.Dispose(); }
    private sealed record Stored(string Inbox, string ProtectedKey, FriendCard[] Friends);
    private sealed record Envelope(string F, string N, string T, string C);
}

public sealed record CallSignal(string Kind, string Id, long Time, string? Code = null)
{
    public static CallSignal Create(string kind, string id, string? code = null) => new(kind, id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), code);
}
