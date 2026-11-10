namespace Voicecon;

/// <summary>
/// A separate, resizable window for one remote stream. Closing it stops watching (the sender stops transmitting to us).
/// Double-click, F11 or the button toggle fullscreen on the current monitor; Esc returns.
/// </summary>
public sealed class StreamWindow : ThemedDialog
{
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly BackdropPanel header = new() { Dock = DockStyle.Top };
    private readonly UiLabel titleLabel = new(), infoLabel = new();
    private readonly SoftButton fullButton = new() { Glyph = UiIcon.Expand, Tone = ButtonTone.Quiet, Circle = true, AccessibleName = "Во весь экран" };
    private bool fullscreen; private Rectangle restoreBounds; private FormWindowState restoreState;
    private long frames, lastFrame; private string waiting = "Подключаем трансляцию… первый кадр появится через секунду";
    public string Key { get; }
    public bool HasImage => picture.Image is not null;
    public bool Paused => WindowState == FormWindowState.Minimized || !Visible;

    public StreamWindow(string key, string title)
    {
        Key = key; var s = DeviceDpi / 96f;
        Text = title; ClientSize = new Size(960, 600); MinimumSize = new Size(480, 320); StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi; ShowInTaskbar = true;
        header.Height = 52; header.Padding = new Padding(16, 6, 10, 6);
        titleLabel.Text = title; titleLabel.Font = UiTheme.Heading; titleLabel.ForeColor = UiTheme.Current.Text; titleLabel.Dock = DockStyle.Fill; titleLabel.TextAlign = ContentAlignment.MiddleLeft; titleLabel.AutoEllipsis = true;
        infoLabel.Font = UiTheme.Small; infoLabel.ForeColor = UiTheme.Current.Muted; infoLabel.Dock = DockStyle.Right; infoLabel.Width = 220; infoLabel.TextAlign = ContentAlignment.MiddleRight;
        fullButton.Dock = DockStyle.Right; fullButton.Width = 44;
        header.Controls.Add(titleLabel); header.Controls.Add(infoLabel); header.Controls.Add(fullButton);
        Controls.Add(picture); Controls.Add(header);
        fullButton.Click += (_, _) => ToggleFullscreen();
        picture.DoubleClick += (_, _) => ToggleFullscreen();
        picture.Paint += (_, e) =>
        {
            if (picture.Image is not null) return;
            TextRenderer.DrawText(e.Graphics, waiting, UiTheme.Body, picture.ClientRectangle, Color.FromArgb(170, 255, 255, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        };
        UiPainting.ApplyBackgrounds(this); picture.BackColor = Color.Black;
    }

    /// <summary>Takes ownership of the bitmap.</summary>
    public void ShowFrame(Bitmap image)
    {
        if (IsDisposed) { image.Dispose(); return; }
        var old = picture.Image; picture.Image = image; old?.Dispose();
        frames++; var now = Environment.TickCount64;
        if (now - lastFrame > 1000) { infoLabel.Text = $"{image.Width}×{image.Height}"; lastFrame = now; }
    }
    public void ShowMessage(string text) { waiting = text; var old = picture.Image; picture.Image = null; old?.Dispose(); picture.Invalidate(); }

    public void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            restoreBounds = Bounds; restoreState = WindowState; fullscreen = true;
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; header.Visible = false;
            Bounds = Screen.FromControl(this).Bounds; TopMost = true;
        }
        else
        {
            fullscreen = false; TopMost = false; FormBorderStyle = FormBorderStyle.Sizable; header.Visible = true;
            Bounds = restoreBounds; WindowState = restoreState;
        }
        fullButton.Glyph = fullscreen ? UiIcon.Collapse : UiIcon.Expand; fullButton.Invalidate();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11 || keyData == Keys.Escape && fullscreen) { ToggleFullscreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { var image = picture.Image; picture.Image = null; image?.Dispose(); }
        base.Dispose(disposing);
    }
}
