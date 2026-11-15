using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Voicecon;

public sealed partial class MainForm
{
    private readonly VideoCapture capture = new();
    private ProcessLoopback? shareSound;
    private readonly ConcurrentDictionary<string, StreamView> streamWindows = new();
    private readonly ConcurrentDictionary<string, VideoDecoder> decoders = new();
    private readonly ConcurrentDictionary<string, uint> stoppedSequences = new();
    private bool cameraStarting, screenStarting;

    /// <summary>
    /// One stream being watched (frames arrive on network threads). It shows on the stage in the main window, or in its
    /// own window when popped out (<see cref="Window"/> is then set).
    /// </summary>
    private sealed class StreamView(string peer, byte kind, string key, string title)
    {
        public string Peer { get; } = peer; public byte Kind { get; } = kind; public string Key { get; } = key; public string Title { get; } = title;
        public volatile StreamWindow? Window;
        public volatile bool Paused; public int Posting; public Bitmap? Pending; public readonly object Gate = new();
    }
    private readonly StageView stage = new();
    private SurfacePanel? stageCard;
    private CenterLayout? center;

    private void WireVideo()
    {
        shareButton.Click += (_, _) => ShowShareMenu();
        capture.FrameNeeded = kind => room?.WantsVideo(kind) ?? false;
        capture.Frame += packet => { var current = room; if (current is { IsDisposed: false, ConnectedCount: > 0 }) current.SendVideo(packet); };
        capture.Stopped += (kind, sequence) => Dispatch(() =>
        {
            if (kind == 1) StopShareSound();
            room?.StopVideo(kind, sequence);
            if (room is { IsDisposed: false, ConnectedCount: > 0 }) Cue(kind == 1 ? NotificationCue.ScreenShareOff : NotificationCue.CameraOff);
            RefreshControls();
        });
        capture.Error += error => Dispatch(() => { SetHint(error); RefreshControls(); });
        stage.CollapseRequested += () => SetStage(StageMode.Mini);
        stage.ExpandRequested += () => SetStage(StageMode.Full);
        stage.PopOutRequested += key => PopOut(key, fullscreen: false);
        stage.FullscreenRequested += key => PopOut(key, fullscreen: true);
        stage.StopRequested += key => StopWatching(key);
        Resize += (_, _) => UpdateStagePause(); VisibleChanged += (_, _) => UpdateStagePause();
    }

    private void ShowShareMenu()
    {
        var menu = ThemeMenuRenderer.CreateMenu();
        if (capture.ScreenActive) menu.Items.Add("Остановить демонстрацию экрана", null, (_, _) => StopScreen());
        else menu.Items.Add("Показать экран или окно…", null, async (_, _) => await ToggleScreenAsync());
        if (capture.CameraActive) menu.Items.Add("Выключить камеру", null, async (_, _) => await StopCameraAsync());
        else menu.Items.Add("Включить камеру…", null, async (_, _) => await ToggleCameraAsync());
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(shareButton, new Point(0, shareButton.Height + 4));
    }

