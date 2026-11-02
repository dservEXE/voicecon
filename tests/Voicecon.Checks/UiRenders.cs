using System.Drawing.Imaging;
using Voicecon;
using System.Reflection;

internal static class UiRenders
{
    public static void Render()
    {
        // DrawToBitmap paints our own invisible controls. No Show, focus, input,
        // tray icon, hotkeys, microphone, network or settings writes are involved.
        var directory = Path.GetFullPath("artifacts/previews"); Directory.CreateDirectory(directory);
        var sdkLicense = Path.GetFullPath(".tools/licenses/WindowsSDK-license.dat");
        if (File.Exists(sdkLicense))
        {
            using var licenseText = new RichTextBox(); licenseText.LoadFile(sdkLicense, RichTextBoxStreamType.RichText);
            File.WriteAllText(Path.GetFullPath(".tools/licenses/WindowsSDK.txt"), licenseText.Text);
        }
        UiTheme.Select("graphite");
        using (var icon = BrandIcon.Create())
        {
            var assetPath = Path.GetFullPath("src/Voicecon/assets"); Directory.CreateDirectory(assetPath);
            File.WriteAllBytes(Path.Combine(assetPath, "voicecon.ico"), BrandIcon.CreateFile());
        }
        foreach (var theme in UiTheme.All)
        {
            using var form = new MainForm(true, theme.Id);
            Prepare(form);
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(directory, theme.Id + ".png"), ImageFormat.Png);
            var inset = (form.Width - form.ClientSize.Width) / 2;
            var client = new Rectangle(inset, form.Height - form.ClientSize.Height - inset, form.ClientSize.Width, form.ClientSize.Height);
            using var clientImage = image.Clone(client, PixelFormat.Format32bppArgb);
            clientImage.Save(Path.Combine(directory, theme.Id + "-client.png"), ImageFormat.Png);
            Console.WriteLine($"RENDER {theme.Id}: {image.Width}x{image.Height}");
        }
        using var compact = new MainForm(true, "graphite"); compact.Size = compact.MinimumSize;
        Prepare(compact); using var small = new Bitmap(compact.Width, compact.Height);
        compact.DrawToBitmap(small, new Rectangle(Point.Empty, small.Size)); small.Save(Path.Combine(directory, "compact.png"), ImageFormat.Png);
        UiTheme.Select("graphite"); using var network = new NetworkDialog(new Settings()); Prepare(network);
        using var networkImage = new Bitmap(network.Width, network.Height);
        network.DrawToBitmap(networkImage, new Rectangle(Point.Empty, networkImage.Size)); networkImage.Save(Path.Combine(directory, "network.png"), ImageFormat.Png);
        using var settings = new MainForm(true, "graphite");
        typeof(MainForm).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settings, [true]); Prepare(settings);
        using var settingsImage = new Bitmap(settings.Width, settings.Height); settings.DrawToBitmap(settingsImage, new Rectangle(Point.Empty, settingsImage.Size)); settingsImage.Save(Path.Combine(directory, "settings.png"), ImageFormat.Png);
                var scroll = Descendants(settings).OfType<ModernScrollBar>().First(); scroll.Value = 10000;
        using (var bottom = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(bottom, new Rectangle(Point.Empty, bottom.Size)); bottom.Save(Path.Combine(directory, "settings-bottom.png"), ImageFormat.Png); }
        using var share = new ShareDialog(new Settings { VideoResolution = 0, VideoFps = 120 }); Prepare(share);
        using (var image = new Bitmap(share.Width, share.Height)) { share.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "share.png"), ImageFormat.Png); }
        using var video = new MainForm(true, "graphite"); var page = (Control)typeof(MainForm).GetField("videoPage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(video)!;
        typeof(MainForm).GetMethod("ShowContent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(video, [page]); Prepare(video);
        using (var image = new Bitmap(video.Width, video.Height)) { video.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "video.png"), ImageFormat.Png); }
        var sample = new Bitmap(960, 540); using (var graphics = Graphics.FromImage(sample)) { graphics.Clear(Color.FromArgb(23, 31, 46)); graphics.FillRectangle(Brushes.SlateBlue, 80, 70, 500, 280); graphics.DrawString("Voicecon · просмотр демонстрации", UiTheme.Title, Brushes.White, 100, 380); }
        typeof(MainForm).GetMethod("DisplayVideo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(video, ["preview:1", "Друг", (byte)1, sample]);
        foreach (var mode in new[] { VideoViewMode.Normal, VideoViewMode.Wide, VideoViewMode.Fullscreen })
        {
            typeof(MainForm).GetMethod("SetVideoMode", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(video, ["preview:1", mode]);
            var target = mode == VideoViewMode.Fullscreen ? (Form)typeof(MainForm).GetField("viewer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(video)! : video;
            if (mode == VideoViewMode.Fullscreen) target.Size = new Size(1280, 720);
            Prepare(target); using var image = new Bitmap(target.Width, target.Height); target.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "view-" + mode.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
        }
        typeof(MainForm).GetMethod("CloseExpandedVideo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(video, []);
        Console.WriteLine("Offscreen renders saved: " + directory);
    }
    private static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Prepare(Control control)
    {
        _ = control.Handle;
        control.PerformLayout();
        foreach (Control child in control.Controls) Prepare(child);
    }
}
