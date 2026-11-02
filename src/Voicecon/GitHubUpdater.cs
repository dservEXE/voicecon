using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Voicecon;

public sealed record GitHubRelease(Version Version, string Tag, Uri Download, string Sha256, long Size, string Page);
public sealed record PreparedUpdate(GitHubRelease Release, string Directory, string Installer);

public sealed class GitHubUpdater : IDisposable
{
    public const string Repository = "dservEXE/voicecon";
    public const string InstallerName = "Voicecon-Setup.exe";
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    public static Version CurrentVersion => Normalize(typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voicecon", "updates");
    private readonly HttpClient http;
    public GitHubUpdater(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Voicecon/" + CurrentVersion.ToString(3));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    public static GitHubRelease? ParseRelease(string json, Version current)
    {
        try { return ParseReleaseCore(json, current); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("GitHub прислал некорректные сведения о релизе.", ex); }
    }
    private static GitHubRelease? ParseReleaseCore(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, "^v?[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant) || !Version.TryParse(tag.TrimStart('v'), out var parsed)) throw new InvalidDataException("У релиза неверный номер версии.");
        var version = Normalize(parsed); if (version <= Normalize(current)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == InstallerName && a.GetProperty("state").GetString() == "uploaded").ToArray();
        if (assets.Length != 1) throw new InvalidDataException("В релизе нет единственного установщика Voicecon-Setup.exe.");
        var asset = assets[0]; var size = asset.GetProperty("size").GetInt64(); if (size is <= 0 or > MaximumInstallerSize) throw new InvalidDataException("Неверный размер установщика.");
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("GitHub не предоставил SHA-256 установщика.");
        var expected = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{InstallerName}");
        if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || url != expected) throw new InvalidDataException("Установщик находится за пределами репозитория Voicecon.");
        return new(version, tag, expected, digest[7..].ToUpperInvariant(), size, $"https://github.com/{Repository}/releases/tag/{tag}");
    }
    public async Task<GitHubRelease?> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if ((int)response.StatusCode is 403 or 429) throw new HttpRequestException("GitHub временно ограничил проверки. Повторите позже.");
        response.EnsureSuccessStatusCode(); var body = await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token); return ParseRelease(System.Text.Encoding.UTF8.GetString(body), CurrentVersion);
    }
    public async Task<PreparedUpdate> DownloadAsync(GitHubRelease release, IProgress<int>? progress, CancellationToken token)
    {
        if (release.Version <= CurrentVersion) throw new InvalidOperationException("Обновление уже не новее установленной версии.");
        if (release.Size is <= 0 or > MaximumInstallerSize || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") || !Regex.IsMatch(release.Tag, "^v?[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?$") || release.Download != new Uri($"https://github.com/{Repository}/releases/download/{release.Tag}/{InstallerName}")) throw new InvalidDataException("Неверные параметры обновления.");
        var directory = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, InstallerName + ".part"); var destination = Path.Combine(directory, InstallerName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var uri = release.Download; HttpResponseMessage? response = null;
            try
            {
                for (var redirects = 0; redirects <= 5; redirects++)
                {
                    if (!AllowedDownload(uri)) throw new InvalidDataException("Недопустимый адрес загрузки.");
                    response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        var location = response.Headers.Location ?? throw new IOException("GitHub не прислал адрес файла."); uri = location.IsAbsoluteUri ? location : new Uri(uri, location); response.Dispose(); response = null; continue;
                    }
                    break;
                }
                if (response is null) throw new IOException("Слишком много перенаправлений при загрузке.");
                response.EnsureSuccessStatusCode(); if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new IOException("Размер загрузки не совпадает с релизом.");
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long total = 0; int read, last = -1;
                    while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += read; if (total > release.Size || total > MaximumInstallerSize) throw new IOException("Установщик больше заявленного размера.");
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token); hash.AppendData(buffer, 0, read);
                        var percent = (int)(total * 100 / release.Size); if (percent != last) { last = percent; progress?.Report(percent); }
                    }
                    if (total != release.Size) throw new IOException("Установщик загружен не полностью.");
                }
                var receivedHash = Convert.ToHexString(hash.GetHashAndReset()); if (!receivedHash.Equals(release.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Проверка SHA-256 не прошла. Установка отменена.");
                File.Move(partial, destination); return new(release, directory, destination);
            }
            finally { response?.Dispose(); }
        }
        catch { CleanupDirectory(directory); throw; }
    }
    public static bool AllowedDownload(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Host is "github.com" or "api.github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        using var source = await content.ReadAsStreamAsync(token); using var target = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0) { if (target.Length + read > limit) throw new IOException("Слишком большой ответ GitHub."); target.Write(buffer, 0, read); } return target.ToArray();
    }
    public static bool IsOwnedDirectory(string directory)
    {
        try
        {
            var path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            return Path.GetDirectoryName(path)!.Equals(Path.GetFullPath(UpdateRoot), StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(Path.GetFileName(path), "^[a-f0-9]{32}$") && (!System.IO.Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
    public static void CleanupDirectory(string directory)
    {
        if (!IsOwnedDirectory(directory)) return;
        try
        {
            foreach (var file in new[] { InstallerName, InstallerName + ".part" }) { var path = Path.Combine(directory, file); if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); }
            if (System.IO.Directory.Exists(directory) && !System.IO.Directory.EnumerateFileSystemEntries(directory).Any()) System.IO.Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public static void CleanupStale()
    {
        if (!System.IO.Directory.Exists(UpdateRoot)) return;
        try { foreach (var dir in System.IO.Directory.EnumerateDirectories(UpdateRoot)) if (System.IO.Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-24)) CleanupDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => http.Dispose();
}
