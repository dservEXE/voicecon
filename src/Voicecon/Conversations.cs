using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voicecon;

/// <summary>A saved set of friends to call together. Only the group name and member ids are stored.</summary>
public sealed record ChatGroup(string Id, string Name, string[] Members, DateTimeOffset Created);

public sealed class GroupBook
{
    private readonly string path;
    public List<ChatGroup> Groups { get; } = [];
    public GroupBook(string? directory = null)
    {
        path = Path.Combine(directory ?? Path.GetDirectoryName(Settings.FilePath)!, "groups.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ChatGroup[]>(File.ReadAllText(path)) is { } stored)
                foreach (var group in stored)
                    if (group is not null && Guid.TryParseExact(group.Id, "N", out _) && !string.IsNullOrWhiteSpace(group.Name))
                        Groups.Add(group with { Name = Clean(group.Name), Members = (group.Members ?? []).Where(m => m is { Length: 32 }).Distinct().ToArray() });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { AppLog.Error("Не удалось прочитать группы", ex); }
    }
    public static string Clean(string name) => new string(name.Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim() is { Length: > 0 } clean ? clean : "Группа";
    public ChatGroup? Find(string id) => Groups.FirstOrDefault(g => g.Id == id);
    public ChatGroup Create(string name, IEnumerable<string> members)
    {
        var group = new ChatGroup(Guid.NewGuid().ToString("N"), Clean(name), members.Distinct().ToArray(), DateTimeOffset.UtcNow);
        Groups.Add(group); Save(); return group;
    }
    /// <summary>Remembers a group someone else called us into, so its chat history has a home in the sidebar.</summary>
    public ChatGroup Upsert(string id, string name, IEnumerable<string> knownMembers)
    {
        var existing = Find(id);
        var members = (existing?.Members ?? []).Concat(knownMembers).Distinct().ToArray();
        var group = existing is null ? new ChatGroup(id, Clean(name), members, DateTimeOffset.UtcNow) : existing with { Members = members };
        Groups.RemoveAll(g => g.Id == id); Groups.Add(group); Save(); return group;
    }
    public void Update(ChatGroup group) { Groups.RemoveAll(g => g.Id == group.Id); Groups.Add(group with { Name = Clean(group.Name) }); Save(); }
    public void Remove(string id) { Groups.RemoveAll(g => g.Id == id); Save(); }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Groups.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("Не удалось сохранить группы", ex); }
    }
}

public sealed record ChatRecord(string Id, string Name, string Text, bool Mine, DateTimeOffset Time);

/// <summary>
/// Local, encrypted chat history. One append-only file per conversation; each line is one AES-GCM record
/// bound to its conversation. The key is protected by Windows DPAPI for the current user.
/// When the total size passes the limit (300 MB by default) the oldest messages across all conversations are dropped.
/// </summary>
public sealed class ChatStore
{
    public const long DefaultLimit = 300L * 1024 * 1024;
    private readonly string directory;
    private readonly byte[] key;
    private readonly long limit;
    private long total;
    public long TotalBytes => total;
    public long Limit => limit;

