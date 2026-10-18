using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace MediaCenter.App.Services;

/// <summary>
/// Prüft, ob das Fenster wirklich etwas zeigt. Auf manchen Rechnern (belegt: Intel Arc plus NVIDIA) bleibt jedes WPF-Fenster im Hardware-Rendering
/// weiß, ohne dass WPF das merkt. Der Test nimmt das Bild des Fensters so auf, wie der Windows-Desktop es zusammensetzt (Windows Graphics Capture),
/// und sieht nach, ob es überhaupt Inhalt hat: eine Oberfläche mit Text, Karten und Verläufen ist nie einfarbig.
/// </summary>
internal static class RenderSelfTest
{
    private const int GridColumns = 48;
    private const int GridRows = 28;

    /// <summary>Anteil gleicher Stichproben, ab dem das Bild als leer gilt.</summary>
    private const double BlankFraction = 0.995;

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    private static class Native
    {
        [DllImport("combase.dll")] public static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

        [DllImport("combase.dll")] public static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")] public static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

        [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

        [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }

    /// <returns>true, wenn das Bild des Fensters leer (einfarbig) ist.</returns>
    /// <exception cref="InvalidOperationException">Das Bild ließ sich nicht aufnehmen.</exception>
    public static async Task<bool> IsBlankAsync(IntPtr window)
    {
        var png = await CapturePngAsync(window);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        // Stichproben im Inneren: ohne Titelleiste und Rand.
        var colors = new Dictionary<int, int>();
        var top = Math.Min(height / 4, 120);
        for (var row = 0; row < GridRows; row++)
        {
            for (var column = 0; column < GridColumns; column++)
            {
                var x = 12 + (width - 24) * column / GridColumns;
                var y = top + (height - top - 12) * row / GridRows;
                var i = y * stride + x * 4;
                var color = pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16;
                colors[color] = colors.GetValueOrDefault(color) + 1;
            }
        }
        return colors.Values.Max() >= GridColumns * GridRows * BlankFraction;
    }

    private static async Task<byte[]> CapturePngAsync(IntPtr window)
    {
        var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        Check(Native.WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", "Windows.Graphics.Capture.GraphicsCaptureItem".Length, out var className), "WindowsCreateString");
        Check(Native.RoGetActivationFactory(className, ref interopId, out var factory), "Aufnahme-Dienst von Windows nicht verfügbar");
        Native.WindowsDeleteString(className);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
        var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(interop.CreateForWindow(window, ref itemId));

        Check(Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */, IntPtr.Zero, 0, 7, out var d3d, out _, out _), "Grafikgerät nicht erstellt");
        var dxgiId = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        Marshal.QueryInterface(d3d, ref dxgiId, out var dxgi);
        Check(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var graphicsDevice), "Grafikgerät für die Aufnahme nicht erstellt");
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>();
        // Ein ruhendes Fenster liefert nur ein Bild beim Start der Aufnahme.
        pool.FrameArrived += (sender, _) =>
        {
            if (sender.TryGetNextFrame() is { } next && !arrived.TrySetResult(next)) next.Dispose();
        };
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
        if (await Task.WhenAny(arrived.Task, Task.Delay(5000)) != arrived.Task) throw new InvalidOperationException("Windows lieferte kein Fensterbild (Zeitüberschreitung).");

        using var frame = arrived.Task.Result;
        var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void Check(int hresult, string what)
    {
        if (hresult < 0) throw new InvalidOperationException($"{what} (HRESULT 0x{hresult:X}).");
    }
}
