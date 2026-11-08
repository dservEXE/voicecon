namespace Voicecon;

public sealed class NetworkDialog : ThemedDialog
{
    private readonly Icon brandIcon;
    public NetworkDialog(Settings settings)
    {
        var t = UiTheme.Current;
        Text = "Voicecon · настройки сети"; ClientSize = new Size(560, 680); Font = UiTheme.Body;
        BackColor = t.Background; ForeColor = t.Text; brandIcon = BrandIcon.Create(); Icon = brandIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        var root = new BufferedGrid { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, Margin = Padding.Empty };
        Controls.Add(root);
        Add(root, Caption("Настройки сети", 20, t.Text, true), 48);
        Add(root, Caption("Обычно всё работает автоматически. Свой TURN-сервер\nнужен, если сеть не пропускает прямое соединение.", 9.5f, t.Muted), 52);
        var surface = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12) };
        var fields = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        surface.Controls.Add(fields); Add(root, surface, 430);
        Add(fields, Caption("Маршрут звонка", 9, t.Muted), 26);
        var route = new ThemedComboBox(); route.Items.AddRange(["Автоматически", "Напрямую", "Через TURN"]);
        route.SelectedIndex = settings.ConnectionRoute switch { "direct" => 1, "relay" => 2, _ => 0 };
        Add(fields, route, 36); Add(fields, Caption("Для режима TURN укажите сервер ниже.", 8.5f, t.Muted), 22);
        var values = new[] { settings.StunUrl, settings.TurnUrl, settings.TurnUser, settings.TurnPassword, settings.SignalingServer };
        var labels = new[] { "STUN-сервер", "TURN-сервер · необязательно", "Логин TURN", "Пароль TURN · защищён учётной записью Windows", "Доставка входящих · сервер ntfy по HTTPS" }; var inputs = new TextBox[5];
        for (var i = 0; i < 5; i++)
        {
            Add(fields, Caption(labels[i], 9, t.Muted), 26);
            var field = new SurfacePanel { Recessed = true, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 6) };
            inputs[i] = new TextBox { Text = values[i], Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = t.Field, ForeColor = t.Text, UseSystemPasswordChar = i == 3, MaxLength = 500 };
            field.Controls.Add(inputs[i]); Add(fields, field, 36);
        }
        Add(root, Caption("Настройки применятся к следующему звонку.", 9, t.Muted), 34);
        var actions = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var cancel = new SoftButton { Text = "Отмена", DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
        var save = new SoftButton { Text = "Сохранить", Tone = ButtonTone.Primary, Dock = DockStyle.Fill };
        actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(save, 2, 0); Add(root, actions, 42); AcceptButton = save; CancelButton = cancel;
        save.Click += (_, _) =>
        {
            if (inputs[0].Text.Trim().Length > 0 && !inputs[0].Text.Trim().StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Адрес STUN должен начинаться с stun:"); return; }
            if (inputs[1].Text.Trim().Length > 0 && !inputs[1].Text.Trim().StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Укажите TURN по UDP: turn:адрес:порт"); return; }
            if (route.SelectedIndex == 2 && inputs[1].Text.Trim().Length == 0)
            { MessageBox.Show(this, "Для маршрута через TURN укажите адрес TURN-сервера."); return; }
            if (!Uri.TryCreate(inputs[4].Text.Trim(), UriKind.Absolute, out var server) || server.Scheme != "https" || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
            { MessageBox.Show(this, "Для доставки входящих нужен HTTPS-адрес сервера ntfy."); return; }
            settings.StunUrl = inputs[0].Text.Trim(); settings.TurnUrl = inputs[1].Text.Trim();
            settings.ConnectionRoute = route.SelectedIndex switch { 1 => "direct", 2 => "relay", _ => "auto" };
            settings.TurnUser = inputs[2].Text.Trim(); settings.TurnPassword = inputs[3].Text;
            settings.SignalingServer = inputs[4].Text.Trim().TrimEnd('/');
            DialogResult = DialogResult.OK; Close();
        };
        UiPainting.ApplyBackgrounds(this);
    }
    private static Label Caption(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), ForeColor = color,
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, BackColor = Color.Transparent
    };
    private static void Add(TableLayoutPanel grid, Control control, int height)
    {
        var index = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); control.Margin = Padding.Empty; control.Dock = DockStyle.Fill; grid.Controls.Add(control, 0, index);
    }
    protected override void Dispose(bool disposing) { if (disposing) brandIcon.Dispose(); base.Dispose(disposing); }
}

