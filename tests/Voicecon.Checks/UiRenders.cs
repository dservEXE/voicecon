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
        foreach (var old in Directory.GetFiles(directory, "*.png")) File.Delete(old);
        UiTheme.Select("graphite");
        using (var icon = BrandIcon.Create())
        {
            var assetPath = Path.GetFullPath("src/Voicecon/assets"); Directory.CreateDirectory(assetPath);
            File.WriteAllBytes(Path.Combine(assetPath, "voicecon.ico"), BrandIcon.CreateFile());
        }
        foreach (var theme in UiTheme.All)
        {
            using var form = new MainForm(true, theme.Id);
            Save(form, Path.Combine(directory, theme.Id + ".png"));
        }
        UiTheme.Select("graphite");
        using (var call = new MainForm(true, "graphite")) { Prepare(call); Invoke(call, "ShowPreviewCall"); Save(call, Path.Combine(directory, "in-call.png")); }
        using (var compact = new MainForm(true, "graphite")) { compact.Size = compact.MinimumSize; Save(compact, Path.Combine(directory, "compact.png")); }
        using (var settings = new MainForm(true, "graphite"))
        {
            // Overlapping siblings print in reverse z-order with DrawToBitmap, so the drawer is captured on its own.
            Prepare(settings); Invoke(settings, "OpenSettings");
            var drawer = (Control)settings.GetType().GetField("settingsDrawer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
            Save(drawer, Path.Combine(directory, "settings.png"));
            var host = (ThemedScrollHost)settings.GetType().GetField("settingsScroller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
            Descendants(host).OfType<ModernScrollBar>().First().Value = 1100; Save(drawer, Path.Combine(directory, "settings-middle.png"));
            Invoke(settings, "ToggleDeveloper");
            Descendants(host).OfType<ModernScrollBar>().First().Value = 100000; Save(drawer, Path.Combine(directory, "settings-bottom.png"));
        }
        using (var share = new ShareDialog(new Settings { ShareMode = "custom" })) Save(share, Path.Combine(directory, "share.png"));
        using (var camera = new CameraDialog(["Встроенная камера", "USB-камера"], "optimized")) Save(camera, Path.Combine(directory, "camera.png"));
        using (var add = new AddFriendDialog("VCF1.eyJJZCI6IjAxMjM0NTY3ODkiLCJOYW1lIjoi0JjQs9GA0L7QuiJ9")) Save(add, Path.Combine(directory, "add-friend.png"));
        using (var network = new NetworkDialog(new Settings())) Save(network, Path.Combine(directory, "network.png"));
        using (var window = new StreamWindow("peer:1", "Алекс · экран"))
        {
            window.Size = new Size(960, 600);
            var sample = new Bitmap(1280, 720); using (var graphics = Graphics.FromImage(sample)) { graphics.Clear(Color.FromArgb(23, 31, 46)); graphics.FillRectangle(Brushes.SlateBlue, 120, 100, 700, 380); graphics.DrawString("Voicecon · трансляция", UiTheme.Title, Brushes.White, 140, 520); }
            window.ShowFrame(sample); Save(window, Path.Combine(directory, "stream.png"));
        }
        var glyphs = new Bitmap(900, 80); using (var g = Graphics.FromImage(glyphs))
        {
            g.Clear(UiTheme.Current.Background); var x = 8;
            foreach (var icon in Enum.GetValues<UiIcon>().Skip(1)) { UiIcons.Draw(g, icon, new RectangleF(x, 8, 24, 24), UiTheme.Current.Text); x += 27; }
        }
        glyphs.Save(Path.Combine(directory, "icons.png"), ImageFormat.Png);
        using (var brand = new Bitmap(560, 280))
        using (var g = Graphics.FromImage(brand))
        {
            g.Clear(UiTheme.Current.Background); BrandArt.Draw(g, new RectangleF(12, 12, 256, 256));
            foreach (var (size, x) in new[] { (64, 300), (32, 390), (24, 450), (16, 500) }) BrandArt.Draw(g, new RectangleF(x, 200, size, size));
            brand.Save(Path.Combine(directory, "brand.png"), ImageFormat.Png);
        }
        Console.WriteLine("Offscreen renders saved: " + directory);
    }
    private static void Save(Control control, string path)
    {
        Prepare(control);
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        if (control is Form form)
        {
            var inset = (form.Width - form.ClientSize.Width) / 2;
            var client = new Rectangle(inset, form.Height - form.ClientSize.Height - inset, form.ClientSize.Width, form.ClientSize.Height);
            using var clientImage = image.Clone(client, PixelFormat.Format32bppArgb); clientImage.Save(path, ImageFormat.Png);
        }
        else image.Save(path, ImageFormat.Png);
        Console.WriteLine("RENDER " + Path.GetFileName(path));
    }
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, []);
    private static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Prepare(Control control)
    {
        _ = control.Handle;
        control.PerformLayout();
        foreach (Control child in control.Controls) Prepare(child);
    }
}
