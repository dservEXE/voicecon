namespace Voicecon;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var instance = new AppInstance();
        if (!instance.IsPrimary || args.Contains("--quit")) { AppInstance.Send(args.Contains("--quit") ? "exit" : "show"); return; }
        UpdateStartup.Schedule(args);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message,
            "Voicecon", MessageBoxButtons.OK, MessageBoxIcon.Error);
        using var form = new MainForm(startInTray: args.Contains("--tray"));
        instance.Listen(command => form.DeliverInstanceCommand(command));
        Application.Run(form);
    }
}
