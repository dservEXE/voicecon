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
    /// <summary>What each running helper is for ("screen", "camera", "watch"), so stopping one stream ends exactly its helpers.</summary>
    private static readonly ConcurrentDictionary<int, string> purposes = new();
    public static Process Start(IEnumerable<string> args, bool input = false, string purpose = "")
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        var p = new Process { StartInfo = info, EnableRaisingEvents = true }; p.Start(); children[p.Id] = p; purposes[p.Id] = purpose;
        try { if (job.Value is { } handle && handle != IntPtr.Zero) AssignProcessToJobObject(handle, p.Handle); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return p;
    }
    /// <summary>Stops every helper still running (used when the call ends and nothing should be encoding or decoding).</summary>
    public static void StopAll() { foreach (var pair in children.ToArray()) Stop(pair.Value); }
    /// <summary>Stops every helper started for <paramref name="purpose"/>; returns how many were still running.</summary>
    public static int StopFor(string purpose)
    {
        var stopped = 0;
        foreach (var pair in children.ToArray())
            if (purposes.TryGetValue(pair.Key, out var p) && p == purpose) { if (!HasExited(pair.Value)) stopped++; Stop(pair.Value); }
        return stopped;
    }
    /// <summary>Helpers for <paramref name="purpose"/> still running.</summary>
    public static int RunningFor(string purpose) => children.Count(pair => purposes.TryGetValue(pair.Key, out var p) && p == purpose && !HasExited(pair.Value));

    // Helpers live in a job that closes with Voicecon: even if Voicecon crashes, no encoder is left capturing or sending.
    private static readonly Lazy<IntPtr> job = new(CreateJob);
    private static IntPtr CreateJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null); if (handle == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var size = System.Runtime.InteropServices.Marshal.SizeOf<ExtendedLimits>(); var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try { System.Runtime.InteropServices.Marshal.StructureToPtr(info, buffer, false); if (!SetInformationJobObject(handle, 9, buffer, (uint)size)) { CloseHandle(handle); return IntPtr.Zero; } }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        return handle; // Kept open for the life of the process; Windows closes it (and ends the helpers) on exit.
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ExtendedLimits { public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static bool InJob(Process process) => job.Value != IntPtr.Zero && IsProcessInJob(process.Handle, job.Value, out var inside) && inside;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    /// <summary>Ends a helper and its children. Never throws: stopping a stream must always finish.</summary>
    public static void Stop(Process? process)
    {
        if (process is null) return;
        try { children.TryRemove(process.Id, out _); purposes.TryRemove(process.Id, out _); } catch (InvalidOperationException) { }
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(1500); } } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
        try { Interlocked.Add(ref retiredCpu, process.TotalProcessorTime.Ticks); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        process.Dispose();
    }
    public static bool HasExited(Process process) { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
    /// <summary>Helper processes still running (encoders and decoders).</summary>
    public static int Running => children.Values.Count(p => !HasExited(p));
    public static (long Memory, TimeSpan Cpu) Resources()
    {
        long memory = 0; var cpu = TimeSpan.FromTicks(Interlocked.Read(ref retiredCpu));
        foreach (var pair in children) try { var p = pair.Value; p.Refresh(); if (!p.HasExited) { memory += p.WorkingSet64; cpu += p.TotalProcessorTime; } } catch (InvalidOperationException) { }
        return (memory, cpu);
    }
}
