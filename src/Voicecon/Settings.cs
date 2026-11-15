using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Voicecon;

public enum VoiceMode { Normal, Gaming, Economy }

public sealed class Settings
{
    public string Name { get; set; } = "Игрок";
    public const int CurrentDefaultsVersion = 2;
    public int ProfileDefaultsVersion { get; set; } = CurrentDefaultsVersion;
    public bool CheckUpdates { get; set; } = true;
    public string DismissedUpdate { get; set; } = "";
    public DateTimeOffset LastUpdateCheckUtc { get; set; }
    public string Theme { get; set; } = "graphite";
    public bool Animations { get; set; } = true;
    /// <summary>full · minimal · off</summary>
    public string AnimationLevel { get; set; } = "full";
    public bool ShowOnlineStatus { get; set; } = true;
    public bool MessageSounds { get; set; } = true;
    public bool ShowMetrics { get; set; } = true;
    /// <summary>Soft drifting colour pools on the window canvas.</summary>
    public bool BackgroundGlow { get; set; } = true;
    public string LastConversation { get; set; } = "";
    /// <summary>Chat history size on this PC, 100-500 MB; the oldest messages go first.</summary>
    public int ChatLimitMb { get; set; } = 300;
    public const int ChatLimitMin = 100, ChatLimitMax = 500;
    /// <summary>Newest delivery event already received, so offline messages are fetched once.</summary>
    public long MailboxSince { get; set; }
    /// <summary>Screen sharing: optimized · quality · custom.</summary>
    public string ShareMode { get; set; } = "optimized";
    public int CustomFps { get; set; } = 60;
    public int CustomResolution { get; set; } = 1080;
    public int CustomBitrateKbps { get; set; } = 6000;
    /// <summary>Camera: optimized · quality.</summary>
    public string CameraMode { get; set; } = "optimized";
    public bool StartWithWindows { get; set; } = true;
    public string Ringtone { get; set; } = "boris";
    public string RingtoneFile { get; set; } = "";
    public bool NotificationSounds { get; set; } = true;
    public string SignalingServer { get; set; } = "https://ntfy.sh";
    // No default shortcuts: every field starts empty and is assigned by the user.
    public int MicrophoneHotkey { get; set; }
    public int HeadphonesHotkey { get; set; }
    public int OpenWindowHotkey { get; set; }
    public int ScreenHotkey { get; set; }
    public int CameraHotkey { get; set; }
    public int VideoFps { get; set; } = 30;
    public int VideoResolution { get; set; } = 720;
    public bool ShareSound { get; set; }
    public string VideoQuality { get; set; } = "eco";
    public string Microphone { get; set; } = "";
    public string Speakers { get; set; } = "";
    public bool PushToTalk { get; set; }
    public int TalkKey { get; set; } = 0x56;
    public bool NoiseGate { get; set; } = true;
    public bool NoiseSuppression { get; set; } = true;
    public VoiceMode AudioMode { get; set; } = VoiceMode.Gaming;
    public string LastFriendId { get; set; } = "";
    public int Volume { get; set; } = 100;
    public int MicrophoneGain { get; set; } = 100;
    public string StunUrl { get; set; } = "stun:stun.cloudflare.com:3478";
    public string TurnUrl { get; set; } = "";
    public string ConnectionRoute { get; set; } = "auto";
    public string TurnUser { get; set; } = "";
    public string ProtectedTurnPassword { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string TurnPassword { get; set; } = "";

    public const string DefaultName = "Игрок";
    /// <summary>Set once the name has been checked against older copies of the settings (see <see cref="RecoverName"/>).</summary>
    public bool NameRecoveryChecked { get; set; }

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voicecon", "settings.json");
    /// <summary>The previous good copy, kept by every save.</summary>
    public static string PreviousPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, "settings.previous.json");

    /// <summary>Path of the backup made when the last load had to fall back to defaults; null if the file was fine.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RecoveredFrom { get; private set; }

