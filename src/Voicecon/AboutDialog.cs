namespace Voicecon;

public sealed class AboutDialog : ThemedDialog
{
    private readonly Icon brandIcon;
    public AboutDialog()
    {
        var t = UiTheme.Current;
        Text = "О Voicecon"; ClientSize = new Size(660, 480); MinimumSize = new Size(540, 400);
        StartPosition = FormStartPosition.CenterParent; Font = UiTheme.Body; BackColor = t.Background;
        brandIcon = BrandIcon.Create(); Icon = brandIcon; DoubleBuffered = true;
        var assembly = typeof(MainForm).Assembly;
        var notices = new RichTextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = RichTextBoxScrollBars.None, BorderStyle = BorderStyle.None,
            BackColor = t.Field, ForeColor = t.Text, Font = UiTheme.Small, Text = "Voicecon " + assembly.GetName().Version?.ToString(3) + "\r\n\r\n" };
        foreach (var resource in new[] { "Voicecon.License", "Voicecon.ThirdPartyNotices" })
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is not null) { using var reader = new StreamReader(stream); notices.AppendText(reader.ReadToEnd() + "\r\n\r\n"); }
        }
        notices.SelectionStart = notices.SelectionLength = 0;
        Padding = new Padding(18); Controls.Add(new ThemedTextScroll(notices));
    }
    protected override void Dispose(bool disposing) { if (disposing) brandIcon.Dispose(); base.Dispose(disposing); }
}
