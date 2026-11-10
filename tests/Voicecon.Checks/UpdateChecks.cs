using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Voicecon;
internal static class UpdateChecks
{
    private static void Require(bool value, string description) { if (!value) throw new Exception(description); }
    private static string Metadata(byte[] data, string tag = "v99.0.0", bool draft = false, bool prerelease = false, string? digest = null, string? url = null) => JsonSerializer.Serialize(new
    { tag_name = tag, draft, prerelease, assets = new[] { new { name = GitHubUpdater.InstallerName, state = "uploaded", size = data.LongLength, digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(data)), browser_download_url = url ?? $"https://github.com/{GitHubUpdater.Repository}/releases/download/{tag}/{GitHubUpdater.InstallerName}" } } });
    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Invoke(object obj, string name) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, []);
    public static async Task Check()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('V', 4096)); var current = new Version(0, 6, 0); var good = Metadata(bytes);
        var release = GitHubUpdater.ParseRelease(good, current)!; Require(release.Version == new Version(99, 0, 0, 0), "New release version normalised");
        Require(GitHubUpdater.ParseRelease(Metadata(bytes, "v0.6.0"), current) is null && GitHubUpdater.ParseRelease(Metadata(bytes, "v0.5.9"), current) is null, "Equal version and downgrade ignored");
        Require(GitHubUpdater.ParseRelease(Metadata(bytes, draft: true), current) is null && GitHubUpdater.ParseRelease(Metadata(bytes, prerelease: true), current) is null, "Drafts and prereleases ignored");
        foreach (var bad in new[] { "{}", "not json", Metadata(bytes, "v0.7.0-preview"), Metadata(bytes, digest: "sha256:00"), Metadata(bytes, url: "https://github.com/elsewhere/app/setup.exe"), Metadata(bytes, url: "https://github.com.evil.invalid/dservEXE/voicecon/releases/download/v0.7.0/Voicecon-Setup.exe") })
        { var rejected = false; try { GitHubUpdater.ParseRelease(bad, current); } catch (InvalidDataException) { rejected = true; } Require(rejected, "Malformed or untrusted release rejected"); }
        Require(!GitHubUpdater.AllowedDownload(new Uri("http://github.com/a")) && !GitHubUpdater.AllowedDownload(new Uri("https://release-assets.githubusercontent.com.evil.invalid/a")), "Redirect host/scheme restrictions");
        using (var client = new GitHubUpdater(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(good) }))) Require(await client.CheckAsync(default) is not null, "GitHub metadata check");
        using (var client = new GitHubUpdater(new Handler(_ => new(HttpStatusCode.NotFound)))) Require(await client.CheckAsync(default) is null, "Repository without a release stays usable");
        using (var client = new GitHubUpdater(new Handler(_ => new(HttpStatusCode.Forbidden)))) { var limited = false; try { await client.CheckAsync(default); } catch (HttpRequestException) { limited = true; } Require(limited, "Rate limit reported without installation"); }
        Console.WriteLine("PASS release versions, downgrade/equal/prerelease/draft rejection, malformed metadata, repository URL/hash constraints, HTTPS redirects, 404 and rate limiting");

        var redirects = 0;
        using (var client = new GitHubUpdater(new Handler(request =>
        {
            if (request.RequestUri!.Host == "github.com") { redirects++; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/synthetic-package"); return response; }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        })))
        {
            var package = await client.DownloadAsync(release, null, default);
            Require(redirects == 1 && File.ReadAllBytes(package.Installer).SequenceEqual(bytes) && GitHubUpdater.IsOwnedDirectory(package.Directory), "Verified download through GitHub asset redirect");
            GitHubUpdater.CleanupDirectory(package.Directory); Require(!Directory.Exists(package.Directory), "Completed staging files are fully removed");
        }
        var before = Directory.Exists(GitHubUpdater.UpdateRoot) ? Directory.GetDirectories(GitHubUpdater.UpdateRoot).ToHashSet() : [];
        foreach (var payload in new[] { bytes[..^1], Encoding.UTF8.GetBytes(new string('X', bytes.Length)) })
        {
            using var client = new GitHubUpdater(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })); var rejected = false;
            try { await client.DownloadAsync(release, null, default); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; } Require(rejected, "Truncated or tampered download rejected");
        }
        using (var cancelled = new CancellationTokenSource())
        using (var client = new GitHubUpdater(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
        { cancelled.Cancel(); try { await client.DownloadAsync(release, null, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { } }
        Require(Directory.GetDirectories(GitHubUpdater.UpdateRoot).All(before.Contains), "Failed/cancelled downloads leave no extra installer versions");
        var outside = Path.GetFullPath(Path.Combine("artifacts", "update-safety-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(outside); var sentinel = Path.Combine(outside, GitHubUpdater.InstallerName); File.WriteAllText(sentinel, "keep");
        GitHubUpdater.CleanupDirectory(outside); Require(File.ReadAllText(sentinel) == "keep", "Cleanup cannot delete files outside owned staging folders"); File.Delete(sentinel); Directory.Delete(outside);
        Console.WriteLine("PASS bounded SHA-256 download, legitimate redirect, partial/tamper/cancel cleanup, no accumulated download versions, cleanup path confinement");

        Require(new Settings().Name == "Игрок" && Settings.Parse("{}").Name == "Игрок", "Neutral factory name");
        Require(Settings.Parse("{\"Name\":\"Legacy Person\",\"Volume\":250}") is { Name: "Игрок", Volume: 250 }, "Legacy shared name reset without resetting audio settings");
        Require(Settings.Parse("{\"ProfileDefaultsVersion\":1,\"Name\":\"Моя команда\"}").Name == "Моя команда", "Explicit new profile name survives update");
        using (var resource = typeof(MainForm).Assembly.GetManifestResourceStream("Voicecon.MediaRuntime")!)
        using (var archive = new System.IO.Compression.ZipArchive(resource)) Require(archive.Entries.All(e => e.Name is not ("settings.json" or "contacts.json")), "No personal profile files embedded in runtime");
        using var form = new MainForm(true, "graphite"); var field = form.GetType().GetField("availableUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!; field.SetValue(form, release); Invoke(form, "UpdateUpdateControls");
        Require(Get<RowStyle>(form, "updateBannerRow").Height > 0 && Get<SoftButton>(form, "bannerUpdate").Tone == ButtonTone.Primary && Get<SoftButton>(form, "bannerUpdate").Enabled, "Prominent optional update banner");
        Set(form, "busy", true); Invoke(form, "UpdateUpdateControls"); Require(!Get<SoftButton>(form, "bannerUpdate").Enabled, "Busy call state cannot be interrupted by update"); Set(form, "busy", false);
        typeof(SoftButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Get<SoftButton>(form, "laterUpdate"), [EventArgs.Empty]);
        Require(Get<RowStyle>(form, "updateBannerRow").Height == 0, "Later dismisses banner without forcing install");
        field.SetValue(form, release with { Tag = "v0.8.0", Version = new Version(0, 8, 0, 0) }); Invoke(form, "UpdateUpdateControls"); Require(Get<RowStyle>(form, "updateBannerRow").Height > 0, "A subsequent release can notify again");
        Console.WriteLine("PASS neutral default and legacy-name migration, profile-free runtime, bright optional banner, Later, future update notification, busy-call guard; no install or app focus");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
