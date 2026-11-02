using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
namespace Voicecon;
public static class MediaRuntime
{
    private static readonly Lazy<string> executable = new(Extract);
    private static readonly ConcurrentDictionary<int, Process> children = new();
    private static long retiredCpu;
    public static string Executable => executable.Value;
    private static string Extract()
    {
        var replacement = Path.Combine(AppContext.BaseDirectory, "media-runtime", "ffmpeg.exe");
        if (File.Exists(replacement)) return replacement; // LGPL: user may supply a modified compatible build.
        using var resource = typeof(MediaRuntime).Assembly.GetManifestResourceStream("Voicecon.MediaRuntime") ?? throw new IOException("Видеомодуль отсутствует.");
        var hash = Convert.ToHexString(SHA256.HashData(resource)); resource.Position = 0;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voicecon", "runtime", hash[..16]); Directory.CreateDirectory(root);
        using var guard = new Mutex(false, "Local\\Voicecon-Media-" + hash[..16]); try { guard.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name != entry.FullName) throw new IOException("Неверный путь видеомодуля.");
                var path = Path.Combine(root, entry.Name);
                // Verify every file against the embedded archive before executing an extracted helper.
                using var original = entry.Open(); var expected = SHA256.HashData(original);
                if (File.Exists(path)) { using var existing = File.OpenRead(path); if (SHA256.HashData(existing).SequenceEqual(expected)) continue; }
                using var source = entry.Open(); using var output = File.Create(path); source.CopyTo(output);
            }
        }
        finally { guard.ReleaseMutex(); }
        CleanupOtherRuntimes(root);
        return Path.Combine(root, "ffmpeg.exe");
    }
    private static void CleanupOtherRuntimes(string current)
    {
        var parent = Path.GetDirectoryName(current)!;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ffmpeg.exe", "avcodec-63.dll", "avdevice-63.dll", "avfilter-12.dll", "avformat-63.dll", "avutil-61.dll", "swscale-10.dll", "swresample-7.dll", "buildconf.txt", "build-recipe-commit.txt", "provenance.json", "SOURCES.txt", "LICENSE-GPL3.txt", "LICENSE-LGPL3.txt", "LICENSE-OpenH264.txt" };
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(parent))
            {
                if (folder.Equals(current, StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(folder), "^[A-Fa-f0-9]{16}$") || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                var files = Directory.GetFiles(folder);
                if (!File.Exists(Path.Combine(folder, "ffmpeg.exe")) || files.Any(file => !known.Contains(Path.GetFileName(file)) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) || Directory.EnumerateDirectories(folder).Any()) continue;
                try { foreach (var file in files) File.Delete(file); Directory.Delete(folder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public static Process Start(IEnumerable<string> args, bool input = false)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        var p = new Process { StartInfo = info, EnableRaisingEvents = true }; p.Start(); children[p.Id] = p; return p;
    }
    public static void Stop(Process? process)
    {
        if (process is null) return; children.TryRemove(process.Id, out _);
        try { if (!process.HasExited) process.Kill(true); Interlocked.Add(ref retiredCpu, process.TotalProcessorTime.Ticks); } catch (InvalidOperationException) { } finally { process.Dispose(); }
    }
    public static (long Memory, TimeSpan Cpu) Resources()
    {
        long memory = 0; var cpu = TimeSpan.FromTicks(Interlocked.Read(ref retiredCpu));
        foreach (var pair in children) try { var p = pair.Value; p.Refresh(); if (!p.HasExited) { memory += p.WorkingSet64; cpu += p.TotalProcessorTime; } } catch (InvalidOperationException) { }
        return (memory, cpu);
    }
}
