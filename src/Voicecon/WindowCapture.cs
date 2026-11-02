using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
namespace Voicecon;
public sealed record ShareSource(string Label, IntPtr Window, int ProcessId, Size Size, int MonitorIndex = -1, Rectangle MonitorBounds = default)
{
    public override string ToString() => Label;
}
public static class ShareSources
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    public static List<ShareSource> Enumerate(string mode)
    {
        if (mode == "Экран") return Screen.AllScreens.Select((s, i) => new ShareSource($"Экран {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}", IntPtr.Zero, 0, s.Bounds.Size, i, s.Bounds)).ToList();
        var windows = new List<ShareSource>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid); if (pid == Environment.ProcessId) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity); if (title.Length == 0 || !GetWindowRect(hwnd, out var rect) || rect.Right - rect.Left < 32 || rect.Bottom - rect.Top < 32) return true;
            try { using var p = Process.GetProcessById((int)pid); windows.Add(new($"{p.ProcessName} · {title}", hwnd, (int)pid, new Size(rect.Right - rect.Left, rect.Bottom - rect.Top))); } catch (ArgumentException) { }
            return true;
        }, IntPtr.Zero);
        return (mode == "Приложение" ? windows.GroupBy(w => w.ProcessId).Select(g => g.OrderByDescending(w => (long)w.Size.Width * w.Size.Height).First()).ToList() : windows).OrderBy(w => w.Label).ToList();
    }
}
public sealed class WindowCapture : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IItemInterop
    { IntPtr CreateForWindow(IntPtr window, ref Guid iid); IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid); }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IItemInterop factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    private IDirect3DDevice? device; private GraphicsCaptureItem? item; private Direct3D11CaptureFramePool? pool; private GraphicsCaptureSession? session;
    private volatile bool disposed; private int busy; private long last; private int fps; private Size output;
    public event Action<byte[]>? Frame;
    public event Action<string>? Closed;
    public Size NativeSize { get; private set; }
    public void Start(IntPtr window, int frames, Rectangle? monitor = null)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Эта Windows не поддерживает захват отдельных окон.");
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem"; WindowsCreateString(name, name.Length, out var text);
        try
        {
            var iid = typeof(IItemInterop).GUID; Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out var factory));
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var target = monitor.HasValue ? MonitorFromPoint(new Point(monitor.Value.Left + 1, monitor.Value.Top + 1), 0) : window;
            if (target == IntPtr.Zero) throw new IOException("Выбранный источник больше не доступен.");
            var ptr = monitor.HasValue ? factory.CreateForMonitor(target, ref itemIid) : factory.CreateForWindow(target, ref itemIid);
            try { item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr); } finally { Marshal.Release(ptr); Marshal.ReleaseComObject(factory); }
        }
        finally { WindowsDeleteString(text); }
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var native, out _, out var context));
        try
        {
            var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in dxgiIid, out var dxgi));
            try { Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var projected)); try { device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected); } finally { Marshal.Release(projected); } } finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(context); Marshal.Release(native); }
        fps = frames; NativeSize = output = new Size(item.Size.Width, item.Size.Height);
        item.Closed += (_, _) => Closed?.Invoke("Окно закрыто. Демонстрация остановлена.");
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        pool.FrameArrived += Arrived; session = pool.CreateCaptureSession(item); session.IsCursorCaptureEnabled = true; session.StartCapture();
    }
    private async void Arrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (disposed || Environment.TickCount64 - last < 1000.0 / fps || Interlocked.Exchange(ref busy, 1) != 0) return;
        last = Environment.TickCount64;
        try
        {
            using var frame = sender.TryGetNextFrame(); if (frame is null) return;
            // Stop on resize rather than leaking another window or silently changing encoder dimensions.
            if (frame.ContentSize.Width != output.Width || frame.ContentSize.Height != output.Height) { Closed?.Invoke("Размер окна изменился. Выберите окно заново для продолжения."); return; }
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4)); bitmap.CopyToBuffer(buffer); var bytes = new byte[buffer.Length]; using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            if (!disposed) Frame?.Invoke(bytes);
        }
        catch (Exception ex) { if (!disposed) Closed?.Invoke("Захват окна остановлен: " + ex.Message); }
        finally { Volatile.Write(ref busy, 0); }
    }
    public void Dispose() { disposed = true; session?.Dispose(); session = null; if (pool is { } old) { pool = null; old.FrameArrived -= Arrived; old.Dispose(); } device?.Dispose(); device = null; }
}
