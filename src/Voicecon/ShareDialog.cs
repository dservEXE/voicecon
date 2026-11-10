namespace Voicecon;

/// <summary>Choose what to share and how: optimized (light), quality, or custom fps / bitrate / resolution.</summary>
public sealed class ShareDialog : ThemedDialog
{
    public const int OptimizedFps = 30, OptimizedHeight = 720, OptimizedKbps = 2000;
    public const int QualityFps = 60, QualityHeight = 1080, QualityKbps = 6000;
    private readonly SegmentedPicker kind = new(), mode = new(), frames = new();
    private readonly ThemedComboBox sources = new(), resolution = new();
    private readonly SlimSlider bitrate = new() { Minimum = 500, Maximum = 20000, Step = 500, AccessibleName = "Битрейт" };
    private readonly UiLabel bitrateValue = new(), estimate = new();
    private readonly ToggleRow sound = new();
    private readonly SoftButton start = new() { Text = "Начать демонстрацию", Glyph = UiIcon.Screen, Tone = ButtonTone.Primary, DialogResult = DialogResult.OK };
    private readonly SoftButton cancel = new() { Text = "Отмена", Tone = ButtonTone.Quiet, DialogResult = DialogResult.Cancel };
    private readonly BufferedGrid custom;
    private readonly RowStyle customRow;
    private readonly Settings settings;
    private static readonly string[] Kinds = ["Экран", "Окно", "Приложение"];
    public ShareSource? Source => sources.SelectedItem as ShareSource;
    public string Mode => mode.SelectedIndex switch { 1 => "quality", 2 => "custom", _ => "optimized" };
    public int Fps => mode.SelectedIndex switch { 0 => OptimizedFps, 1 => QualityFps, _ => frames.SelectedIndex switch { 0 => 15, 1 => 30, 3 => 120, _ => 60 } };
    public int Resolution => mode.SelectedIndex switch { 0 => OptimizedHeight, 1 => QualityHeight, _ => resolution.SelectedIndex switch { 0 => 720, 1 => 1080, 2 => 1440, _ => 0 } };
    public int BitrateKbps => mode.SelectedIndex switch { 0 => OptimizedKbps, 1 => QualityKbps, _ => bitrate.Value };
    public bool IncludeSound => sound.Checked;