    public static Settings Load() => Load(FilePath);
    public static Settings Load(string path)
    {
        var value = LoadFile(path);
        RecoverName(value, path);
        return value;
    }
    private static Settings LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            // A save interrupted between its two renames leaves only the previous copy.
            return File.Exists(PreviousPath(path)) && Read(PreviousPath(path)) is { } previousJson && TryParse(previousJson) is { } restored ? restored : new();
        }
        var json = Read(path);
        if (json is not null && TryParse(json) is { } value) return value;
        // Unreadable or damaged: keep a copy aside, then fall back to the previous good copy before defaults.
        var backup = Preserve(path);
        if (Read(PreviousPath(path)) is { } previous && TryParse(previous) is { } older)
        {
            AppLog.Info("Файл настроек повреждён, восстановлена предыдущая копия");
            older.RecoveredFrom = backup; return older;
        }
        AppLog.Info("Файл настроек повреждён, используются настройки по умолчанию");
        return new() { RecoveredFrom = backup };
    }
    /// <summary>Reads a file, retrying briefly: right after an update or at Windows start the file can be busy for a moment.</summary>
    private static string? Read(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 8) { AppLog.Error("Не удалось прочитать настройки", ex); return null; }
                Thread.Sleep(150);
            }
        }
    }
    /// <summary>
    /// Earlier versions could fall back to the default name. Once, if the name is the default, look for the name in
    /// older copies of the settings (the previous copy and set-aside damaged files) and bring it back.
    /// </summary>
    private static void RecoverName(Settings value, string path)
    {
        if (value.NameRecoveryChecked) return;
        value.NameRecoveryChecked = true;
        if (value.Name != DefaultName) return;
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(folder)) return;
            var candidates = Directory.GetFiles(folder, "settings.broken-*.json").Append(PreviousPath(path)).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (var file in candidates)
            {
                if (Read(file) is not { } json) continue;
                // Only copies written since 0.6 (they carry a version), so the old shared legacy name never comes back.
                var (versioned, stored) = ReadNameLoosely(json);
                if (versioned && CleanName(stored) is { } found && found != DefaultName)
                {
                    value.Name = found; AppLog.Info("Имя профиля восстановлено из резервной копии настроек"); return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>Reads the top-level name and version from a settings file even if it is cut off part-way.</summary>
    private static (bool Versioned, string? Name) ReadNameLoosely(string json)
    {
        bool versioned = false; string? name = null;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var property = reader.GetString();
                if (!reader.Read()) break;
                if (property == nameof(ProfileDefaultsVersion) && reader.TokenType == JsonTokenType.Number) versioned = true;
                else if (property == nameof(Name) && reader.TokenType == JsonTokenType.String) name = reader.GetString();
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return (versioned, name);
    }
    private static string? CleanName(string? name)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? null : text[..Math.Min(text.Length, 24)];
    }

    /// <summary>Copies an unusable settings file aside before defaults overwrite it on the next save.</summary>
    private static string? Preserve(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            var backup = Path.Combine(folder, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            if (File.Exists(backup)) backup = Path.Combine(folder, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.Copy(path, backup);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Не удалось сохранить копию повреждённых настроек", ex);
            return null;
        }
    }
    public static Settings Parse(string json) => TryParse(json) ?? new();
    private static Settings? TryParse(string json)
    {
        JsonDocument schema;
        try { schema = JsonDocument.Parse(json); } catch (JsonException) { return null; }
        using (schema)
        {
            if (schema.RootElement.ValueKind != JsonValueKind.Object) return null;
            Settings value;
            try { value = JsonSerializer.Deserialize<Settings>(json) ?? new(); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                // One bad field must not cost the whole profile: take every field that still reads.
                value = Salvage(schema.RootElement);
                AppLog.Info("В файле настроек были повреждённые поля, остальные сохранены");
            }
            try { return Normalize(value, schema.RootElement); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or NullReferenceException) { return null; }
        }
    }
    private static Settings Salvage(JsonElement root)
    {
        var value = new Settings();
        foreach (var property in root.EnumerateObject())
        {
            var target = typeof(Settings).GetProperty(property.Name);
            if (target is null || !target.CanWrite || target.GetSetMethod() is null || target.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false)) continue;
            try { if (property.Value.Deserialize(target.PropertyType) is { } field) target.SetValue(value, field); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { }
        }
        return value;
    }
    private static Settings Normalize(Settings value, JsonElement root)
    {
        {
            // Text fields written as null by a damaged file fall back to their defaults.
            var defaults = new Settings();
            foreach (var property in typeof(Settings).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetSetMethod() is not null))
                if (property.GetValue(value) is null) property.SetValue(value, property.GetValue(defaults));
            var version = root.TryGetProperty(nameof(ProfileDefaultsVersion), out _) ? value.ProfileDefaultsVersion : 0;
            value.Name = CleanName(value.Name) ?? DefaultName;
            // Profiles from before 0.6 carried a shared legacy name; it is replaced once by the neutral one.
            // Every profile since 0.6 has a version, so later updates never touch a name.
            if (version < 1) value.Name = DefaultName;
            if (version < 2)
            {
                // 0.7: shortcuts no longer have defaults. Clear the old factory chords the user never changed.
                if (value.MicrophoneHotkey == (int)(Keys.Control | Keys.Shift | Keys.M)) value.MicrophoneHotkey = 0;
                if (value.HeadphonesHotkey == (int)(Keys.Control | Keys.Shift | Keys.D)) value.HeadphonesHotkey = 0;
                if (value.OpenWindowHotkey == (int)(Keys.Control | Keys.Shift | Keys.O)) value.OpenWindowHotkey = 0;
                if (value.ScreenHotkey == (int)(Keys.Control | Keys.Shift | Keys.S)) value.ScreenHotkey = 0;
                if (value.CameraHotkey == (int)(Keys.Control | Keys.Shift | Keys.C)) value.CameraHotkey = 0;
                if (!root.TryGetProperty(nameof(AnimationLevel), out _)) value.AnimationLevel = value.Animations ? "full" : "off";
            }
            value.ProfileDefaultsVersion = CurrentDefaultsVersion;
            if (value.AnimationLevel is not ("full" or "minimal" or "off")) value.AnimationLevel = "full";
            if (value.ShareMode is not ("optimized" or "quality" or "custom")) value.ShareMode = "optimized";
            if (value.CameraMode is not ("optimized" or "quality")) value.CameraMode = "optimized";
            if (value.CustomFps is not (15 or 30 or 60 or 120)) value.CustomFps = 60;
            if (value.CustomResolution is not (720 or 1080 or 1440 or 0)) value.CustomResolution = 1080;
            value.CustomBitrateKbps = Math.Clamp(value.CustomBitrateKbps, 500, 20000);
            if (value.Ringtone == "bell") value.Ringtone = "boris";
            value.Volume = Math.Clamp(value.Volume, 0, AudioLevels.MaximumPercent);
            value.MicrophoneGain = Math.Clamp(value.MicrophoneGain, 0, AudioLevels.MaximumPercent);
            if ((int)value.AudioMode is < 0 or > 2) value.AudioMode = VoiceMode.Normal;
            if (value.ConnectionRoute is not ("auto" or "direct" or "relay")) value.ConnectionRoute = "auto";
            if (value.ProtectedTurnPassword.Length > 0)
            {
                try { value.TurnPassword = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedTurnPassword), null, DataProtectionScope.CurrentUser)); }
                catch (Exception ex) when (ex is CryptographicException or FormatException) { value.ProtectedTurnPassword = ""; }
            }
            if (value.TalkKey is not (0x56 or 0x14 or 0x05 or 0x06)) value.TalkKey = 0x56;
            value.ChatLimitMb = Math.Clamp(value.ChatLimitMb / 50 * 50, ChatLimitMin, ChatLimitMax);
            return value;
        }
    }

    public void Save() => Save(FilePath);
    public void Save(string path)
    {
        ProtectedTurnPassword = TurnPassword.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(TurnPassword), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        // The replaced file becomes the previous copy, so a damaged save can always fall back one step.
        if (File.Exists(path))
        {
            try { File.Replace(temporary, path, PreviousPath(path), true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        }
        File.Move(temporary, path, true);
    }
}
