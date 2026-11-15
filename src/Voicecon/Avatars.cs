using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Voicecon;

/// <summary>
/// Avatars: your own choice (a colour or a photo) and what friends shared with you in calls.
/// Your avatar travels to the people you call over the encrypted call channel (never through the delivery service).
/// A friend's avatar received in a one-to-one call is kept on this PC; avatars seen in group calls last for the call only.
/// </summary>
public static class Avatars
{
    /// <summary>Colours to choose from; deep enough for white initials in every theme.</summary>
    public static readonly Color[] Palette =
    [
        Color.FromArgb(124, 92, 236), Color.FromArgb(76, 110, 230), Color.FromArgb(40, 150, 214), Color.FromArgb(28, 160, 140),
        Color.FromArgb(70, 160, 80), Color.FromArgb(214, 150, 40), Color.FromArgb(226, 110, 60), Color.FromArgb(214, 72, 96),
        Color.FromArgb(200, 80, 170), Color.FromArgb(110, 118, 140),
    ];
    /// <summary>Largest photo file accepted when choosing one (as most messengers do; it is shrunk right away).</summary>
    public const long MaxPhotoFileBytes = 8 * 1024 * 1024;
    /// <summary>Stored photo side; what is sent to friends is smaller.</summary>
    public const int PhotoSize = 192, SharedSize = 96;
    /// <summary>Largest shared photo accepted from a peer.</summary>
    public const int MaxSharedBytes = 48 * 1024;

    public sealed record Entry(Color? Color, Bitmap? Photo);
    private sealed record Stored(Dictionary<string, string> Colors);

    private static readonly object sync = new();
    private static readonly Dictionary<string, Entry> saved = new(), session = new();
    private static string? folder;
    private static string ownId = "";
    public static event Action? Changed;
    public static string OwnId => ownId;

    /// <summary>Loads saved avatars; <paramref name="own"/> is this profile's id.</summary>
    public static void Load(string own, string? directory = null)
    {
        lock (sync)
        {
            ownId = own; folder = directory ?? Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "avatars");
            foreach (var entry in saved.Values) entry.Photo?.Dispose();
            saved.Clear();
            try
            {
                if (!Directory.Exists(folder)) return;
                var index = Path.Combine(folder, "index.json");
                var colors = File.Exists(index) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(index))?.Colors ?? [] : [];
                var ids = colors.Keys.Concat(Directory.GetFiles(folder, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>()).Where(ValidId).Distinct();
                foreach (var id in ids)
                {
                    Bitmap? photo = null; var file = Path.Combine(folder, id + ".png");
                    if (File.Exists(file) && new FileInfo(file).Length <= 512 * 1024) photo = Decode(File.ReadAllBytes(file), PhotoSize);
                    saved[id] = new Entry(colors.TryGetValue(id, out var hex) ? ParseColor(hex) : null, photo);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Error("Аватарки не загружены", ex); }
        }
    }

    public static Entry? Get(string id)
    {
        lock (sync) return session.TryGetValue(id, out var live) ? live : saved.GetValueOrDefault(id);
    }
    public static Entry? Own => Get(ownId);

    /// <summary>Sets your own avatar: a colour (null = automatic) and an optional photo.</summary>
    public static void SetOwn(Color? color, Bitmap? photo) { if (ownId.Length > 0) Save(ownId, color, photo); }
    /// <summary>Keeps a friend's avatar (received in a one-to-one call).</summary>
    public static void SetFriend(string id, Color? color, Bitmap? photo) { if (ValidId(id) && id != ownId) Save(id, color, photo); }
    /// <summary>Shows an avatar for the rest of a group call without keeping it.</summary>
    public static void SetSession(string id, Color? color, Bitmap? photo)
    {
        if (!ValidId(id) || id == ownId) { photo?.Dispose(); return; }
        lock (sync) { if (session.Remove(id, out var old)) old.Photo?.Dispose(); session[id] = new Entry(color, photo); }
        Changed?.Invoke();
    }
    public static void ClearSession()
    {
        lock (sync) { if (session.Count == 0) return; foreach (var entry in session.Values) entry.Photo?.Dispose(); session.Clear(); }
        Changed?.Invoke();
    }

    private static void Save(string id, Color? color, Bitmap? photo)
    {
        lock (sync)
        {
            if (saved.Remove(id, out var old) && !ReferenceEquals(old.Photo, photo)) old.Photo?.Dispose();
            if (color is null && photo is null) { } else saved[id] = new Entry(color, photo);
            if (folder is null) return;
            try
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, id + ".png");
                if (photo is null) { if (File.Exists(file)) File.Delete(file); }
                else { photo.Save(file + ".tmp", ImageFormat.Png); File.Move(file + ".tmp", file, true); }
                var colors = saved.Where(p => p.Value.Color is not null).ToDictionary(p => p.Key, p => Hex(p.Value.Color!.Value));
                File.WriteAllText(Path.Combine(folder, "index.json.tmp"), JsonSerializer.Serialize(new Stored(colors)));
                File.Move(Path.Combine(folder, "index.json.tmp"), Path.Combine(folder, "index.json"), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { AppLog.Error("Аватарка не сохранена", ex); }
        }
        Changed?.Invoke();
    }

    public static bool ValidId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    public static Color? ParseColor(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !hex.Skip(1).All(Uri.IsHexDigit)) return null;
        return Color.FromArgb(Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
    }

    /// <summary>Reads a picture the person chose: size-checked, cropped to the centre square and shrunk.</summary>
    public static Bitmap LoadPhoto(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new IOException("Файл не найден.");
        if (info.Length > MaxPhotoFileBytes) throw new IOException($"Фото больше {MaxPhotoFileBytes / 1024 / 1024} МБ — выберите файл поменьше.");
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        Image image;
        try { image = Image.FromStream(stream, false, true); } catch (ArgumentException) { throw new IOException("Это не изображение или формат не поддерживается (нужны JPG, PNG, BMP или GIF)."); }
        using (image)
        {
            if (image.Width < 16 || image.Height < 16) throw new IOException("Фото слишком маленькое.");
            if (image.Width > 12000 || image.Height > 12000) throw new IOException("Фото слишком большое по размеру в пикселях.");
            return Square(image, PhotoSize);
        }
    }
    /// <summary>A shared photo, small enough for the call channel (JPEG, about 4–8 KB).</summary>
    public static byte[] Share(Bitmap photo)
    {
        using var small = Square(photo, SharedSize);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
        using var output = new MemoryStream(); small.Save(output, codec, parameters); return output.ToArray();
    }
    /// <summary>Decodes a photo from a peer or the disk: only PNG or JPEG, bounded in bytes and pixels, redrawn into a fresh bitmap.</summary>
    public static Bitmap? Decode(byte[] bytes, int size = SharedSize)
    {
        if (bytes.Length is < 8 or > 512 * 1024) return null;
        var png = bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47; var jpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
        if (!png && !jpeg) return null;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var image = Image.FromStream(stream, false, true);
            if (image.Width is < 8 or > 1024 || image.Height is < 8 or > 1024) return null;
            return Square(image, size);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }
    private static Bitmap Square(Image image, int size)
    {
        var side = Math.Min(image.Width, image.Height);
        var source = new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side);
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
        using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(image, new Rectangle(0, 0, size, size), source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }
}
