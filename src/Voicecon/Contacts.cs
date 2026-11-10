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
            if (card.Name.Length is < 1 or > 48 || !ContactBook.ValidInbox(card.Inbox)) throw new FormatException();
            if (card.Id != ContactBook.KeyId(card.PublicKey)) throw new FormatException();
            using var key = ECDiffieHellman.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(card.PublicKey), out _);
            if (key.KeySize != 256 || !Uri.TryCreate(card.Server, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new FormatException();
            return card with { Name = new string(card.Name.Where(c => !char.IsControl(c)).ToArray()), Server = card.Server.TrimEnd('/') };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new FormatException("Не удалось прочитать код друга. Нужен полный код VCF1.…", ex); }
    }
}

/// <summary>contacts.json exists but cannot be used: damaged JSON, or a key protected for another Windows account.</summary>
/// Deliberately not an IOException: callers treat IOException as transient and leave the file alone.
public sealed class ContactBookCorruptException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ContactBook : IDisposable
{
    private readonly ECDiffieHellman key;
    private readonly string path;
    private readonly string inbox;
    private readonly object sync = new();
    // Records that no longer pass validation are kept verbatim so a later version (or a manual fix) can recover them.
    private readonly List<FriendCard> unreadable = [];
    public string Id { get; }
    public string PublicKey { get; }
    public List<FriendCard> Friends { get; } = [];
    public int UnreadableFriends { get { lock (sync) return unreadable.Count; } }
    /// <exception cref="ContactBookCorruptException">The file exists but its contents or key cannot be used.</exception>
    /// <exception cref="IOException">The file could not be read right now; it is left untouched.</exception>
    public ContactBook(string? directory = null)
    {
        path = PathFor(directory);
        key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            if (File.Exists(path))
            {
                var data = Read(path, key);
                inbox = data.Inbox;
                foreach (var card in data.Friends ?? [])
                {
                    if (card is null) continue;
                    try { var friend = FriendCard.Decode(card.Encode()); Friends.RemoveAll(f => f.Id == friend.Id); Friends.Add(friend); }
                    catch (FormatException) { unreadable.Add(card); }
                }
            }
            else inbox = "vc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()); Id = KeyId(PublicKey);
            if (!File.Exists(path)) Save();
        }
        catch { key.Dispose(); throw; }
    }
    private static string PathFor(string? directory) => Path.Combine(directory ?? Path.GetDirectoryName(Settings.FilePath)!, "contacts.json");
    private static Stored Read(string path, ECDiffieHellman key)
    {
        var text = File.ReadAllText(path); // IOException here is transient and propagates unchanged.
        Stored? data;
        try { data = JsonSerializer.Deserialize<Stored>(text); }
        catch (JsonException ex) { throw new ContactBookCorruptException("Файл contacts.json повреждён.", ex); }
        if (data is null || string.IsNullOrEmpty(data.ProtectedKey) || !ValidInbox(data.Inbox)) throw new ContactBookCorruptException("Файл contacts.json повреждён или неполон.");
        byte[] secret;
        try { secret = ProtectedData.Unprotect(Convert.FromBase64String(data.ProtectedKey), null, DataProtectionScope.CurrentUser); }
        catch (FormatException ex) { throw new ContactBookCorruptException("Файл contacts.json повреждён: ключ записан неверно.", ex); }
        catch (CryptographicException ex) { throw new ContactBookCorruptException("Ключ контактов не расшифровывается. Обычно так бывает, если файл перенесён с другого компьютера или из другой учётной записи Windows.", ex); }
        try
        {
            key.ImportPkcs8PrivateKey(secret, out _);
            if (key.KeySize != 256) throw new CryptographicException("Unexpected key size");
        }
        catch (CryptographicException ex) { throw new ContactBookCorruptException("Файл contacts.json содержит неверный ключ.", ex); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        return data;
    }
    /// <summary>Moves an unusable contacts.json aside (never deletes it) so a new profile can be created. Returns the backup path.</summary>
    public static string PreserveCorrupt(string? directory = null)
    {
        var source = PathFor(directory);
        var folder = Path.GetDirectoryName(source)!;
        var backup = Path.Combine(folder, $"contacts.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (File.Exists(backup)) backup = Path.Combine(folder, $"contacts.broken-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.Move(source, backup);
        return backup;
    }
    public static bool ValidInbox(string? inbox) => inbox is { Length: 39 } && inbox.StartsWith("vc-", StringComparison.Ordinal) && inbox[3..].All(char.IsAsciiHexDigit);
    public static string KeyId(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey))).ToLowerInvariant()[..32];
    public FriendCard OwnCard(string name, string server) => new(Id, name.Trim()[..Math.Min(name.Trim().Length, 48)], inbox, PublicKey, server.TrimEnd('/'));
    public void Add(FriendCard friend)
    {
        if (friend.Id == Id) throw new InvalidOperationException("Это ваш собственный код.");
        lock (sync) { Friends.RemoveAll(f => f.Id == friend.Id); unreadable.RemoveAll(f => f.Id == friend.Id); Friends.Add(friend); Save(); }
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
            var data = JsonSerializer.Serialize(new Stored(inbox, Convert.ToBase64String(protectedKey), [.. Friends, .. unreadable]));
            File.WriteAllText(path + ".tmp", data); File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public void Dispose() { lock (sync) key.Dispose(); }
    private sealed record Stored(string Inbox, string ProtectedKey, FriendCard?[]? Friends);
    private sealed record Envelope(string F, string N, string T, string C);
}

public sealed record CallSignal(string Kind, string Id, long Time, string? Code = null)
{
    public static CallSignal Create(string kind, string id, string? code = null) => new(kind, id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), code);
}