    public ChatStore(string? directory = null, long limit = DefaultLimit)
    {
        this.directory = directory ?? Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "chats");
        this.limit = Math.Max(4096, limit);
        Directory.CreateDirectory(this.directory);
        key = LoadKey();
        total = Files().Sum(f => f.Length);
    }

    private byte[] LoadKey()
    {
        var keyPath = Path.Combine(directory, "key.bin");
        if (File.Exists(keyPath))
        {
            try { return ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException ex)
            {
                // History from another Windows account cannot be read here; keep it aside and start fresh.
                AppLog.Error("Ключ истории чата не расшифровывается, история перенесена в резервную папку", ex);
                var aside = directory + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.Move(directory, aside); Directory.CreateDirectory(directory);
            }
        }
        var fresh = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, ProtectedData.Protect(fresh, null, DataProtectionScope.CurrentUser));
        return fresh;
    }

    private IEnumerable<FileInfo> Files() => new DirectoryInfo(directory).EnumerateFiles("*.vch");
    private string FileFor(string conversation) => Path.Combine(directory, Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(conversation)))[..32].ToLowerInvariant() + ".vch");

    public void Append(string conversation, ChatRecord record)
    {
        var line = Seal(conversation, record) + "\n"; var bytes = Encoding.ASCII.GetBytes(line);
        using (var stream = new FileStream(FileFor(conversation), FileMode.Append, FileAccess.Write, FileShare.Read)) stream.Write(bytes);
        total += bytes.Length;
        if (total > limit) Trim();
    }

    public IReadOnlyList<ChatRecord> LoadRecent(string conversation, int count)
    {
        var file = FileFor(conversation);
        if (!File.Exists(file) || count <= 0) return [];
        var lines = TailLines(file, count);
        var result = new List<ChatRecord>(lines.Count);
        foreach (var line in lines) if (Open(conversation, line) is { } record) result.Add(record);
        return result;
    }

    public int Count(string conversation)
    {
        var file = FileFor(conversation); if (!File.Exists(file)) return 0;
        var count = 0; using var reader = new StreamReader(file, Encoding.ASCII); while (reader.ReadLine() is not null) count++; return count;
    }

    public void Clear()
    {
        foreach (var file in Files()) try { file.Delete(); } catch (IOException) { }
        total = Files().Sum(f => f.Length);
    }

    // Line format: "<unix ms>.<base64 nonce|tag|ciphertext>". The timestamp is bound to the record through the AAD,
    // so trimming can find the oldest messages without decrypting anything.
    private string Seal(string conversation, ChatRecord record)
    {
        var time = record.Time.ToUnixTimeMilliseconds();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Stored(record.Id, record.Name, record.Text, record.Mine, time));
        var output = new byte[12 + 16 + plain.Length]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Aad(conversation, time));
        return time.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + Convert.ToBase64String(output);
    }
    private static byte[] Aad(string conversation, long time) => Encoding.UTF8.GetBytes(conversation + "|" + time.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static bool TryTime(string line, out long time)
    {
        time = 0; var dot = line.IndexOf('.');
        return dot > 0 && long.TryParse(line.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out time);
    }

    private ChatRecord? Open(string conversation, string line)
    {
        try
        {
            line = line.Trim(); if (!TryTime(line, out var time)) return null;
            var data = Convert.FromBase64String(line[(line.IndexOf('.') + 1)..]); if (data.Length < 29) return null;
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Aad(conversation, time));
            var stored = JsonSerializer.Deserialize<Stored>(plain);
            return stored is null || stored.T != time ? null : new ChatRecord(stored.I, stored.N, stored.X, stored.M, DateTimeOffset.FromUnixTimeMilliseconds(stored.T).ToLocalTime());
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException) { return null; }
    }

    private static List<string> TailLines(string file, int count)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var position = stream.Length; var collected = new List<byte[]>(); var newlines = 0; var block = new byte[65536];
        while (position > 0 && newlines <= count)
        {
            var size = (int)Math.Min(block.Length, position); position -= size; stream.Position = position;
            stream.ReadExactly(block, 0, size);
            for (var i = 0; i < size; i++) if (block[i] == (byte)'\n') newlines++;
            collected.Insert(0, block[..size]);
        }
        var text = Encoding.ASCII.GetString(collected.SelectMany(b => b).ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // When reading started mid-file, the first fragment may be a partial line.
        var start = position > 0 ? 1 : 0;
        return lines.Skip(Math.Max(start, lines.Length - count)).ToList();
    }

    /// <summary>Drops the oldest messages (across all conversations) until the store is at 95% of the limit.</summary>
    private void Trim()
    {
        var target = (long)(limit * 0.95);
        var guard = 0;
        while (total > target && guard++ < 10_000)
        {
            FileInfo? oldest = null; var oldestTime = long.MaxValue;
            foreach (var file in Files())
            {
                var time = FirstTime(file.FullName);
                if (time < oldestTime) { oldestTime = time; oldest = file; }
            }
            if (oldest is null) break;
            var excess = total - target;
            // Small steps keep the global "oldest first" order across conversations.
            var removed = DropHead(oldest.FullName, Math.Clamp(excess, 4096, 1024 * 1024));
            if (removed <= 0) break;
            total -= removed;
        }
        total = Files().Sum(f => f.Length);
    }

    /// <summary>Earliest timestamp in a file (its first line). Malformed lines count as oldest so they go first.</summary>
    private static long FirstTime(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.ASCII);
            var line = reader.ReadLine();
            return line is not null && TryTime(line, out var time) ? time : long.MinValue;
        }
        catch (IOException) { return long.MinValue; }
    }

    /// <summary>Removes whole lines from the start of a file totalling at least <paramref name="bytes"/>. Returns bytes removed.</summary>
    private static long DropHead(string file, long bytes)
    {
        var info = new FileInfo(file);
        if (info.Length <= bytes) { var length = info.Length; info.Delete(); return length; }
        long cut;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Position = bytes; int b;
            while ((b = stream.ReadByte()) >= 0 && b != '\n') { }
            cut = stream.Position;
            if (cut >= stream.Length) { stream.Dispose(); var length = info.Length; info.Delete(); return length; }
            var temp = file + ".tmp";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write)) stream.CopyTo(output);
        }
        File.Move(file + ".tmp", file, true);
        return cut;
    }

    private sealed record Stored(string I, string N, string X, bool M, long T);
}
