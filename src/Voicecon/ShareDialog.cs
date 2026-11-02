namespace Voicecon;
public sealed class ShareDialog : ThemedDialog
{
    private readonly ThemedComboBox mode = new(), sources = new(), resolution = new(), frames = new();
    private readonly ToggleRow sound = new(); private readonly SoftButton start = new() { Text = "Начать демонстрацию", Tone = ButtonTone.Primary, DialogResult = DialogResult.OK };
    private readonly Settings settings;
    public ShareSource? Source => sources.SelectedItem as ShareSource;
    public int Fps => frames.SelectedIndex switch { 1 => 60, 2 => 120, _ => 30 };
    public int Resolution => resolution.SelectedIndex switch { 0 => 720, 1 => 1080, _ => 0 };
    public bool IncludeSound => sound.Checked;
    public ShareDialog(Settings settings)
    {
        this.settings = settings; Text = "Что показать участникам?"; ClientSize = new Size(580, 490); Font = UiTheme.Body; BackColor = UiTheme.Current.Background;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        var grid = new BufferedGrid { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 0 }; grid.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(grid);
        void Row(Control child, int height) { var row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.Absolute, height)); child.Dock = DockStyle.Fill; child.Margin = Padding.Empty; grid.Controls.Add(child, 0, row); }
        Label Label(string text) => new() { Text = text, ForeColor = UiTheme.Current.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = UiTheme.Small };
        Row(Label("Источник"), 26); mode.Items.AddRange(["Экран", "Окно", "Приложение"]); Row(mode, 38); Row(new Panel(), 10); Row(sources, 40);
        Row(Label("Приложение: основное окно и звук его процесса с дочерними процессами."), 36);
        Row(Label("Разрешение · без растягивания изображения"), 26); Row(resolution, 38);
        Row(Label("Целевая частота · фактическая зависит от источника и нагрузки"), 26); frames.Items.AddRange(["30 кадров/с", "60 кадров/с", "120 кадров/с"]); frames.SelectedIndex = settings.VideoFps switch { 60 => 1, 120 => 2, _ => 0 }; Row(frames, 38);
        sound.Text = "Передавать звук"; sound.Checked = settings.ShareSound; sound.Enabled = ProcessLoopback.Supported; Row(sound, 64); Row(new Panel(), 12); Row(start, 44); Row(new Panel(), 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        mode.SelectedIndexChanged += (_, _) => Reload(); sources.SelectedIndexChanged += (_, _) => UpdateResolution(); mode.SelectedIndex = 0;
        AcceptButton = start; UiPainting.ApplyBackgrounds(this);
    }
    private void Reload()
    {
        sources.Items.Clear(); sources.Items.AddRange(ShareSources.Enumerate(mode.SelectedItem?.ToString() ?? "Экран").ToArray()); start.Enabled = sources.Items.Count > 0;
        if (sources.Items.Count > 0) sources.SelectedIndex = 0; else { resolution.Items.Clear(); resolution.Items.Add("Нет доступных окон"); resolution.SelectedIndex = 0; }
        sound.Description = !ProcessLoopback.Supported ? "Звук приложения требует Windows 11 / сборку 20348+" : mode.SelectedIndex == 0 ? "Звук системы, кроме Voicecon — без эха звонка" : "Только выбранное приложение и его дочерние процессы";
    }
    private void UpdateResolution()
    {
        var old = resolution.SelectedIndex; var size = Source?.Size ?? Size.Empty;
        resolution.Items.Clear(); resolution.Items.AddRange(["720p · высота до 720", "1080p · высота до 1080", $"Оригинал ({size.Width}×{size.Height})"]);
        resolution.SelectedIndex = old >= 0 ? old : settings.VideoResolution switch { 1080 => 1, 0 => 2, _ => 0 };
    }
}
