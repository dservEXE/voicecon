using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Voicecon;

public sealed class Settings
{
    public string Name { get; set; } = "Игрок";
    public int ProfileDefaultsVersion { get; set; } = 1;
    public bool CheckUpdates { get; set; } = true;
    public string DismissedUpdate { get; set; } = "";
    public DateTimeOffset LastUpdateCheckUtc { get; set; }
    public string Theme { get; set; } = "graphite";
    public bool Animations { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public string Ringtone { get; set; } = "bell";
    public string RingtoneFile { get; set; } = "";
    public bool NotificationSounds { get; set; } = true;
    public string SignalingServer { get; set; } = "https://ntfy.sh";
    public int MicrophoneHotkey { get; set; } = (int)(Keys.Control | Keys.Shift | Keys.M);
    public int HeadphonesHotkey { get; set; } = (int)(Keys.Control | Keys.Shift | Keys.D);
    public int OpenWindowHotkey { get; set; } = (int)(Keys.Control | Keys.Shift | Keys.O);
    public int ScreenHotkey { get; set; } = (int)(Keys.Control | Keys.Shift | Keys.S);
    public int CameraHotkey { get; set; } = (int)(Keys.Control | Keys.Shift | Keys.C);
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

    public static Settings Load()
    {
        try { return Parse(File.ReadAllText(FilePath)); } catch { return new(); }
    }
    public static Settings Parse(string json)
    {
        try
        {
            using var schema = JsonDocument.Parse(json);
            var value = JsonSerializer.Deserialize<Settings>(json) ?? new();
            if (!schema.RootElement.TryGetProperty(nameof(ProfileDefaultsVersion), out _) || value.ProfileDefaultsVersion < 1 || string.IsNullOrWhiteSpace(value.Name)) value.Name = "Игрок";
            value.ProfileDefaultsVersion = 1;
            value.Volume = Math.Clamp(value.Volume, 0, AudioLevels.MaximumPercent);
            value.MicrophoneGain = Math.Clamp(value.MicrophoneGain, 0, AudioLevels.MaximumPercent);
            if (value.ConnectionRoute is not ("auto" or "direct" or "relay")) value.ConnectionRoute = "auto";
            if (value.ProtectedTurnPassword.Length > 0)
            {
                try { value.TurnPassword = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedTurnPassword), null, DataProtectionScope.CurrentUser)); }
                catch (Exception ex) when (ex is CryptographicException or FormatException) { value.ProtectedTurnPassword = ""; }
            }
            if (value.TalkKey is not (0x56 or 0x14 or 0x05 or 0x06)) value.TalkKey = 0x56;
            return value;
        }
        catch { return new(); }
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

