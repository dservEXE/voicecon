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
    public string LastConversation { get; set; } = "";
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

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voicecon", "settings.json");

    /// <summary>Path of the backup made when the last load had to fall back to defaults; null if the file was fine.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RecoveredFrom { get; private set; }

    public static Settings Load() => Load(FilePath);
    public static Settings Load(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path)) return new();
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Не удалось прочитать настройки", ex);
            return new() { RecoveredFrom = Preserve(path) };
        }
        if (TryParse(json) is { } value) return value;
        AppLog.Info("Файл настроек повреждён, используются настройки по умолчанию");
        return new() { RecoveredFrom = Preserve(path) };
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
        try
        {
            using var schema = JsonDocument.Parse(json);
            var value = JsonSerializer.Deserialize<Settings>(json) ?? new();
            var root = schema.RootElement;
            var version = root.TryGetProperty(nameof(ProfileDefaultsVersion), out _) ? value.ProfileDefaultsVersion : 0;
            if (version < 1 || string.IsNullOrWhiteSpace(value.Name)) value.Name = "Игрок";
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
            return value;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
    }

    public void Save()
    {
        ProtectedTurnPassword = TurnPassword.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(TurnPassword), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
