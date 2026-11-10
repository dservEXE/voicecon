using System.Text;
using Voicecon;

/// <summary>Chat history storage (encryption, ordering, 300 MB-style limit), groups, presence and settings migration. Disk-only, no network.</summary>
internal static class ConversationChecks
{
    private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }

    public static void Check()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoiceconChecks-" + Guid.NewGuid().ToString("N"));
        AppLog.UseDirectory(Path.Combine(root, "logs"));
        try
        {
            CheckChatStore(root);
            CheckLimit(root);
            CheckGroups(root);
            CheckPresence();
            CheckSettingsMigration();
            Console.WriteLine("PASS encrypted chat history: per-conversation roundtrip, tamper/foreign-conversation rejection, tail reads, oldest-first trimming under the size limit; groups; presence topics, freshness and offline; settings migration (empty hotkeys, gaming mode, animation level)");
        }
        finally
        {
            AppLog.UseDirectory(null);
            var path = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(temp + "VoiceconChecks-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static void CheckChatStore(string root)
    {
        var folder = Path.Combine(root, "chats");
        var store = new ChatStore(folder);
        var start = DateTimeOffset.Now.AddMinutes(-10);
        for (var i = 0; i < 25; i++) store.Append("f:alice", new ChatRecord(Guid.NewGuid().ToString("N"), i % 2 == 0 ? "Алиса" : "Вы", $"Сообщение {i} 😎", i % 2 == 1, start.AddSeconds(i)));
        store.Append("g:team", new ChatRecord("x", "Боб", "В группе", false, start));
        var recent = store.LoadRecent("f:alice", 10);
        Require(recent.Count == 10 && recent[^1].Text == "Сообщение 24 😎" && recent[0].Text == "Сообщение 15 😎" && recent[^1].Mine == false && recent[^2].Mine, "Tail read returns the newest messages in order");
        Require(store.LoadRecent("g:team", 50).Single().Text == "В группе", "Conversations are separate");
        Require(store.LoadRecent("f:nobody", 5).Count == 0, "Unknown conversation is empty");
        var files = Directory.GetFiles(folder, "*.vch");
        Require(files.All(f => !Path.GetFileName(f).Contains("alice") && !File.ReadAllText(f).Contains("Сообщение") && !File.ReadAllText(f, Encoding.UTF8).Contains("Алиса")), "History is encrypted and file names hide conversation ids");
        // Moving a record into another conversation file must fail authentication.
        var aliceFile = files.OrderByDescending(f => new FileInfo(f).Length).First(); var teamFile = files.First(f => f != aliceFile);
        File.AppendAllText(teamFile, File.ReadLines(aliceFile).First() + "\n");
        Require(store.LoadRecent("g:team", 50).Count == 1, "Record copied from another conversation is rejected");
        var reopened = new ChatStore(folder);
        Require(reopened.LoadRecent("f:alice", 100).Count == 25 && reopened.TotalBytes > 0, "History survives restart with the DPAPI-protected key");
        reopened.Clear(); Require(reopened.LoadRecent("f:alice", 5).Count == 0 && reopened.TotalBytes == 0, "Clear removes local history");
    }

    private static void CheckLimit(string root)
    {
        var folder = Path.Combine(root, "limited");
        var store = new ChatStore(folder, 64 * 1024);
        var start = DateTimeOffset.Now.AddDays(-2); var text = new string('ж', 300);
        // Old conversation first, then a newer one; the oldest messages must go first.
        for (var i = 0; i < 80; i++) store.Append("f:old", new ChatRecord(i.ToString(), "Старый", text + i, false, start.AddMinutes(i)));
        for (var i = 0; i < 80; i++) store.Append("f:new", new ChatRecord(i.ToString(), "Новый", text + i, false, start.AddDays(1).AddMinutes(i)));
        Require(store.TotalBytes <= store.Limit, $"Store stays under its limit ({store.TotalBytes} > {store.Limit})");
        var newest = store.LoadRecent("f:new", 1000);
        Require(newest.Count > 0 && newest[^1].Text.EndsWith("79"), "Newest messages are kept");
        var old = store.LoadRecent("f:old", 1000);
        Require(old.Count < 80 && (old.Count == 0 || old[0].Time > start), "Oldest messages are dropped first");
        Require(newest.Count >= old.Count, "Newer conversation keeps at least as much as the older one");
    }

    private static void CheckGroups(string root)
    {
        var folder = Path.Combine(root, "groups");
        var book = new GroupBook(folder); var a = new string('a', 32); var b = new string('b', 32);
        var group = book.Create("  Рейд\u0007  ", [a, b, a]);
        Require(group.Name == "Рейд" && group.Members.Length == 2, "Group names are cleaned and members de-duplicated");
        var reloaded = new GroupBook(folder); Require(reloaded.Find(group.Id)?.Members.SequenceEqual([a, b]) == true, "Groups persist");
        var invited = reloaded.Upsert(Guid.NewGuid().ToString("N"), "Чужая группа", [a]); Require(new GroupBook(folder).Find(invited.Id)?.Name == "Чужая группа", "Group from an incoming call is remembered");
        reloaded.Remove(group.Id); Require(new GroupBook(folder).Find(group.Id) is null, "Group removal persists");
    }

    private static void CheckPresence()
    {
        var inbox = "vc-" + new string('1', 36);
        var topic = PresenceService.TopicFor(inbox);
        Require(topic.Length == 39 && topic.StartsWith("vp-") && topic != PresenceService.TopicFor("vc-" + new string('2', 36)) && !topic.Contains(inbox[3..]), "Presence topic is derived from the private inbox, not equal to it");
        var now = DateTimeOffset.UtcNow;
        Require(PresenceService.Evaluate("on", now.AddMinutes(-5).ToUnixTimeSeconds(), now).Status == PresenceStatus.Online, "Fresh heartbeat is online");
        Require(PresenceService.Evaluate("call", now.ToUnixTimeSeconds(), now).Status == PresenceStatus.InCall, "In-call state");
        Require(PresenceService.Evaluate("on", now.AddMinutes(-50).ToUnixTimeSeconds(), now).Status == PresenceStatus.Offline, "Stale heartbeat counts as offline");
        Require(PresenceService.Evaluate("off", now.ToUnixTimeSeconds(), now).Status == PresenceStatus.Offline, "Explicit offline");
        using var keys = new System.Security.Cryptography.ECDiffieHellmanCng(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(keys.ExportSubjectPublicKeyInfo());
        var friend = new FriendCard(ContactBook.KeyId(publicKey), "Друг", inbox, publicKey, "https://ntfy.invalid");
        using var service = new PresenceService(friend with { Inbox = "vc-" + new string('3', 36) });
        service.SetFriends([friend], subscribe: false);
        var changed = new List<string>(); service.Changed += changed.Add;
        string Event(string message) => System.Text.Json.JsonSerializer.Serialize(new { @event = "message", topic, message });
        Require(service.Handle(Encoding.UTF8.GetBytes(Event($"{{\"V\":1,\"S\":\"on\",\"T\":{now.ToUnixTimeSeconds()}}}"))) && service.Get(friend.Id).Status == PresenceStatus.Online && changed.Count == 1, "Presence message updates the friend");
        Require(!service.Handle(Encoding.UTF8.GetBytes(Event($"{{\"V\":1,\"S\":\"off\",\"T\":{now.AddMinutes(-1).ToUnixTimeSeconds()}}}"))) && service.Get(friend.Id).Status == PresenceStatus.Online, "Older message cannot override a newer one");
        Require(!service.Handle(Encoding.UTF8.GetBytes(Event("{\"V\":1,\"S\":\"hacked\",\"T\":1}"))) && !service.Handle("not json"u8.ToArray()), "Malformed presence is ignored");
        service.SetFriends([], subscribe: false); service.Dispose();
    }

    private static void CheckSettingsMigration()
    {
        var old = Settings.Parse($"{{\"ProfileDefaultsVersion\":1,\"Name\":\"Игрок 2\",\"MicrophoneHotkey\":{(int)(Keys.Control | Keys.Shift | Keys.M)},\"HeadphonesHotkey\":{(int)Keys.G},\"Animations\":false,\"AudioMode\":0}}");
        Require(old.MicrophoneHotkey == 0 && old.HeadphonesHotkey == (int)Keys.G, "Old factory shortcut cleared, user's own shortcut kept");
        Require(old.AnimationLevel == "off" && old.AudioMode == VoiceMode.Normal && old.Name == "Игрок 2", "Animation choice and explicit voice mode survive migration");
        var fresh = new Settings();
        Require(fresh.MicrophoneHotkey == 0 && fresh.HeadphonesHotkey == 0 && fresh.OpenWindowHotkey == 0 && fresh.ScreenHotkey == 0 && fresh.CameraHotkey == 0, "No default shortcuts");
        Require(fresh.AudioMode == VoiceMode.Gaming && fresh.ShareMode == "optimized" && fresh.ShowOnlineStatus, "Gaming mode and optimized sharing by default");
        Require(Settings.Parse("{\"ProfileDefaultsVersion\":2,\"CustomFps\":7,\"CustomBitrateKbps\":999999,\"ShareMode\":\"x\"}") is { CustomFps: 60, CustomBitrateKbps: 20000, ShareMode: "optimized" }, "Share settings are validated");
    }
}
