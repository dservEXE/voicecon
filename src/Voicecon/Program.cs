namespace Voicecon;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Background threads (audio, WebRTC, capture) cannot be caught by the WinForms handler.
        // .NET still terminates the process after an unhandled exception, but the cause is recorded.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Необработанная ошибка, приложение будет закрыто" + (e.IsTerminating ? "" : " (не критично)"), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Необработанная ошибка фоновой задачи", e.Exception); e.SetObserved(); };
        using var instance = new AppInstance();
        if (!instance.IsPrimary || args.Contains("--quit")) { AppInstance.Send(args.Contains("--quit") ? "exit" : "show"); return; }
        AppLog.Info("Запуск Voicecon " + GitHubUpdater.CurrentVersion.ToString(3) + (args.Contains("--tray") ? " в трее" : ""));
        UpdateStartup.Schedule(args);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("Ошибка интерфейса", e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nПодробности записаны в журнал:\n" + AppLog.FilePath,
                "Voicecon", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        using var form = new MainForm(startInTray: args.Contains("--tray"));
        instance.Listen(command => form.DeliverInstanceCommand(command));
        Application.Run(form);
        AppLog.Info("Выход из Voicecon");
    }
}
