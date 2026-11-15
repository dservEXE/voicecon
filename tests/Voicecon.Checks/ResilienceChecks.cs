using System.Text.Json.Nodes;
using Voicecon;

/// <summary>Damaged or locked profile files must never silently destroy contacts or settings.</summary>
internal static class ResilienceChecks
{
    private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }

    public static void Check()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoiceconChecks-" + Guid.NewGuid().ToString("N"));
        AppLog.UseDirectory(Path.Combine(root, "logs"));
        try
        {
            CheckUnreadableContact(root);
            CheckCorruptContacts(root);
            CheckForeignKey(root);
            CheckLockedContacts(root);
            CheckSettings(root);
            CheckLog(root);
            Console.WriteLine("PASS unreadable contacts kept, corrupt/foreign contacts preserved before reset, locked file untouched, settings backup, local log rotation");
        }
        finally
        {
            AppLog.UseDirectory(null);
            var path = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(temp + "VoiceconChecks-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static FriendCard Card(string root, string name)
    {
        using var book = new ContactBook(Path.Combine(root, "friend-" + name));
        return book.OwnCard(name, "https://ntfy.sh");
    }

    private static void CheckUnreadableContact(string root)
    {
        var folder = Path.Combine(root, "unreadable"); var file = Path.Combine(folder, "contacts.json");
        var bob = Card(root, "Bob"); var carol = Card(root, "Carol"); var dave = Card(root, "Dave");
        string id;
        using (var book = new ContactBook(folder)) { book.Add(bob); book.Add(carol); id = book.Id; }
        // Simulate a record that a newer validation rule no longer accepts.
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var friend in json["Friends"]!.AsArray()) if (friend!["Name"]!.GetValue<string>() == "Carol") friend["Server"] = "http://insecure.example";
        json["Friends"]!.AsArray().Add(null);
        File.WriteAllText(file, json.ToJsonString());
        using (var book = new ContactBook(folder))
        {
            Require(book.Id == id, "Identity survives one unreadable contact");
            Require(book.Friends.Count == 1 && book.Friends[0].Id == bob.Id && book.UnreadableFriends == 1, "Unreadable contact is skipped, others load");
            book.Add(dave);
        }
        var stored = JsonNode.Parse(File.ReadAllText(file))!["Friends"]!.AsArray();
        Require(stored.Count == 3 && stored.Any(f => f!["Name"]!.GetValue<string>() == "Carol"), "Unreadable contact is preserved on save");
        using (var book = new ContactBook(folder))
        {
            book.Add(carol);
            Require(book.UnreadableFriends == 0 && book.Friends.Count == 3, "Re-adding a contact replaces its unreadable record");
        }
    }

    private static void CheckCorruptContacts(string root)
    {
        var folder = Path.Combine(root, "corrupt"); Directory.CreateDirectory(folder); var file = Path.Combine(folder, "contacts.json");
        File.WriteAllText(file, "{ not json");
        var rejected = false; try { using var _ = new ContactBook(folder); } catch (ContactBookCorruptException) { rejected = true; }
        Require(rejected && File.ReadAllText(file) == "{ not json", "Corrupt contacts are reported, not overwritten");
        var backup = ContactBook.PreserveCorrupt(folder);
        Require(!File.Exists(file) && File.ReadAllText(backup) == "{ not json" && Path.GetDirectoryName(backup) == folder, "Corrupt contacts are moved aside, not deleted");
        using var fresh = new ContactBook(folder);
        Require(File.Exists(file) && fresh.Friends.Count == 0, "A new profile can be created after preserving the old one");
        File.WriteAllText(Path.Combine(folder, "contacts.json"), "{}");
        rejected = false; try { using var _ = new ContactBook(folder); } catch (ContactBookCorruptException) { rejected = true; }
        Require(rejected, "Incomplete contacts file is reported as corrupt");
    }

    private static void CheckForeignKey(string root)
    {
        var folder = Path.Combine(root, "foreign"); var file = Path.Combine(folder, "contacts.json");
        using (new ContactBook(folder)) { }
        // A key protected for another Windows account cannot be unprotected here.
        var json = JsonNode.Parse(File.ReadAllText(file))!; json["ProtectedKey"] = Convert.ToBase64String(new byte[200]);
        var original = json.ToJsonString(); File.WriteAllText(file, original);
        var error = (Exception?)null; try { using var _ = new ContactBook(folder); } catch (Exception ex) { error = ex; }
        Require(error is ContactBookCorruptException && error.Message.Contains("учётной записи") && File.ReadAllText(file) == original, "Foreign DPAPI key is explained and left intact");
    }

    private static void CheckLockedContacts(string root)
    {
        var folder = Path.Combine(root, "locked"); var file = Path.Combine(folder, "contacts.json");
        using (new ContactBook(folder)) { }
        var before = File.ReadAllBytes(file);
        var error = (Exception?)null;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        { try { using var _ = new ContactBook(folder); } catch (Exception ex) { error = ex; } }
        Require(error is IOException && error is not ContactBookCorruptException, "Locked file is a transient error, not corruption");
        Require(File.ReadAllBytes(file).SequenceEqual(before), "Locked contacts file is untouched");
    }

    private static void CheckSettings(string root)
    {
        var folder = Path.Combine(root, "settings"); Directory.CreateDirectory(folder); var file = Path.Combine(folder, "settings.json");
        Require(Settings.Load(file) is { RecoveredFrom: null, Name: "Игрок" }, "Missing settings use defaults without a backup");
        File.WriteAllText(file, "{\"ProfileDefaultsVersion\":1,\"Name\":\"Команда\",\"Volume\":180}");
        Require(Settings.Load(file) is { RecoveredFrom: null, Name: "Команда", Volume: 180 }, "Valid settings load without a backup");
        File.WriteAllText(file, "{\"Name\":\"Команда\",\"Volume\":");
        var recovered = Settings.Load(file);
        Require(recovered.Name == "Игрок" && recovered.RecoveredFrom is { } backup && File.ReadAllText(backup) == "{\"Name\":\"Команда\",\"Volume\":", "Corrupt settings are copied aside before defaults are used");
        Require(Settings.Parse("{ broken") is { Name: "Игрок" }, "Parse keeps returning defaults for broken JSON");

        // The name must survive everything short of the person changing it.
        Require(Settings.Parse("{\"ProfileDefaultsVersion\":2,\"Name\":\"Макс\",\"Volume\":\"много\",\"Theme\":null,\"MicrophoneGain\":150}") is { Name: "Макс", Volume: 100, Theme: "graphite", MicrophoneGain: 150 },
            "One damaged field keeps the name and every other field");
        Require(Settings.Parse("{\"ProfileDefaultsVersion\":2,\"Name\":\"  \"}").Name == "Игрок" && Settings.Parse("{\"ProfileDefaultsVersion\":2,\"Name\":\"Макс\\u0007\"}").Name == "Макс", "Blank names fall back, control characters are dropped");
        var saves = Path.Combine(root, "saves"); Directory.CreateDirectory(saves); var saved = Path.Combine(saves, "settings.json");
        new Settings { Name = "Макс", Volume = 150 }.Save(saved); new Settings { Name = "Макс", Volume = 90 }.Save(saved);
        Require(File.Exists(Settings.PreviousPath(saved)) && Settings.Load(Settings.PreviousPath(saved)) is { Name: "Макс", Volume: 150 } && Settings.Load(saved) is { Volume: 90 }, "Each save keeps the previous copy");
        File.WriteAllText(saved, "{\"ProfileDefaultsVersion\":2,\"Name\":");
        Require(Settings.Load(saved) is { Name: "Макс", Volume: 150, RecoveredFrom: not null }, "A damaged file falls back to the previous copy, not to defaults");
        File.Delete(saved);
        Require(Settings.Load(saved) is { Name: "Макс", Volume: 150 }, "A save interrupted between renames restores the previous copy");
        var busy = Path.Combine(root, "busy"); Directory.CreateDirectory(busy); var busyFile = Path.Combine(busy, "settings.json");
        new Settings { Name = "Макс" }.Save(busyFile);
        var locked = new FileStream(busyFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(400); locked.Dispose(); });
        Require(Settings.Load(busyFile) is { Name: "Макс", RecoveredFrom: null }, "A file busy for a moment is read again, not replaced by defaults");
        release.Wait();
        var lost = Path.Combine(root, "lost"); Directory.CreateDirectory(lost); var lostFile = Path.Combine(lost, "settings.json");
        File.WriteAllText(lostFile, "{\"ProfileDefaultsVersion\":2,\"Name\":\"Игрок\"}");
        File.WriteAllText(Path.Combine(lost, "settings.broken-20261110-120000.json"), "{\"ProfileDefaultsVersion\":2,\"Name\":\"max19\",\"Volume\":");
        File.WriteAllText(Path.Combine(lost, "settings.broken-20261101-120000.json"), "{\"Name\":\"Legacy Person\"}");
        var restored = Settings.Load(lostFile);
        Require(restored is { Name: "max19", NameRecoveryChecked: true }, "A name lost to the default is found in an older copy of the settings");
        File.WriteAllText(lostFile, "{\"ProfileDefaultsVersion\":2,\"Name\":\"Игрок\",\"NameRecoveryChecked\":true}");
        Require(Settings.Load(lostFile).Name == "Игрок", "The recovery runs once, so a later choice of the default name stays");
        File.Delete(Path.Combine(lost, "settings.broken-20261110-120000.json")); File.WriteAllText(lostFile, "{\"ProfileDefaultsVersion\":2,\"Name\":\"Игрок\"}");
        Require(Settings.Load(lostFile).Name == "Игрок", "The pre-0.6 shared legacy name is never brought back");
    }

    private static void CheckLog(string root)
    {
        AppLog.Error("Синтетическая ошибка проверки", new InvalidOperationException("synthetic"));
        var text = File.ReadAllText(AppLog.FilePath);
        Require(text.Contains("Синтетическая ошибка проверки") && text.Contains("InvalidOperationException"), "Errors reach the local log");
        File.WriteAllText(AppLog.FilePath, new string('x', (int)AppLog.MaximumSize + 10));
        AppLog.Info("после ротации");
        Require(File.Exists(AppLog.FilePath + ".1") && new FileInfo(AppLog.FilePath).Length < 1024, "Log rotates at the size limit");
        Require(AppLog.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Checks never write to the user's log");
    }
}