    private async Task ToggleScreenAsync()
    {
        if (screenStarting) return;
        if (capture.ScreenActive) { StopScreen(); return; }
        if (room?.ConnectedCount is not > 0) { SetHint("Демонстрация доступна во время звонка."); return; }
        screenStarting = true;
        try
        {
            ShowWindow(); using var picker = new ShareDialog(settings) { Icon = Icon };
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Source is not { } source || room?.ConnectedCount is not > 0) return;
            picker.Save(settings); SaveSettings();
            SetHint("Подготавливаем видеокодировщик…"); await Task.Run(() => MediaRuntime.Executable);
            if (room?.ConnectedCount is not > 0) return;
            capture.Configure(picker.Fps, picker.Resolution, picker.BitrateKbps); await capture.StartScreenAsync(source);
            if (room?.ConnectedCount is not > 0) { capture.StopScreen(); return; }
            room.AnnounceStream(1, capture.ScreenSize.Width, capture.ScreenSize.Height, picker.Fps);
            Cue(NotificationCue.ScreenShareOn);
            SetHint("Экран в эфире. Участники увидят его, когда нажмут «Смотреть» — пока никто не смотрит, кадры не кодируются.");
            if (picker.IncludeSound)
            {
                var audio = shareSound = new ProcessLoopback(); room.StartShareAudio(audio.StreamId); audio.Packet += packet => { if (shareSound == audio && capture.ScreenActive) room?.SendShareAudio(packet); };
                audio.Failed += message => Dispatch(() => { if (shareSound == audio) { StopShareSound(); SetHint(message); RefreshControls(); } });
                try { await audio.StartAsync(source.Window == IntPtr.Zero ? Environment.ProcessId : source.ProcessId, source.Window != IntPtr.Zero); }
                catch (Exception ex) { StopShareSound(); SetHint("Видео включено, звук не удалось включить: " + ex.Message); }
            }
        }
        catch (Exception ex) { StopShareSound(); SetHint("Не удалось включить демонстрацию: " + ex.Message); AppLog.Error("Демонстрация не запущена", ex); }
        finally { screenStarting = false; RefreshControls(); }
    }
    private void StopScreen() { StopShareSound(); capture.StopScreen(); RefreshControls(); }
    private void StopShareSound() { var old = shareSound; shareSound = null; old?.Dispose(); if (old is not null) room?.StopShareAudio(); }

    private async Task ToggleCameraAsync()
    {
        if (cameraStarting) return;
        if (capture.CameraActive) { await StopCameraAsync(); return; }
        if (room?.ConnectedCount is not > 0) { SetHint("Камера доступна во время звонка."); return; }
        cameraStarting = true;
        try
        {
            ShowWindow(); var cameras = await VideoCapture.CamerasAsync();
            if (cameras.Count == 0) throw new InvalidOperationException("Windows не нашла доступную камеру.");
            using var dialog = new CameraDialog(cameras.Select(c => c.DisplayName).ToArray(), settings.CameraMode) { Icon = Icon };
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedCamera < 0 || room?.ConnectedCount is not > 0) return;
            settings.CameraMode = dialog.Mode; SaveSettings();
            await Task.Run(() => MediaRuntime.Executable); capture.Configure(dialog.Fps, dialog.Resolution, dialog.BitrateKbps); await capture.StartCameraAsync(cameras[dialog.SelectedCamera]);
            if (room?.ConnectedCount is not > 0) { await capture.StopCameraAsync(); return; }
            room.AnnounceStream(2, capture.CameraSize.Width, capture.CameraSize.Height, dialog.Fps); Cue(NotificationCue.CameraOn);
        }
        catch (Exception ex) { SetHint("Не удалось включить камеру: " + ex.Message); }
        finally { cameraStarting = false; RefreshControls(); }
    }
    private async Task StopCameraAsync() { await capture.StopCameraAsync(); RefreshControls(); }
    private void StopOwnVideo() { StopScreen(); _ = StopCameraAsync(); }

    // ---------------- Watching ----------------
    /// <summary>Starts watching a stream on the stage, or brings it forward if it is already being watched.</summary>
    private void ToggleWatch(string peer, string name, byte kind)
    {
        var key = peer + ":" + kind;
        if (streamWindows.TryGetValue(key, out var open))
        {
            if (open.Window is { IsDisposed: false } window) { if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal; window.Activate(); }
            else { stage.FocusOn(key); SetStage(StageMode.Full); }
            return;
        }
        if (room is not { IsDisposed: false } current) return;
        var title = name + (kind == 1 ? " · экран" : " · камера");
        var view = new StreamView(peer, kind, key, title);
        streamWindows[key] = view; stoppedSequences.TryRemove(key, out _);
        stage.Add(key, title); SetStage(StageMode.Full);
        current.Watch(peer, kind, true);
        RefreshControls();
    }
    /// <summary>Shows the stage large (chat beside it), small above the chat, or not at all.</summary>
    private void SetStage(StageMode mode)
    {
        if (stage.Count == 0) mode = StageMode.Hidden;
        if (mode != StageMode.Hidden) stage.Mode = mode;
        if (center is not null) center.Mode = mode;
        UpdateStagePause();
    }
    /// <summary>Streams on the stage are not decoded while the main window is hidden or minimised.</summary>
    private void UpdateStagePause()
    {
        var hidden = !Visible || WindowState == FormWindowState.Minimized;
        foreach (var view in streamWindows.Values) if (view.Window is null) view.Paused = hidden;
    }
    /// <summary>Stops watching: the sender stops sending to us.</summary>
    private void StopWatching(string key, bool closeWindow = true)
    {
        if (!streamWindows.TryRemove(key, out var view)) return;
        room?.Watch(view.Peer, view.Kind, false);
        if (decoders.TryRemove(key, out var decoder)) decoder.Dispose();
        lock (view.Gate) { view.Pending?.Dispose(); view.Pending = null; }
        var window = view.Window; view.Window = null;
        if (closeWindow && window is { IsDisposed: false }) window.Close();
        stage.Remove(key); if (stage.Count == 0) SetStage(StageMode.Hidden);
        if (!IsDisposed) RefreshControls();
    }
    /// <summary>Moves a stream from the stage into its own window (full screen if asked: Esc then brings it back).</summary>
    private void PopOut(string key, bool fullscreen)
    {
        if (!streamWindows.TryGetValue(key, out var view) || view.Window is not null) return;
        var window = new StreamWindow(key, view.Title) { Icon = Icon, ReturnOnLeavingFullscreen = fullscreen };
        view.Window = window; view.Paused = false;
        stage.Remove(key); if (stage.Count == 0) SetStage(StageMode.Hidden);
        window.Resize += (_, _) => { if (view.Window == window) view.Paused = window.WindowState == FormWindowState.Minimized; };
        window.LeftFullscreen += () => BeginInvoke(new Action(() => ReturnToStage(view, window)));
        window.FormClosed += (_, _) => { if (view.Window == window) StopWatching(key, closeWindow: false); window.Dispose(); };
        if (!previewMode) { window.Show(); if (fullscreen) window.ToggleFullscreen(); }
    }
    private void ReturnToStage(StreamView view, StreamWindow window)
    {
        if (view.Window != window || !streamWindows.ContainsKey(view.Key)) return;
        view.Window = null; stage.Add(view.Key, view.Title); SetStage(StageMode.Full); window.Close();
    }

    private void ReceiveVideo(string id, string name, byte[] packet)
    {
        if (packet.Length < 6) return;
        var key = id + ":" + packet[0];
        if (!streamWindows.TryGetValue(key, out var view) || view.Paused) return;
        if (stoppedSequences.TryGetValue(key, out var stoppedAt) && unchecked((int)(VideoCodec.Sequence(packet) - stoppedAt)) <= 0) return;
        if (H264Packet.IsH264(packet))
        {
            if (!H264Packet.Valid(packet)) return;
            var decoder = decoders.GetOrAdd(key, _ =>
            {
                var d = new VideoDecoder();
                d.Frame += bitmap => { if (decoders.TryGetValue(key, out var current) && current == d) Post(view, bitmap); else bitmap.Dispose(); };
                return d;
            });
            try { decoder.Submit(packet); } catch (Exception) { if (decoders.TryRemove(key, out var bad)) bad.Dispose(); }
        }
        else
        {
            // Legacy JPEG frames from very old clients.
            _ = Task.Run(() =>
            {
                try { Post(view, VideoCodec.Decode(packet)); }
                catch (Exception ex) when (ex is FormatException or ArgumentException or ExternalException or InvalidOperationException) { }
            });
        }
    }
    /// <summary>Keeps only the newest decoded frame per window; one UI post in flight at a time.</summary>
    private void Post(StreamView view, Bitmap bitmap)
    {
        lock (view.Gate) { view.Pending?.Dispose(); view.Pending = bitmap; }
        if (Interlocked.Exchange(ref view.Posting, 1) == 1) return;
        if (view.Window is { IsDisposed: true } || IsDisposed || !IsHandleCreated) { Interlocked.Exchange(ref view.Posting, 0); return; }
        try
        {
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref view.Posting, 0);
                Bitmap? image; lock (view.Gate) { image = view.Pending; view.Pending = null; }
                if (image is null) return;
                if (view.Window is { } window) { if (window.IsDisposed) image.Dispose(); else window.ShowFrame(image); }
                else if (streamWindows.TryGetValue(view.Key, out var current) && current == view) stage.ShowFrame(view.Key, image);
                else image.Dispose();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref view.Posting, 0); }
    }

    private void StopRemoteVideo(string id, byte kind, uint sequence)
    {
        var key = id + ":" + kind; stoppedSequences[key] = sequence;
        if (streamWindows.TryGetValue(key, out var view)) { SetHint(view.Title + " — трансляция завершена."); StopWatching(key); }
    }
    private void CloseEndedStreams()
    {
        var live = room?.RemoteStreams.Select(s => s.PeerId + ":" + s.Kind).ToHashSet() ?? [];
        foreach (var key in streamWindows.Keys.Where(k => !live.Contains(k)).ToArray()) StopWatching(key);
    }
    private void RemoveVideoParticipant(string id)
    {
        foreach (var view in streamWindows.Values.Where(v => v.Peer == id).ToArray()) StopWatching(view.Key);
    }
    private void StopAllVideo()
    {
        StopShareSound(); capture.StopScreen(); _ = capture.StopCameraAsync();
        foreach (var key in streamWindows.Keys.ToArray()) StopWatching(key);
        streamWindows.Clear(); stage.Clear(); SetStage(StageMode.Hidden);
        foreach (var pair in decoders) if (decoders.TryRemove(pair.Key, out var decoder)) decoder.Dispose();
        stoppedSequences.Clear();
        // The call is over: nothing may still be encoding or decoding.
        MediaRuntime.StopAll();
    }
    /// <summary>Render-only: a stream on the stage (and optionally the mini view) without a call.</summary>
    internal void ShowPreviewStage(bool mini)
    {
        stage.Clear();
        var frame = new Bitmap(1280, 720);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(26, 32, 48)); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var panel = new SolidBrush(Color.FromArgb(44, 54, 82))) g.FillRectangle(panel, 60, 60, 1160, 600);
            using (var accent = new SolidBrush(Color.FromArgb(124, 92, 236))) g.FillRectangle(accent, 120, 120, 520, 280);
            using var font = new Font("Segoe UI Semibold", 40); g.DrawString("Сборка · рейд в 21:00", font, Brushes.White, 120, 440);
        }
        stage.Add("preview:1", "Алекс · экран"); stage.ShowFrame("preview:1", frame);
        var camera = new Bitmap(640, 360); using (var g = Graphics.FromImage(camera)) g.Clear(Color.FromArgb(70, 60, 90));
        stage.Add("preview:2", "Мира · камера"); stage.ShowFrame("preview:2", camera); stage.FocusOn("preview:1");
        SetStage(mini ? StageMode.Mini : StageMode.Full);
    }
    private void UpdateVideoState()
    {
        var active = capture.ScreenActive || capture.CameraActive;
        if (stopVideoItem is not null) stopVideoItem.Enabled = active;
        if (tray is not null) tray.Text = active ? "Voicecon · идёт демонстрация" : "Voicecon";
    }
}
