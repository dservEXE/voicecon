namespace Voicecon;
public enum VideoViewMode { Normal, Wide, Fullscreen }
public sealed class VideoPictureBox : PictureBox
{
    public void MoveTo(Control parent)
    {
        _ = parent.Handle;
        parent.Controls.Add(this); Dock = DockStyle.Fill; Visible = true;
        // An unopened/hidden destination may not have reparented an existing HWND.
        // Recreate only the display surface on mode changes; its image/decoder stay intact.
        if (IsHandleCreated) RecreateHandle();
    }
}

/// <summary>Hosts the existing image surface. Never clones frames or creates another decoder.</summary>
public sealed class VideoViewer : Form
{
    private readonly PictureBox picture;
    public event Action? ReturnRequested;
    public VideoViewer(PictureBox picture, string title)
    {
        this.picture = picture; Text = title; BackColor = Color.Black; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true;
        var header = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 6, 12, 6), BackColor = UiTheme.Current.Background };
        var back = new SoftButton { Text = "В окно · Esc", Dock = DockStyle.Right, Width = 160 }; back.Click += (_, _) => ReturnRequested?.Invoke();
        var label = new Label { Text = title, Dock = DockStyle.Fill, ForeColor = UiTheme.Current.Text, Font = UiTheme.Body, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        header.Controls.Add(label); header.Controls.Add(back); _ = Handle; ((VideoPictureBox)picture).MoveTo(this); Controls.Add(header);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    { if (keyData is Keys.Escape or Keys.F11) { ReturnRequested?.Invoke(); return true; } return base.ProcessCmdKey(ref msg, keyData); }
    private void Detach() { if (Contains(picture)) picture.Parent?.Controls.Remove(picture); }
    protected override void OnFormClosing(FormClosingEventArgs e) { Detach(); base.OnFormClosing(e); }
    protected override void Dispose(bool disposing) { if (disposing) Detach(); base.Dispose(disposing); }
}

public sealed partial class MainForm
{
    private readonly Panel videoHost = new(), wideVideoHost = new(), widePictureHost = new();
    private readonly Label wideVideoTitle = new();
    private readonly Dictionary<string, Control> pictureHomes = [], videoCards = [];
    private readonly Dictionary<string, string> videoTitles = [];
    private string? expandedVideo;
    private VideoViewMode videoMode;
    private VideoViewer? viewer;
    private bool changingVideoMode;
    private VideoViewMode fullscreenReturn;
    private Control BuildVideoHost()
    {
        videoHost.Dock = DockStyle.Fill; videoTiles.Dock = DockStyle.Fill; videoHost.Controls.Add(videoTiles);
        var grid = Grid(1); var header = Columns(110, -1, 150);
        var normal = new SoftButton { Text = "К плиткам", Tone = ButtonTone.Quiet, Dock = DockStyle.Fill }; normal.Click += (_, _) => SetVideoMode(expandedVideo, VideoViewMode.Normal);
        var full = new SoftButton { Text = "Полный экран", Tone = ButtonTone.Primary, Dock = DockStyle.Fill }; full.Click += (_, _) => SetVideoMode(expandedVideo, VideoViewMode.Fullscreen);
        PrepareLabel(wideVideoTitle, TextTone.Accent); header.Controls.Add(normal, 0, 0); header.Controls.Add(wideVideoTitle, 1, 0); header.Controls.Add(full, 2, 0); AddRow(grid, header, 40); AddRow(grid, Space(), 8);
        widePictureHost.BackColor = Color.Black; AddRow(grid, widePictureHost, 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        wideVideoHost.Dock = DockStyle.Fill; wideVideoHost.Controls.Add(grid); videoHost.Controls.Add(wideVideoHost); wideVideoHost.Visible = false; return videoHost;
    }
    private void SetVideoMode(string? key, VideoViewMode mode)
    {
        if (changingVideoMode) return;
        if (key is null || !pictures.ContainsKey(key)) mode = VideoViewMode.Normal;
        var returnMode = videoMode == VideoViewMode.Wide ? VideoViewMode.Wide : VideoViewMode.Normal;
        changingVideoMode = true;
        try
        {
            if (viewer is { } old) { viewer = null; old.Close(); old.Dispose(); }
            if (expandedVideo is { } previous && pictures.TryGetValue(previous, out var former) && pictureHomes.TryGetValue(previous, out var home) && !home.IsDisposed) ((VideoPictureBox)former).MoveTo(home);
            wideVideoHost.Visible = false; videoTiles.Visible = true; videoMode = mode; expandedVideo = mode == VideoViewMode.Normal ? null : key;
            if (mode == VideoViewMode.Normal) return;
            var picture = pictures[key!]; var title = videoTitles.GetValueOrDefault(key!, "Видео");
            if (mode == VideoViewMode.Wide)
            {
                wideVideoTitle.Text = "  " + title; ((VideoPictureBox)picture).MoveTo(widePictureHost);
                videoTiles.Visible = false; wideVideoHost.Visible = true; wideVideoHost.BringToFront();
            }
            else
            {
                fullscreenReturn = returnMode;
                var full = viewer = new VideoViewer(picture, title) { Icon = Icon, Bounds = Screen.FromControl(this).Bounds, StartPosition = FormStartPosition.Manual };
                full.ReturnRequested += () => SetVideoMode(key, fullscreenReturn);
                full.FormClosed += (_, _) => { if (ReferenceEquals(viewer, full)) { viewer = null; SetVideoMode(key, fullscreenReturn); } };
                if (!previewMode) full.Show(this);
            }
        }
        finally { changingVideoMode = false; }
    }
    private void CloseExpandedVideo() => SetVideoMode(expandedVideo, VideoViewMode.Normal);
}
