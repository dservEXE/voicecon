using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Voicecon Setup")]
[assembly: AssemblyProduct("Voicecon Installer")]


internal static partial class Setup
{
    private static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Voicecon");
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify")
        {
            try
            {
                Directory.CreateDirectory(args[1]); Extract("Voicecon.Payload", Path.Combine(args[1], "Voicecon.exe")); Extract("Voicecon.Uninstall", Path.Combine(args[1], "Uninstall.exe"));
                VerifyPortableCleanup(args[1]);
                File.WriteAllText(Path.Combine(args[1], "verified.txt"), "Payloads, portable cleanup and rollback verified without installation, UI, startup changes or app launch.");
            }
            catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(ex); }
            return;
        }
        if (args.Length > 0 && args[0] == "--update") { InstallUpdate(args); return; }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupWindow());
    }
    private static void Extract(string name, string destination)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var output = File.Create(destination)) { if (input == null) throw new IOException("Missing embedded payload"); input.CopyTo(output); }
    }
    private static List<string> StopApp()
    {
        var stopped = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != Process.GetCurrentProcess().SessionId || (!process.ProcessName.Equals("Voicecon", StringComparison.OrdinalIgnoreCase) && !process.ProcessName.StartsWith("Voicecon-", StringComparison.OrdinalIgnoreCase))) continue;
                var path = process.MainModule.FileName;
                if (!string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "Voicecon", StringComparison.OrdinalIgnoreCase)) continue;
                var info = FileVersionInfo.GetVersionInfo(path);
                if (info.FileMajorPart > 0 || info.FileMinorPart >= 3)
                {
                    using (var command = Process.Start(new ProcessStartInfo(path, "--quit") { UseShellExecute = false, CreateNoWindow = true })) command.WaitForExit(2500);
                    if (process.WaitForExit(1500)) { stopped.Add(path); continue; }
                }
                if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); }
                if (process.HasExited) stopped.Add(path);
            }
            catch { }
            finally { process.Dispose(); }
        }
        return stopped;
    }
    private static void Shortcut(string location, string executable)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell"); var shell = Activator.CreateInstance(type);
        dynamic shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { location });
        shortcut.TargetPath = executable; shortcut.WorkingDirectory = InstallDirectory; shortcut.IconLocation = executable + ",0"; shortcut.Description = "Voicecon — своя команда"; shortcut.Save();
    }
    private sealed class SetupWindow : Form
    {
        private readonly CheckBox startup = new CheckBox(), desktop = new CheckBox(), launch = new CheckBox();
        private readonly Button install = new Button();
        private readonly Label status = new Label();
        public SetupWindow()
        {
            Text = "Установка Voicecon " + BuildInfo.Version; ClientSize = new Size(580, 440); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; BackColor = Color.FromArgb(17, 21, 29); ForeColor = Color.FromArgb(240, 243, 250); Font = new Font("Segoe UI", 10);
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            var title = new Label { Text = "voicecon", Font = new Font("Segoe UI Semibold", 26), Location = new Point(30, 24), Size = new Size(500, 48) }; Controls.Add(title);
            Controls.Add(new Label { Text = "Голос, чат, экран и камера для своей команды.\nУстановка только для вашего пользователя, без прав администратора.", Location = new Point(32, 85), Size = new Size(510, 55), ForeColor = Color.FromArgb(156, 169, 189) });
            startup.Text = "Запускать вместе с Windows — сразу в трее"; startup.Checked = true; startup.SetBounds(32, 157, 510, 30); Controls.Add(startup);
            desktop.Text = "Создать ярлык на рабочем столе"; desktop.Checked = true; desktop.SetBounds(32, 195, 510, 30); Controls.Add(desktop);
            launch.Text = "Открыть Voicecon после установки"; launch.Checked = true; launch.SetBounds(32, 233, 510, 30); Controls.Add(launch);
            status.Text = "Установка обновит Voicecon и завершит текущий звонок,\nесли приложение уже запущено. Контакты и настройки сохранятся."; status.ForeColor = Color.FromArgb(156, 169, 189); status.SetBounds(32, 281, 510, 62); Controls.Add(status);
            install.Text = "Установить и обновить"; install.SetBounds(32, 362, 510, 46); install.FlatStyle = FlatStyle.Flat; install.FlatAppearance.BorderSize = 0; install.BackColor = Color.FromArgb(176, 194, 255); install.ForeColor = Color.FromArgb(23, 33, 59); Controls.Add(install); install.Click += Install; AcceptButton = install;
        }
        private void Install(object sender, EventArgs args)
        {
            install.Enabled = false; UseWaitCursor = true;
            var stage = Path.Combine(InstallDirectory, "Voicecon.new");
            var executable = Path.Combine(InstallDirectory, "Voicecon.exe");
            var backup = Path.Combine(InstallDirectory, ".voicecon-previous");
            bool replaced = false, committed = false, ownsMutex = false, targetExisted = false;
            var mutex = new System.Threading.Mutex(false, "Local\\Voicecon-Installer-Update");
            try
            {
                try { ownsMutex = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) throw new IOException("Другая установка Voicecon уже выполняется.");
                Directory.CreateDirectory(InstallDirectory);
                Extract("Voicecon.Payload", stage);
                using (var input = File.OpenRead(stage)) using (var hash = SHA256.Create())
                {
                    var actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
                    if (actual != PayloadHash.Value) throw new IOException("Проверка файла приложения не прошла.");
                }
                var stopped = StopApp();
                if (File.Exists(backup)) { if (!IsVoicecon(backup)) throw new IOException("Неожиданный файл резервного восстановления."); File.Delete(backup); }
                targetExisted = File.Exists(executable);
                if (targetExisted) File.Replace(stage, executable, backup); else File.Move(stage, executable);
                replaced = true;
                Extract("Voicecon.Uninstall", Path.Combine(InstallDirectory, "Uninstall.exe")); File.WriteAllText(Path.Combine(InstallDirectory, ".voicecon-install"), "Voicecon " + BuildInfo.Version);
                using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                { if (startup.Checked) run.SetValue("Voicecon", "\"" + executable + "\" --tray"); else run.DeleteValue("Voicecon", false); }
                using (var entry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Voicecon"))
                {
                    entry.SetValue("DisplayName", "Voicecon"); entry.SetValue("DisplayVersion", BuildInfo.Version); entry.SetValue("Publisher", "Voicecon"); entry.SetValue("InstallLocation", InstallDirectory); entry.SetValue("DisplayIcon", executable);
                    entry.SetValue("UninstallString", "\"" + Path.Combine(InstallDirectory, "Uninstall.exe") + "\""); entry.SetValue("NoModify", 1); entry.SetValue("NoRepair", 1); entry.SetValue("EstimatedSize", (int)(new FileInfo(executable).Length / 1024));
                }
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"); Directory.CreateDirectory(startMenu); Shortcut(Path.Combine(startMenu, "Voicecon.lnk"), executable);
                if (desktop.Checked) Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Voicecon.lnk"), executable);
                else foreach (var source in stopped) UpdateOwnedShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Voicecon.lnk"), source, executable);
                if (launch.Checked) Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
                committed = true; TryDelete(backup);
                foreach (var source in stopped) CleanupPortableSource(source, executable);
                Close();
            }
            catch (Exception ex)
            {
                if (replaced && !committed) RestoreApplication(backup, executable, targetExisted);
                status.Text = "Установка не завершена: " + ex.Message; install.Enabled = true; UseWaitCursor = false;
            }
            finally { if (ownsMutex) { TryDelete(stage); mutex.ReleaseMutex(); } mutex.Dispose(); }
        }
    }
}
