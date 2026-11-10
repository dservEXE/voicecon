using System.Text;

namespace Voicecon;

/// <summary>
/// Local diagnostic log in %LOCALAPPDATA%\Voicecon\logs. Never sent anywhere.
/// Only exception details and short event names are written: no chat text, codes, keys or addresses.
/// </summary>
public static class AppLog
{
    public const long MaximumSize = 1024 * 1024;
    private static readonly object sync = new();
    private static string? directoryOverride;

    public static string Directory => directoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voicecon", "logs");
    public static string FilePath => Path.Combine(Directory, "voicecon.log");

    /// <summary>Redirects the log for device-free checks.</summary>
    public static void UseDirectory(string? directory) { lock (sync) directoryOverride = directory; }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string context, Exception? exception) => Write("ERROR", context, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ')
                .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message);
            if (exception is not null) line.AppendLine().Append(exception);
            line.AppendLine();
            lock (sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaximumSize) File.Move(path, path + ".1", true);
                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Diagnostics must never break the application.
        }
    }
}
