using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Voicecon Uninstall")]

internal static class Uninstall
{
    private static readonly string Expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Voicecon"));
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        if (args.Length == 0)
        {
            if (MessageBox.Show("Удалить Voicecon и его автозагрузку? Текущий звонок завершится. Контакты и настройки останутся в профиле пользователя.", "Удаление Voicecon", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var helper = Path.Combine(Path.GetTempPath(), "Voicecon-uninstall-" + Guid.NewGuid().ToString("N") + ".exe"); File.Copy(Assembly.GetExecutingAssembly().Location, helper);
            Process.Start(new ProcessStartInfo(helper, "--cleanup \"" + Expected + "\" " + Process.GetCurrentProcess().Id) { UseShellExecute = false }); return;
        }
        if (args.Length != 3 || args[0] != "--cleanup" || !string.Equals(Path.GetFullPath(args[1]), Expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(Expected, ".voicecon-install"))) return;
        try
        {
            int original; if (int.TryParse(args[2], out original)) { try { using (var parent = Process.GetProcessById(original)) parent.WaitForExit(3000); } catch { } }
            var executable = Path.Combine(Expected, "Voicecon.exe");
            foreach (var process in Process.GetProcessesByName("Voicecon"))
            {
                try { if (string.Equals(process.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)) { process.Kill(); process.WaitForExit(3000); } } catch { } finally { process.Dispose(); }
            }
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            { if (run != null && ((string)run.GetValue("Voicecon", "")).StartsWith("\"" + executable + "\"", StringComparison.OrdinalIgnoreCase)) run.DeleteValue("Voicecon", false); }
            using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
            { if (parent != null) parent.DeleteSubKeyTree("Voicecon", false); }
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Voicecon.lnk"), executable);
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Voicecon.lnk"), executable);
            foreach (var file in new[] { "Voicecon.exe", "Uninstall.exe", ".voicecon-install", "Voicecon.new", ".voicecon-previous" }) { var path = Path.Combine(Expected, file); if (File.Exists(path)) File.Delete(path); }
            if (Directory.Exists(Expected) && Directory.GetFileSystemEntries(Expected).Length == 0) Directory.Delete(Expected);
            MessageBox.Show("Voicecon удалён. Контакты и настройки сохранены.", "Voicecon");
        }
        catch (Exception ex) { MessageBox.Show("Не удалось завершить удаление: " + ex.Message, "Voicecon"); }
    }
    private static void RemoveShortcut(string path, string target)
    {
        if (!File.Exists(path)) return; var type = Type.GetTypeFromProgID("WScript.Shell"); var shell = Activator.CreateInstance(type);
        dynamic shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        if (string.Equals((string)shortcut.TargetPath, target, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
    }
}
