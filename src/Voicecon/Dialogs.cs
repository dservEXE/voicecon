namespace Voicecon;

/// <summary>Add a friend by their VCF1 code; also shows and copies your own code.</summary>
public sealed class AddFriendDialog : ThemedDialog
{
    private readonly TextBox input = new() { Multiline = true, MaxLength = 4000, Font = UiTheme.Code };
    public string Code => input.Text;
    public AddFriendDialog(string? ownCode)
    {
        Text = "Добавить друга"; ClientSize = new Size(560, 470); StartPosition = FormStartPosition.CenterParent; Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; Padding = new Padding(10);
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 16) }; Controls.Add(card);
        var grid = Dialogs.Grid(); card.Controls.Add(grid);
        Dialogs.Row(grid, Dialogs.Label("Добавить друга по коду", UiTheme.Heading, false), 34);
        Dialogs.Row(grid, Dialogs.Label("Обменяйтесь кодами один раз — дальше звоните кнопкой. Добавить нужно взаимно.", UiTheme.Small), 24);
        Dialogs.Row(grid, new BackdropPanel(), 12);
        Dialogs.Row(grid, Dialogs.Label("ВАШ КОД"), 22);
        var own = new TextBox { ReadOnly = true, Text = ownCode ?? "Контакты ещё загружаются…", Font = UiTheme.Code };
        var copy = new SoftButton { Text = "Копировать", Glyph = UiIcon.Copy, Tone = ButtonTone.Secondary, Dock = DockStyle.Fill, Enabled = ownCode is not null };
        copy.Click += (_, _) => { if (ownCode is null) return; Clipboard.SetText(ownCode); copy.Text = "Скопировано"; copy.Invalidate(); };
        var ownRow = Dialogs.Columns(-1, 10, 150); ownRow.Controls.Add(Dialogs.Input(own, false), 0, 0); ownRow.Controls.Add(copy, 2, 0); Dialogs.Row(grid, ownRow, 46);
        Dialogs.Row(grid, new BackdropPanel(), 14);
        Dialogs.Row(grid, Dialogs.Label("КОД ДРУГА · начинается с VCF1."), 22);
        Dialogs.Row(grid, Dialogs.Input(input, true), 140);
        Dialogs.Row(grid, new BackdropPanel(), 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        var ok = new SoftButton { Text = "Добавить друга", Glyph = UiIcon.PersonAdd, Tone = ButtonTone.Primary, DialogResult = DialogResult.OK, Dock = DockStyle.Fill };
        var cancel = new SoftButton { Text = "Отмена", Tone = ButtonTone.Quiet, DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
        var actions = Dialogs.Columns(-35, 10, -65); actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(ok, 2, 0); Dialogs.Row(grid, actions, 48);
        AcceptButton = ok; CancelButton = cancel; UiPainting.ApplyBackgrounds(this); input.ForeColor = own.ForeColor = UiTheme.Current.Text;
        Shown += (_, _) => input.Focus();
    }
}

/// <summary>Name a group and pick up to five friends.</summary>
public sealed class GroupDialog : ThemedDialog
{
    private readonly TextBox nameInput = new() { MaxLength = 40 };
    private readonly List<(ToggleRow Toggle, string Id)> checks = [];
    public string GroupName => nameInput.Text;
    public IEnumerable<string> Members => checks.Where(c => c.Toggle.Checked).Select(c => c.Id);
    public GroupDialog(string title, string name, IReadOnlyList<FriendCard> friends, IReadOnlyCollection<string> members)
    {
        Text = title; ClientSize = new Size(500, 560); StartPosition = FormStartPosition.CenterParent; Font = UiTheme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; Padding = new Padding(10);
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 16, 16) }; Controls.Add(card);
        var grid = Dialogs.Grid(); card.Controls.Add(grid);
        Dialogs.Row(grid, Dialogs.Label(title, UiTheme.Heading, false), 34);
        Dialogs.Row(grid, Dialogs.Label("Звонок группе вызывает всех сразу, до 5 друзей. История чата общая.", UiTheme.Small), 24);
        Dialogs.Row(grid, new BackdropPanel(), 10);
        Dialogs.Row(grid, Dialogs.Label("НАЗВАНИЕ"), 22); nameInput.Text = name.Length > 0 ? name : "Команда"; Dialogs.Row(grid, Dialogs.Input(nameInput, false), 42);
        Dialogs.Row(grid, new BackdropPanel(), 12); Dialogs.Row(grid, Dialogs.Label("УЧАСТНИКИ"), 22);
        var host = new ThemedScrollHost { Dock = DockStyle.Fill }; var list = Dialogs.Grid(); list.Dock = DockStyle.Top; list.AutoSize = true; list.AutoSizeMode = AutoSizeMode.GrowAndShrink; host.SetContent(list);
        var ok = new SoftButton { Text = "Сохранить", Glyph = UiIcon.Group, Tone = ButtonTone.Primary, DialogResult = DialogResult.OK, Dock = DockStyle.Fill };
        foreach (var friend in friends)
        {
            var toggle = new ToggleRow { Text = friend.Name, Checked = members.Contains(friend.Id) }; Dialogs.Row(list, toggle, 40); checks.Add((toggle, friend.Id));
            toggle.CheckedChanged += (_, _) => Limit();
        }
        Dialogs.Row(grid, host, 0); grid.RowStyles[^1].SizeType = SizeType.Percent; grid.RowStyles[^1].Height = 100;
        var cancel = new SoftButton { Text = "Отмена", Tone = ButtonTone.Quiet, DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
        var actions = Dialogs.Columns(-35, 10, -65); actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(ok, 2, 0); Dialogs.Row(grid, new BackdropPanel(), 10); Dialogs.Row(grid, actions, 48);
        void Limit()
        {
            var count = checks.Count(c => c.Toggle.Checked);
            foreach (var (toggle, _) in checks) toggle.Enabled = toggle.Checked || count < VoiceRoom.Capacity - 1;
            ok.Enabled = count > 0 && nameInput.Text.Trim().Length > 0; ok.Invalidate();
        }
        nameInput.TextChanged += (_, _) => Limit(); Limit();
        AcceptButton = ok; CancelButton = cancel; UiPainting.ApplyBackgrounds(this); nameInput.ForeColor = UiTheme.Current.Text;
    }
}

internal static class Dialogs
{
    public static BufferedGrid Grid() { var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty }; grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return grid; }
    public static BufferedGrid Columns(params int[] widths)
    {
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = widths.Length, Margin = Padding.Empty }; grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var w in widths) grid.ColumnStyles.Add(new ColumnStyle(w > 0 ? SizeType.Absolute : SizeType.Percent, Math.Abs(w))); return grid;
    }
    public static void Row(TableLayoutPanel grid, Control child, int height) { var row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); child.Dock = DockStyle.Fill; child.Margin = Padding.Empty; grid.Controls.Add(child, 0, row); }
    public static UiLabel Label(string text, Font? font = null, bool muted = true) => new() { Text = text, Font = font ?? UiTheme.Caption, ForeColor = muted ? UiTheme.Current.Muted : UiTheme.Current.Text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    public static SurfacePanel Input(TextBox input, bool multiline)
    {
        var surface = new SurfacePanel { Recessed = true, Radius = multiline ? 16 : 12, Padding = new Padding(12, multiline ? 9 : 10, 10, 5), Dock = DockStyle.Fill };
        input.BorderStyle = BorderStyle.None; input.Dock = DockStyle.Fill; input.Margin = Padding.Empty; surface.Controls.Add(input); return surface;
    }
}
