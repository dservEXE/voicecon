using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
internal static partial class Setup
{
    private static string Quoted(string value) { return "\"" + value.Replace("\"", "") + "\""; }
    private static bool IsVoicecon(string file) { return File.Exists(file) && FileVersionInfo.GetVersionInfo(file).ProductName == "Voicecon"; }
    private static void InstallUpdate(string[] args)
    {
        string stage = null, backup = null, target = null, source = null, cleanup = null; bool replaced = false, launched = false, parentExited = false, targetExisted = false;
        try
        {
            int pid;
            if (args.Length != 4 || !int.TryParse(args[1], out pid)) throw new IOException("Неверные параметры обновления.");
            source = Path.GetFullPath(args[2]); cleanup = Path.GetFullPath(args[3]).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voicecon", "updates"));
            var self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetDirectoryName(cleanup), root, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(cleanup), "^[a-f0-9]{32}$") || (File.GetAttributes(cleanup) & FileAttributes.ReparsePoint) != 0 || !string.Equals(Path.GetFullPath(self), Path.Combine(cleanup, "Voicecon-Setup.exe"), StringComparison.OrdinalIgnoreCase) || !IsVoicecon(source)) throw new IOException("Не удалось проверить источник обновления.");
            using (var mutex = new System.Threading.Mutex(false, "Local\\Voicecon-Installer-Update"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Установка обновления уже выполняется.");
                try
                {
                    Directory.CreateDirectory(InstallDirectory); stage = Path.Combine(InstallDirectory, "Voicecon.new"); backup = Path.Combine(InstallDirectory, ".voicecon-previous"); target = Path.Combine(InstallDirectory, "Voicecon.exe");
                    Extract("Voicecon.Payload", stage);
                    using (var input = File.OpenRead(stage)) using (var hash = SHA256.Create())
                    { if (BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "") != PayloadHash.Value) throw new IOException("Проверка приложения не прошла."); }
                    try
                    {
                        using (var parent = Process.GetProcessById(pid))
                        {
                            if (!string.Equals(Path.GetFullPath(parent.MainModule.FileName), source, StringComparison.OrdinalIgnoreCase)) throw new IOException("Процесс приложения изменился.");
                            if (!parent.WaitForExit(20000)) throw new IOException("Приложение не завершилось. Обновление отменено.");
                        }
                    }
                    catch (ArgumentException) { }
                    parentExited = true;
                    if (File.Exists(backup)) { if (!IsVoicecon(backup)) throw new IOException("Неожиданный файл резервного восстановления."); File.Delete(backup); }
                    targetExisted = File.Exists(target);
                    if (targetExisted) File.Replace(stage, target, backup); else File.Move(stage, target); replaced = true;
                    Extract("Voicecon.Uninstall", Path.Combine(InstallDirectory, "Uninstall.exe"));
                    File.WriteAllText(Path.Combine(InstallDirectory, ".voicecon-install"), "Voicecon " + BuildInfo.Version);
                    using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        var command = run == null ? "" : (string)run.GetValue("Voicecon", "");
                        if (command.StartsWith(Quoted(source), StringComparison.OrdinalIgnoreCase) || command.StartsWith(Quoted(target), StringComparison.OrdinalIgnoreCase)) run.SetValue("Voicecon", Quoted(target) + " --tray");
                    }
                    using (var entry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Voicecon"))
                    {
                        entry.SetValue("DisplayName", "Voicecon"); entry.SetValue("DisplayVersion", BuildInfo.Version); entry.SetValue("Publisher", "Voicecon"); entry.SetValue("InstallLocation", InstallDirectory); entry.SetValue("DisplayIcon", target);
                        entry.SetValue("UninstallString", Quoted(Path.Combine(InstallDirectory, "Uninstall.exe"))); entry.SetValue("NoModify", 1); entry.SetValue("NoRepair", 1); entry.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024));
                    }
                    UpdateOwnedShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Voicecon.lnk"), source, target);
                    var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"); Directory.CreateDirectory(menu); Shortcut(Path.Combine(menu, "Voicecon.lnk"), target);
                    Process.Start(new ProcessStartInfo(target, "--cleanup-update " + Quoted(cleanup) + " " + Process.GetCurrentProcess().Id) { UseShellExecute = false }); launched = true;
                    TryDelete(backup);
                    CleanupPortableSource(source, target);
                }
                catch
                {
                    if (replaced && !launched) RestoreApplication(backup, target, targetExisted);
                    throw;
                }
                finally { if (stage != null) { TryDelete(stage); stage = null; } mutex.ReleaseMutex(); }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Обновление не установлено: " + ex.Message + "\nКонтакты и настройки сохранены.", "Voicecon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (parentExited && !launched && source != null && IsVoicecon(source))
            { try { Process.Start(new ProcessStartInfo(source, "--cleanup-update " + Quoted(cleanup) + " " + Process.GetCurrentProcess().Id) { UseShellExecute = false }); } catch { } }
        }
    }
    private static void TryDelete(string file) { try { if (file != null && File.Exists(file)) File.Delete(file); } catch { } }
    private static void RestoreApplication(string backup, string target, bool targetExisted)
    {
        try
        {
            if (File.Exists(backup)) File.Replace(backup, target, null);
            else if (!targetExisted && IsVoicecon(target)) File.Delete(target);
        }
        catch { }
    }
    private static void CleanupPortableSource(string source, string target)
    {
        try
        {
            source = Path.GetFullPath(source); target = Path.GetFullPath(target);
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(source), "^Voicecon(?:-[0-9]+(?:\\.[0-9]+){1,3})?\\.exe$", RegexOptions.IgnoreCase) &&
                !HasReparseAncestor(source) && IsVoicecon(source)) File.Delete(source);
        }
        catch { }
    }
    private static bool HasReparseAncestor(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    private static void VerifyPortableCleanup(string directory)
    {
        var target = Path.Combine(directory, "Voicecon.exe");
        var old = Path.Combine(directory, "Voicecon-0.5.0.exe");
        var renamed = Path.Combine(directory, "custom-name.exe");
        var unrelated = Path.Combine(directory, "Voicecon-99.0.exe");
        var installed = Path.Combine(directory, "installed-copy.exe");
        var backup = Path.Combine(directory, "rollback-copy.exe");
        File.Copy(target, old, true); File.Copy(target, renamed, true);
        File.WriteAllText(unrelated, "Unrelated user file");
        try
        {
            CleanupPortableSource(target, target);
            CleanupPortableSource(renamed, target);
            CleanupPortableSource(unrelated, target);
            CleanupPortableSource(old, target);
            if (!File.Exists(target) || !File.Exists(renamed) || !File.Exists(unrelated) || File.Exists(old)) throw new IOException("Portable cleanup verification failed.");
            File.Copy(target, backup, true); File.WriteAllText(installed, "Failed replacement");
            RestoreApplication(backup, installed, true);
            if (!IsVoicecon(installed) || File.Exists(backup)) throw new IOException("Existing installation rollback failed.");
            RestoreApplication(backup, installed, false);
            if (File.Exists(installed)) throw new IOException("First installation rollback failed.");
        }
        finally { TryDelete(old); TryDelete(renamed); TryDelete(unrelated); TryDelete(installed); TryDelete(backup); }
    }
    private static void UpdateOwnedShortcut(string location, string oldPath, string target)
    {
        if (!File.Exists(location)) return;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell"); var shell = Activator.CreateInstance(type);
            dynamic link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { location });
            string path = link.TargetPath;
            if (string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) Shortcut(location, target);
        }
        catch { }
    }
}