    public ShareDialog(Settings settings)
    {
        this.settings = settings; Text = "Демонстрация экрана"; ClientSize = new Size(600, 730); Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) }; Padding = new Padding(10); Controls.Add(card);
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 1 }; grid.ColumnStyles.Add(new(SizeType.Percent, 100)); card.Controls.Add(grid);
        void Row(TableLayoutPanel target, Control child, int height) { var row = target.RowCount++; target.RowStyles.Add(new(SizeType.Absolute, height)); child.Dock = DockStyle.Fill; child.Margin = Padding.Empty; target.Controls.Add(child, 0, row); }
        UiLabel Label(string text, Font? font = null, bool muted = true) => new() { Text = text, ForeColor = muted ? UiTheme.Current.Muted : UiTheme.Current.Text, TextAlign = ContentAlignment.MiddleLeft, Font = font ?? UiTheme.Caption, AutoEllipsis = true };
        Row(grid, Label("Что показать участникам?", UiTheme.Heading, false), 34);
        Row(grid, Label("Передача начнётся только после кнопки ниже. Видео получают лишь те, кто нажмёт «Смотреть».", UiTheme.Small), 24);
        Row(grid, new BackdropPanel(), 8);
        Row(grid, Label("ИСТОЧНИК"), 22);
        foreach (var k in Kinds) kind.Options.Add(new(k)); Row(grid, kind, 42); Row(grid, new BackdropPanel(), 8); Row(grid, sources, 40);
        Row(grid, new BackdropPanel(), 10);
        Row(grid, Label("КАЧЕСТВО"), 22);
        mode.Options.Add(new("Оптимальный", "720p · 30 к/с · 2 Мбит/с")); mode.Options.Add(new("Качество", "1080p · 60 к/с · 6 Мбит/с")); mode.Options.Add(new("Свой", "к/с · битрейт · размер"));
        Row(grid, mode, 56);
        custom = new BufferedGrid { ColumnCount = 1, Margin = Padding.Empty }; custom.ColumnStyles.Add(new(SizeType.Percent, 100));
        Row(custom, new BackdropPanel(), 8);
        Row(custom, Label("РАЗРЕШЕНИЕ · без растягивания"), 22); Row(custom, resolution, 40);
        Row(custom, Label("ЧАСТОТА КАДРОВ"), 26); foreach (var f in new[] { "15", "30", "60", "120" }) frames.Options.Add(new(f + " к/с")); Row(custom, frames, 40);
        var rateHeader = new BufferedGrid { ColumnCount = 2, Margin = Padding.Empty }; rateHeader.ColumnStyles.Add(new(SizeType.Percent, 60)); rateHeader.ColumnStyles.Add(new(SizeType.Percent, 40)); rateHeader.RowStyles.Add(new(SizeType.Percent, 100));
        rateHeader.Controls.Add(Label("БИТРЕЙТ"), 0, 0); bitrateValue.TextAlign = ContentAlignment.MiddleRight; bitrateValue.ForeColor = UiTheme.Current.Accent; bitrateValue.Font = UiTheme.Strong; bitrateValue.Dock = DockStyle.Fill; rateHeader.Controls.Add(bitrateValue, 1, 0);
        Row(custom, rateHeader, 26); Row(custom, bitrate, 32);
        var customIndex = grid.RowCount; Row(grid, custom, 0); customRow = grid.RowStyles[customIndex];
        Row(grid, new BackdropPanel(), 6);
        estimate.Font = UiTheme.Small; estimate.ForeColor = UiTheme.Current.Muted; estimate.TextAlign = ContentAlignment.MiddleLeft; Row(grid, estimate, 36);
        sound.Text = "Передавать звук"; sound.Checked = settings.ShareSound; sound.Enabled = ProcessLoopback.Supported; Row(grid, sound, 54);
        Row(grid, new BackdropPanel(), 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        var actions = new BufferedGrid { ColumnCount = 3, Margin = Padding.Empty }; actions.ColumnStyles.Add(new(SizeType.Percent, 35)); actions.ColumnStyles.Add(new(SizeType.Absolute, 10)); actions.ColumnStyles.Add(new(SizeType.Percent, 65)); actions.RowStyles.Add(new(SizeType.Percent, 100));
        cancel.Dock = start.Dock = DockStyle.Fill; actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(start, 2, 0); Row(grid, actions, 48);

        bitrate.Value = settings.CustomBitrateKbps; frames.SetSilently(settings.CustomFps switch { 15 => 0, 30 => 1, 120 => 3, _ => 2 });
        mode.SetSilently(settings.ShareMode switch { "quality" => 1, "custom" => 2, _ => 0 });
        kind.SelectedIndexChanged += (_, _) => Reload(); sources.SelectedIndexChanged += (_, _) => UpdateResolution();
        mode.SelectedIndexChanged += (_, _) => UpdateMode(true); frames.SelectedIndexChanged += (_, _) => UpdateEstimate(); bitrate.ValueChanged += (_, _) => UpdateEstimate(); resolution.SelectedIndexChanged += (_, _) => UpdateEstimate();
        Reload(); UpdateMode(false);
        AcceptButton = start; CancelButton = cancel; UiPainting.ApplyBackgrounds(this);
    }
    private void UpdateMode(bool animate)
    {
        var target = mode.SelectedIndex == 2 ? 196 * DeviceDpi / 96f : 0; var startHeight = customRow.Height; custom.Visible = true;
        if (animate) UiMotion.Run(customRow, this, 220, p => { customRow.Height = startHeight + (target - startHeight) * p; }, () => custom.Visible = mode.SelectedIndex == 2);
        else { customRow.Height = target; custom.Visible = mode.SelectedIndex == 2; }
        UpdateEstimate();
    }
    private void UpdateEstimate()
    {
        bitrateValue.Text = $"{bitrate.Value / 1000.0:0.0} Мбит/с";
        var size = Resolution == 0 ? "оригинал" : Resolution + "p";
        estimate.Text = $"≈ {BitrateKbps / 1000.0:0.#} Мбит/с на каждого зрителя · {size} · {Fps} к/с. " + (Mode == "optimized" ? "Минимум нагрузки и задержки — для игр." : Mode == "quality" ? "Чётче и плавнее, нужна быстрая сеть." : "");
    }
    private void Reload()
    {
        sources.Items.Clear(); sources.Items.AddRange(ShareSources.Enumerate(Kinds[kind.SelectedIndex]).ToArray()); start.Enabled = sources.Items.Count > 0;
        if (sources.Items.Count > 0) sources.SelectedIndex = 0; else { resolution.Items.Clear(); resolution.Items.Add("Нет доступных окон"); resolution.SelectedIndex = 0; }
        sound.Description = !ProcessLoopback.Supported ? "Звук приложения требует Windows 11 / сборку 20348+" : kind.SelectedIndex == 0 ? "Звук системы, кроме Voicecon — без эха звонка" : "Только выбранное приложение и его дочерние процессы";
        sound.Invalidate();
    }
    private void UpdateResolution()
    {
        var old = resolution.SelectedIndex; var size = Source?.Size ?? Size.Empty;
        resolution.Items.Clear(); resolution.Items.AddRange(["720p", "1080p", "1440p", $"Оригинал ({size.Width}×{size.Height})"]);
        resolution.SelectedIndex = old >= 0 && old < 4 ? old : settings.CustomResolution switch { 720 => 0, 1440 => 2, 0 => 3, _ => 1 };
        UpdateEstimate();
    }
    /// <summary>Remembers the choice for next time (mode and custom values).</summary>
    public void Save(Settings target)
    {
        target.ShareMode = Mode; target.ShareSound = IncludeSound;
        if (Mode == "custom") { target.CustomFps = Fps; target.CustomResolution = Resolution; target.CustomBitrateKbps = bitrate.Value; }
        target.VideoFps = Fps; target.VideoResolution = Resolution;
    }
}

