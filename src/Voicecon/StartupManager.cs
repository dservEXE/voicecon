using Microsoft.Win32;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Voicecon;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("Voicecon") is string; }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Voicecon", "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue("Voicecon", false);
    }
}

public sealed class AppInstance : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Mutex mutex;
    public bool IsPrimary { get; }
    private static string Name => "Voicecon-03-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..12];
    public AppInstance() { mutex = new Mutex(true, @"Local\" + Name, out var created); IsPrimary = created; }
    public static bool Send(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out); pipe.Connect(1200);
            using var writer = new StreamWriter(pipe); writer.WriteLine(command); writer.Flush(); return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return false; }
    }
    public void Listen(Action<string> command)
    {
        _ = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(lifetime.Token); using var reader = new StreamReader(pipe);
                    var text = await reader.ReadLineAsync(lifetime.Token); if (text is "show" or "exit") command(text);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
        });
    }
    public void Dispose() { lifetime.Cancel(); if (IsPrimary) mutex.ReleaseMutex(); mutex.Dispose(); }
}
