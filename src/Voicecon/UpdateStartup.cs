using System.Diagnostics;
namespace Voicecon;
public static class UpdateStartup
{
    public static void Schedule(string[] args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var index = Array.IndexOf(args, "--cleanup-update");
                if (index >= 0 && args.Length > index + 2 && int.TryParse(args[index + 2], out var pid) && GitHubUpdater.IsOwnedDirectory(args[index + 1]))
                {
                    try { using var helper = Process.GetProcessById(pid); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch (ArgumentException) { } catch (TimeoutException) { return; }
                    GitHubUpdater.CleanupDirectory(args[index + 1]);
                }
                GitHubUpdater.CleanupStale();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        });
    }
}