/// <summary>Pick a camera and a quality preset.</summary>
public sealed class CameraDialog : ThemedDialog
{
    private readonly ThemedComboBox list = new();
    private readonly SegmentedPicker mode = new();
    public int SelectedCamera => list.SelectedIndex;
    public string Mode => mode.SelectedIndex == 1 ? "quality" : "optimized";
    public int Fps => 30;
    public int Resolution => mode.SelectedIndex == 1 ? 1080 : 720;
    public int BitrateKbps => mode.SelectedIndex == 1 ? 4000 : 1500;
    public CameraDialog(IReadOnlyList<string> cameras, string current)
    {
        Text = "Камера"; ClientSize = new Size(520, 330); Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) }; Padding = new Padding(10); Controls.Add(card);
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 1 }; grid.ColumnStyles.Add(new(SizeType.Percent, 100)); card.Controls.Add(grid);
        void Row(Control child, int height) { var row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.Absolute, height)); child.Dock = DockStyle.Fill; child.Margin = Padding.Empty; grid.Controls.Add(child, 0, row); }
        Row(new UiLabel { Text = "Включить камеру", Font = UiTheme.Heading, ForeColor = UiTheme.Current.Text, TextAlign = ContentAlignment.MiddleLeft }, 34);
        Row(new UiLabel { Text = "Видео увидят только те, кто нажмёт «Смотреть».", Font = UiTheme.Small, ForeColor = UiTheme.Current.Muted, TextAlign = ContentAlignment.MiddleLeft }, 24);
        list.Items.AddRange(cameras.Cast<object>().ToArray()); list.SelectedIndex = cameras.Count > 0 ? 0 : -1; Row(new BackdropPanel(), 8); Row(list, 40); Row(new BackdropPanel(), 12);
        mode.Options.Add(new("Оптимальный", "720p · 1,5 Мбит/с")); mode.Options.Add(new("Качество", "1080p · 4 Мбит/с")); mode.SetSilently(current == "quality" ? 1 : 0); Row(mode, 56);
        Row(new BackdropPanel(), 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        var actions = new BufferedGrid { ColumnCount = 3, Margin = Padding.Empty }; actions.ColumnStyles.Add(new(SizeType.Percent, 35)); actions.ColumnStyles.Add(new(SizeType.Absolute, 10)); actions.ColumnStyles.Add(new(SizeType.Percent, 65)); actions.RowStyles.Add(new(SizeType.Percent, 100));
        var cancel = new SoftButton { Text = "Отмена", Tone = ButtonTone.Quiet, DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
        var start = new SoftButton { Text = "Включить камеру", Glyph = UiIcon.Camera, Tone = ButtonTone.Primary, DialogResult = DialogResult.OK, Dock = DockStyle.Fill, Enabled = cameras.Count > 0 };
        actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(start, 2, 0); Row(actions, 48);
        AcceptButton = start; CancelButton = cancel; UiPainting.ApplyBackgrounds(this);
    }
}
