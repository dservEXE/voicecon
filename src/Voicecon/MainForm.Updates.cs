using System.Diagnostics;
namespace Voicecon;
public sealed partial class MainForm
{
    private readonly GitHubUpdater updater = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private CancellationTokenSource? updateDownload;
    private readonly UiLabel updateStatus = new(), updateHeadline = new();
    private readonly SoftButton checkUpdate = new(), installUpdate = new(), bannerUpdate = new(), laterUpdate = new();
    private readonly ToggleRow autoUpdates = new();
    private RowStyle? updateBannerRow;
    private GitHubRelease? availableUpdate;
    private PreparedUpdate? preparedUpdate;
    private bool checkingUpdate, downloadingUpdate, updateStarted;
    private Control BuildUpdateBanner()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Elevation = 0.8f, Padding = new Padding(16, 7, 12, 7), Wash = Color.FromArgb(30, UiTheme.Current.Accent) }; var row = Columns(-1, 236, 4, 104);
        PrepareLabel(updateHeadline, TextTone.Normal); updateHeadline.Font = UiTheme.Heading; updateHeadline.Text = "Есть обновление Voicecon";
        PrepareButton(bannerUpdate, "Обновить", UiIcon.Arrow, ButtonTone.Primary); PrepareButton(laterUpdate, "Позже", UiIcon.None, ButtonTone.Quiet);
        row.Controls.Add(updateHeadline, 0, 0); row.Controls.Add(bannerUpdate, 1, 0); row.Controls.Add(laterUpdate, 3, 0); card.Controls.Add(row);
        bannerUpdate.Click += async (_, _) => await InstallUpdateAsync(); laterUpdate.Click += (_, _) => { if (availableUpdate is { } release) settings.DismissedUpdate = release.Tag; SaveSettings(); UpdateUpdateControls(); };
        return card;
    }
    private void AddUpdateSettings(TableLayoutPanel grid)
    {
        SetupToggle(autoUpdates, "Проверять обновления", "Установка только по вашей кнопке", settings.CheckUpdates); AddRow(grid, autoUpdates, 52);
        PrepareLabel(updateStatus, TextTone.Muted); updateStatus.Font = UiTheme.Small; updateStatus.Text = "Версия " + GitHubUpdater.CurrentVersion.ToString(3) + " · источник: GitHub"; AddRow(grid, updateStatus, 36);
        var updateActions = Columns(-45, 8, -55); PrepareButton(checkUpdate, "Проверить", UiIcon.Refresh, ButtonTone.Quiet); PrepareButton(installUpdate, "Обновить", UiIcon.Arrow, ButtonTone.Primary);
        updateActions.Controls.Add(checkUpdate, 0, 0); updateActions.Controls.Add(installUpdate, 2, 0); AddRow(grid, updateActions, 44);
        checkUpdate.Click += async (_, _) => await CheckUpdatesAsync(true); installUpdate.Click += async (_, _) => await InstallUpdateAsync();
        autoUpdates.CheckedChanged += (_, _) => { settings.CheckUpdates = autoUpdates.Checked; SaveSettings(); if (!previewMode && settings.CheckUpdates) _ = CheckUpdatesAsync(false); };
        UpdateUpdateControls();
    }
    private bool CallBusyForUpdate => busy || room is { IsDisposed: false, HasStarted: true } || incomingCall is not null || outgoingCalls.Count > 0 || capture.ScreenActive || capture.CameraActive;
    private void UpdateUpdateControls()
    {
        checkUpdate.Enabled = !checkingUpdate && !downloadingUpdate; checkUpdate.Text = checkingUpdate ? "Проверяем…" : "Проверить";
        var enabled = availableUpdate is not null && !checkingUpdate && !downloadingUpdate && !CallBusyForUpdate;
        installUpdate.Enabled = bannerUpdate.Enabled = enabled;
        installUpdate.Text = bannerUpdate.Text = downloadingUpdate ? "Загружаем…" : availableUpdate is { } release ? "Обновить до " + release.Version.ToString(3) : "Обновить";
        if (availableUpdate is { } next) updateHeadline.Text = "Новая версия " + next.Version.ToString(3) + (CallBusyForUpdate ? " · после звонка" : " · можно позже");
        if (updateBannerRow is not null) updateBannerRow.Height = availableUpdate is { } found && settings.DismissedUpdate != found.Tag ? 90 : 0;
    }
    private void ScheduleUpdateCheck()
    {
        if (!previewMode && settings.CheckUpdates && !checkingUpdate && !downloadingUpdate && DateTimeOffset.UtcNow - settings.LastUpdateCheckUtc > TimeSpan.FromHours(6)) _ = CheckUpdatesAsync(false);
    }
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (previewMode || checkingUpdate || downloadingUpdate) return;
        checkingUpdate = true; settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow; SaveSettings(); updateStatus.Text = "Проверяем GitHub Releases…"; UpdateUpdateControls();
        try
        {
            var found = await updater.CheckAsync(updateLifetime.Token); if (IsDisposed) return; availableUpdate = found;
            updateStatus.Text = found is null ? "У вас актуальная версия; новых релизов пока нет." : "Доступна " + found.Version.ToString(3) + ". Обновление необязательное.";
            if (manual && found is not null) settings.DismissedUpdate = "";
        }
        catch (OperationCanceledException) { if (!IsDisposed) updateStatus.Text = "Проверка отменена или GitHub не ответил вовремя."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { if (!IsDisposed) updateStatus.Text = "Не удалось проверить обновления. " + ex.Message; }
        finally { checkingUpdate = false; if (!IsDisposed) UpdateUpdateControls(); }
    }
    private async Task InstallUpdateAsync()
    {
        if (previewMode || availableUpdate is not { } release || downloadingUpdate || checkingUpdate || CallBusyForUpdate) return;
        downloadingUpdate = true; updateDownload = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token); UpdateUpdateControls();
        try
        {
            if (preparedUpdate is null || preparedUpdate.Release.Tag != release.Tag)
            {
                if (preparedUpdate is { } old) GitHubUpdater.CleanupDirectory(old.Directory);
                preparedUpdate = await updater.DownloadAsync(release, new Progress<int>(percent => { if (!IsDisposed) updateStatus.Text = $"Загружаем обновление: {percent}% · проверим SHA-256"; }), updateDownload.Token);
            }
            if (IsDisposed) { GitHubUpdater.CleanupDirectory(preparedUpdate.Directory); return; }
            if (CallBusyForUpdate) { updateStatus.Text = "Обновление загружено. Завершите звонок и нажмите «Обновить»."; return; }
            var info = FileVersionInfo.GetVersionInfo(preparedUpdate.Installer);
            if (info.ProductName != "Voicecon Installer" || !Version.TryParse(info.FileVersion, out var version) || GitHubUpdater.Normalize(version) != release.Version) throw new InvalidDataException("Версия установщика не совпадает с релизом.");
            var start = new ProcessStartInfo(preparedUpdate.Installer) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--update", Environment.ProcessId.ToString(), Environment.ProcessPath!, preparedUpdate.Directory }) start.ArgumentList.Add(argument);
            using var helper = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик.");
            updateStarted = true; quitting = true; Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { if (!IsDisposed) updateStatus.Text = "Обновление не установлено. " + ex.Message; if (preparedUpdate is { } failed) { GitHubUpdater.CleanupDirectory(failed.Directory); preparedUpdate = null; } }
        finally { downloadingUpdate = false; updateDownload?.Dispose(); updateDownload = null; if (!IsDisposed) UpdateUpdateControls(); }
    }
    private void DisposeUpdates()
    {
        updateLifetime.Cancel(); updateDownload?.Cancel(); if (!updateStarted && preparedUpdate is { } staged) GitHubUpdater.CleanupDirectory(staged.Directory); updater.Dispose();
    }
}
