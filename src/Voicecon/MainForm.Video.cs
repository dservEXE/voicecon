using System.Collections.Concurrent;
using System.Runtime.InteropServices;
namespace Voicecon;
public sealed partial class MainForm
{
    private readonly SoftButton videoNav = new(), screenButton = new(), cameraButton = new();
    private readonly ThemedComboBox videoQuality = new(), videoResolution = new();
    private readonly HotkeyBox screenKey = new(), cameraKey = new();
    private Control videoPage = null!;
    private readonly ThemedFlowList videoTiles = new();
    private readonly Label liveStatus = new();
    private readonly VideoCapture capture = new();
    private ProcessLoopback? shareSound;
    private readonly ConcurrentDictionary<string, RemoteFrame> latestFrames = new();
    private readonly ConcurrentDictionary<string, byte> decodePending = new(), unavailableVideo = new();
    private readonly ConcurrentDictionary<string, VideoDecoder> decoders = new();
    private readonly ConcurrentDictionary<string, Bitmap> decodedImages = new();
    private readonly ConcurrentDictionary<string, byte> imagePosted = new();
    private readonly object imageGate = new();
    private readonly ConcurrentDictionary<string, uint> stoppedSequences = new();
    private readonly Dictionary<string, PictureBox> pictures = [];
    private readonly Dictionary<string, uint> videoSequences = [];
    private bool cameraStarting, screenStarting;
    private volatile bool viewingVideo;
    private int videoEpoch;
    private Control BuildVideoPage()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20) }; var grid = Grid(1); card.Controls.Add(grid);
        AddRow(grid, TextLabel("Видео и демонстрация", 17, TextTone.Normal, true), 36);
        PrepareLabel(liveStatus, TextTone.Muted); liveStatus.Font = UiTheme.Small; liveStatus.Text = "Экран и камера выключены. Выберите источник перед началом передачи."; AddRow(grid, liveStatus, 34);
        var actions = Columns(-50, 12, -50); PrepareButton(screenButton, "Показать экран / окно", UiIcon.Palette); PrepareButton(cameraButton, "Включить камеру", UiIcon.Palette);
        actions.Controls.Add(screenButton, 0, 0); actions.Controls.Add(cameraButton, 2, 0); AddRow(grid, actions, 42); AddRow(grid, Space(), 12);
        videoTiles.Dock = DockStyle.Fill; videoTiles.WrapContents = true; AddRow(grid, BuildVideoHost(), 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        AddRow(grid, TextLabel("H.264 · 30 / 60 / 120 fps · 720p / 1080p / оригинал · звук по выбору", 9, TextTone.Muted), 28);
        videoNav.Click += (_, _) => ShowContent(videoPage);
        if (!previewMode)
        {
            screenButton.Click += async (_, _) => await ToggleScreenAsync(); cameraButton.Click += async (_, _) => await ToggleCameraAsync();
            screenKey.Captured += (_, _) => ChangeHotkey(screenKey, 3); cameraKey.Captured += (_, _) => ChangeHotkey(cameraKey, 4);
            videoQuality.SelectedIndexChanged += (_, _) => { settings.VideoFps = videoQuality.SelectedIndex switch { 1 => 60, 2 => 120, _ => 30 }; SaveSettings(); };
            videoResolution.SelectedIndexChanged += (_, _) => { settings.VideoResolution = videoResolution.SelectedIndex switch { 0 => 720, 1 => 1080, _ => 0 }; SaveSettings(); };
            capture.Frame += packet =>
            {
                var current = room; if (current is not { IsDisposed: false } || current.ConnectedCount == 0) return;
                current.SendVideo(packet); if (viewingVideo) ReceiveVideo("self", "Вы", packet);
            };
            capture.Stopped += (kind, sequence) => Dispatch(() => { if (kind == 1 && !capture.ScreenActive) StopShareSound(); room?.StopVideo(kind, sequence); StopRemoteVideo("self", kind, sequence); UpdateVideoState(); });
            capture.Error += error => Dispatch(() => { SetHint(error); UpdateVideoState(); });
        }
        return card;
    }
    private async Task ToggleScreenAsync()
    {
        if (screenStarting) return;
        if (capture.ScreenActive) { StopShareSound(); capture.StopScreen(); UpdateVideoState(); return; }
        if (room?.ConnectedCount is not > 0) { SetHint("Сначала подключитесь к звонку, затем включите демонстрацию."); return; }
        screenStarting = true;
        try
        {
            ShowWindow(); ShowContent(videoPage); using var picker = new ShareDialog(settings) { Icon = Icon };
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Source is not { } source || room?.ConnectedCount is not > 0) return;
            settings.VideoFps = picker.Fps; settings.VideoResolution = picker.Resolution; settings.ShareSound = picker.IncludeSound; SaveSettings();
            videoQuality.SelectedIndex = picker.Fps switch { 60 => 1, 120 => 2, _ => 0 }; videoResolution.SelectedIndex = picker.Resolution switch { 720 => 0, 1080 => 1, _ => 2 };
            liveStatus.Text = "Подготавливаем видеокодировщик…"; await Task.Run(() => MediaRuntime.Executable);
            if (room?.ConnectedCount is not > 0) return;
            capture.Configure(picker.Fps, picker.Resolution); await capture.StartScreenAsync(source);
            if (room?.ConnectedCount is not > 0) { capture.StopScreen(); return; }
            if (picker.IncludeSound)
            {
                var audio = shareSound = new ProcessLoopback(); room?.StartShareAudio(audio.StreamId); audio.Packet += packet => { if (shareSound == audio && capture.ScreenActive) room?.SendShareAudio(packet); };
                audio.Failed += message => Dispatch(() => { if (shareSound == audio) { StopShareSound(); SetHint(message); UpdateVideoState(); } });
                try { await audio.StartAsync(source.Window == IntPtr.Zero ? Environment.ProcessId : source.ProcessId, source.Window != IntPtr.Zero); }
                catch (Exception ex) { StopShareSound(); SetHint("Видео включено, звук не удалось включить: " + ex.Message); }
            }
            UpdateVideoState();
        }
        catch (Exception ex) { StopShareSound(); SetHint("Не удалось включить демонстрацию: " + ex.Message); UpdateVideoState(); }
        finally { screenStarting = false; }
    }
    private void StopShareSound() { var old = shareSound; shareSound = null; old?.Dispose(); if (old is not null) room?.StopShareAudio(); }
    private async Task ToggleCameraAsync()
    {
        if (cameraStarting) return;
        if (capture.CameraActive) { await capture.StopCameraAsync(); UpdateVideoState(); return; }
        if (room?.ConnectedCount is not > 0) { SetHint("Сначала подключитесь к звонку, затем включите камеру."); return; }
        cameraStarting = true;
        try
        {
            ShowWindow(); ShowContent(videoPage); var cameras = await VideoCapture.CamerasAsync();
            if (cameras.Count == 0) throw new InvalidOperationException("Windows не нашла доступную камеру.");
            var index = SelectSource("Включить камеру", "Участники увидят изображение камеры.\nЧастота ограничивается возможностями устройства.", cameras.Select(c => c.DisplayName).ToArray());
            if (index < 0 || room?.ConnectedCount is not > 0) return;
            await Task.Run(() => MediaRuntime.Executable); capture.Configure(settings.VideoFps, settings.VideoResolution); await capture.StartCameraAsync(cameras[index]);
            if (room?.ConnectedCount is not > 0) await capture.StopCameraAsync(); UpdateVideoState();
        }
        catch (Exception ex) { SetHint("Не удалось включить камеру: " + ex.Message); }
        finally { cameraStarting = false; }
    }
    private int SelectSource(string title, string explanation, string[] choices)
    {
        using var dialog = new ThemedDialog { Text = title, ClientSize = new Size(540, 230), BackColor = UiTheme.Current.Background, Font = UiTheme.Body, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Icon = Icon };
        var grid = Grid(1, new Padding(20)); dialog.Controls.Add(grid); AddRow(grid, TextLabel(explanation, 10, TextTone.Muted), 75);
        var list = new ThemedComboBox(); list.Items.AddRange(choices); list.SelectedIndex = 0; AddRow(grid, list, 38); AddRow(grid, Space(), 14);
        var start = new SoftButton { Text = "Начать передачу", Tone = ButtonTone.Primary, DialogResult = DialogResult.OK }; AddRow(grid, start, 42); UiPainting.ApplyBackgrounds(dialog); dialog.AcceptButton = start;
        return dialog.ShowDialog(this) == DialogResult.OK ? list.SelectedIndex : -1;
    }
    private void ReceiveVideo(string id, string name, byte[] packet)
    {
        if (packet.Length < 6 || unavailableVideo.ContainsKey(id)) return;
        var key = id + ":" + packet[0]; if (stoppedSequences.TryGetValue(key, out var stoppedAt) && unchecked((int)(VideoCodec.Sequence(packet) - stoppedAt)) <= 0) return; var frame = new RemoteFrame(name, packet);
        if (!viewingVideo) return;
        if (H264Packet.IsH264(packet))
        {
            if (!H264Packet.Valid(packet)) return;
            var kind = packet[0];
            var decoder = decoders.GetOrAdd(key, _ =>
            {
                var d = new VideoDecoder(); var epoch = videoEpoch;
                d.Frame += bitmap =>
                {
                    if (!viewingVideo || epoch != videoEpoch || !decoders.TryGetValue(key, out var active) || active != d) { bitmap.Dispose(); return; }
                    lock (imageGate) { if (decodedImages.TryRemove(key, out var old)) old.Dispose(); decodedImages[key] = bitmap; }
                    PostVideoImage(key, name, kind, epoch);
                }; return d;
            });
            try { decoder.Submit(packet); } catch (Exception) { if (decoders.TryRemove(key, out var bad)) bad.Dispose(); }
        }
        else { latestFrames[key] = frame; QueueVideo(key, frame); }
    }
    private void PostVideoImage(string key, string name, byte kind, int epoch)
    {
        if (!imagePosted.TryAdd(key, 0)) return;
        if (!IsHandleCreated || IsDisposed) { imagePosted.TryRemove(key, out _); return; }
        try { BeginInvoke(new Action(() =>
        {
            imagePosted.TryRemove(key, out _);
            Bitmap? image; lock (imageGate) if (!decodedImages.TryRemove(key, out image)) return;
            if (!viewingVideo || epoch != videoEpoch || !Visible || !videoPage.Visible || !decoders.ContainsKey(key)) { image.Dispose(); return; }
            DisplayVideo(key, name, kind, image);
        })); } catch (InvalidOperationException) { imagePosted.TryRemove(key, out _); }
    }
    private void DisplayVideo(string key, string name, byte kind, Bitmap image)
    {
        if (!pictures.TryGetValue(key, out var picture))
        {
            var tile = new SurfacePanel { Size = new Size(Math.Min(430, Math.Max(280, (videoTiles.ClientSize.Width - 28) / 2)), 318), Padding = new Padding(8), Margin = new Padding(0, 0, 12, 12) };
            var grid = Grid(1); AddRow(grid, TextLabel(name + (kind == 1 ? " · экран" : " · камера"), 10, TextTone.Accent, true), 28);
            picture = new VideoPictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black }; AddRow(grid, picture, 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
            var toolbar = Columns(-50, 8, -50); var wide = new SoftButton { Text = "Шире", Tone = ButtonTone.Quiet, Dock = DockStyle.Fill }; var full = new SoftButton { Text = "Полный экран", Tone = ButtonTone.Quiet, Dock = DockStyle.Fill };
            wide.Click += (_, _) => SetVideoMode(key, VideoViewMode.Wide); full.Click += (_, _) => SetVideoMode(key, VideoViewMode.Fullscreen); picture.DoubleClick += (_, _) => SetVideoMode(key, videoMode == VideoViewMode.Fullscreen ? fullscreenReturn : VideoViewMode.Fullscreen);
            toolbar.Controls.Add(wide, 0, 0); toolbar.Controls.Add(full, 2, 0); AddRow(grid, toolbar, 34);
            pictureHomes[key] = grid; videoCards[key] = tile; videoTitles[key] = name + (kind == 1 ? " · демонстрация" : " · камера");
            tile.Controls.Add(grid); videoTiles.Controls.Add(tile); pictures[key] = picture; UiPainting.ApplyBackgrounds(tile); picture.BackColor = Color.Black;
        }
        var old = picture.Image; picture.Image = image; old?.Dispose();
    }
    private void QueueVideo(string key, RemoteFrame frame)
    {
        if (!decodePending.TryAdd(key, 0)) return; var epoch = videoEpoch;
        _ = Task.Run(() =>
        {
            Bitmap? image = null; var posted = false;
            try
            {
                image = VideoCodec.Decode(frame.Packet); var owned = image; image = null;
                if (IsDisposed || !IsHandleCreated) { owned.Dispose(); return; }
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var sequence = VideoCodec.Sequence(frame.Packet);
                        if (epoch != videoEpoch || !viewingVideo || (videoSequences.TryGetValue(key, out var last) && unchecked((int)(sequence - last)) <= 0)) { owned.Dispose(); return; }
                        videoSequences[key] = sequence; DisplayVideo(key, frame.Name, frame.Packet[0], owned);
                    }
                    finally { decodePending.TryRemove(key, out _); }
                })); posted = true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or ExternalException or InvalidOperationException) { }
            finally { image?.Dispose(); if (!posted) decodePending.TryRemove(key, out _); }
        });
    }
    private void SuspendVideoDecoders()
    {
        videoEpoch++; foreach (var pair in decoders) if (decoders.TryRemove(pair.Key, out var decoder)) decoder.Dispose();
        lock (imageGate) foreach (var pair in decodedImages) if (decodedImages.TryRemove(pair.Key, out var image)) image.Dispose(); latestFrames.Clear();
    }
    private void StopRemoteVideo(string id, byte kind, uint sequence)
    {
        var key = id + ":" + kind; if (expandedVideo == key) CloseExpandedVideo(); stoppedSequences[key] = sequence; videoSequences[key] = sequence; latestFrames.TryRemove(key, out _);
        if (decoders.TryRemove(key, out var decoder)) decoder.Dispose(); lock (imageGate) if (decodedImages.TryRemove(key, out var image)) image.Dispose();
        if (pictures.Remove(key, out var picture)) { ReleasePictureImage(picture); if (videoCards.Remove(key, out var tile)) tile.Dispose(); pictureHomes.Remove(key); videoTitles.Remove(key); }
    }
    private static void ReleasePictureImage(PictureBox picture)
    {
        var image = picture.Image; picture.Image = null; image?.Dispose();
    }
    private void UpdateVideoState()
    {
        screenButton.Text = capture.ScreenActive ? "Остановить демонстрацию" : "Показать экран / окно"; screenButton.Selected = capture.ScreenActive;
        cameraButton.Text = capture.CameraActive ? "Выключить камеру" : "Включить камеру"; cameraButton.Selected = capture.CameraActive;
        var active = capture.ScreenActive || capture.CameraActive;
        liveStatus.Text = active ? "● ПЕРЕДАЧА · " + capture.Details + (shareSound is not null ? " · со звуком" : "") : "Экран и камера выключены.";
        liveStatus.Tag = active ? "live" : TextTone.Muted; liveStatus.ForeColor = active ? UiTheme.Current.Danger : UiTheme.Current.Muted;
        videoNav.Text = active ? "Видео ●" : "Видео"; videoNav.Invalidate(); if (stopVideoItem is not null) stopVideoItem.Enabled = active;
        if (tray is not null) tray.Text = active ? "Voicecon · идёт передача экрана / камеры" : "Voicecon";
        screenButton.Invalidate(); cameraButton.Invalidate();
    }
    private void StopAllVideo()
    {
        CloseExpandedVideo(); StopShareSound(); capture.StopScreen(); _ = capture.StopCameraAsync(); SuspendVideoDecoders();
        foreach (var picture in pictures.Values) ReleasePictureImage(picture); foreach (Control tile in videoTiles.Controls.Cast<Control>().ToArray()) tile.Dispose();
        pictures.Clear(); pictureHomes.Clear(); videoCards.Clear(); videoTitles.Clear(); latestFrames.Clear(); stoppedSequences.Clear(); videoSequences.Clear(); unavailableVideo.Clear(); UpdateVideoState();
    }
    private void RemoveVideoParticipant(string id) { unavailableVideo[id] = 0; StopRemoteVideo(id, 1, 0); StopRemoteVideo(id, 2, 0); }
    private sealed record RemoteFrame(string Name, byte[] Packet);
}
