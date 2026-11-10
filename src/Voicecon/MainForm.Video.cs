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

    /// <summary>Thread-safe view state for one open stream window (frames arrive on network threads).</summary>
    private sealed class StreamView(StreamWindow window, string peer, byte kind)
    {
        public StreamWindow Window { get; } = window; public string Peer { get; } = peer; public byte Kind { get; } = kind;
        public volatile bool Paused; public int Posting; public Bitmap? Pending; public readonly object Gate = new();
    }

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
    private void ToggleWatch(string peer, string name, byte kind)
    {
        var key = peer + ":" + kind;
        if (streamWindows.TryGetValue(key, out var open)) { if (open.Window.WindowState == FormWindowState.Minimized) open.Window.WindowState = FormWindowState.Normal; open.Window.Activate(); return; }
        if (room is not { IsDisposed: false } current) return;
        var window = new StreamWindow(key, name + (kind == 1 ? " · экран" : " · камера")) { Icon = Icon };
        var view = new StreamView(window, peer, kind);
        streamWindows[key] = view; stoppedSequences.TryRemove(key, out _);
        window.Resize += (_, _) => view.Paused = window.WindowState == FormWindowState.Minimized;
        window.FormClosed += (_, _) =>
        {
            if (streamWindows.TryRemove(key, out _)) { room?.Watch(peer, kind, false); }
            if (decoders.TryRemove(key, out var decoder)) decoder.Dispose();
            lock (view.Gate) { view.Pending?.Dispose(); view.Pending = null; }
            window.Dispose(); RefreshControls();
        };
        current.Watch(peer, kind, true);
        if (!previewMode) window.Show();
        RefreshControls();
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
        if (view.Window.IsDisposed || !IsHandleCreated) { Interlocked.Exchange(ref view.Posting, 0); return; }
        try
        {
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref view.Posting, 0);
                Bitmap? image; lock (view.Gate) { image = view.Pending; view.Pending = null; }
                if (image is null) return;
                if (view.Window.IsDisposed) { image.Dispose(); return; }
                view.Window.ShowFrame(image);
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref view.Posting, 0); }
    }

    private void StopRemoteVideo(string id, byte kind, uint sequence)
    {
        var key = id + ":" + kind; stoppedSequences[key] = sequence;
        if (streamWindows.TryGetValue(key, out var view)) { view.Window.Close(); SetHint(view.Window.Text + " — трансляция завершена."); }
    }
    private void CloseEndedStreams()
    {
        var live = room?.RemoteStreams.Select(s => s.PeerId + ":" + s.Kind).ToHashSet() ?? [];
        foreach (var view in streamWindows.Values.Where(v => !live.Contains(v.Window.Key)).ToArray()) view.Window.Close();
    }
    private void RemoveVideoParticipant(string id)
    {
        foreach (var view in streamWindows.Values.Where(v => v.Peer == id).ToArray()) view.Window.Close();
    }
    private void StopAllVideo()
    {
        StopShareSound(); capture.StopScreen(); _ = capture.StopCameraAsync();
        foreach (var view in streamWindows.Values.ToArray()) { if (!view.Window.IsDisposed) view.Window.Close(); }
        streamWindows.Clear();
        foreach (var pair in decoders) if (decoders.TryRemove(pair.Key, out var decoder)) decoder.Dispose();
        stoppedSequences.Clear();
    }
    private void UpdateVideoState()
    {
        var active = capture.ScreenActive || capture.CameraActive;
        if (stopVideoItem is not null) stopVideoItem.Enabled = active;
        if (tray is not null) tray.Text = active ? "Voicecon · идёт демонстрация" : "Voicecon";
    }
}
