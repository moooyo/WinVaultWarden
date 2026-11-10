#if NATIVE_UI_REVIEW
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace App.NativeUiReview;

internal sealed record OwnProcessCaptureImage(
    string Path,
    int Width,
    int Height,
    string WindowClass,
    string Sha256,
    bool HasVariation);

/// <summary>Captures visible HWNDs belonging exclusively to this synthetic review process.</summary>
internal static class OwnProcessNativeCapture
{
    private const uint PrintClientOnly = 0x00000001;
    private const uint PrintRenderFullContent = 0x00000002;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int MaximumDimension = 16384;
    private const long MaximumPixels = 64L * 1024 * 1024;
    private static readonly nint InvalidGdiObject = new(-1);

    internal static async Task<IReadOnlyList<OwnProcessCaptureImage>> CaptureVisibleAsync(string directory, string stem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        if (stem != System.IO.Path.GetFileName(stem)
            || stem.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The capture stem must be a plain file name.", nameof(stem));
        }

        Directory.CreateDirectory(directory);
        var images = new List<OwnProcessCaptureImage>();
        var windows = EnumerateOwnVisibleWindows();
        var index = 0;
        foreach (var window in windows)
        {
            // A window can disappear or be recycled after enumeration; ownership is checked again.
            if (!BelongsToCurrentProcess(window) || !IsWindowVisible(window)) continue;
            var windowClass = OwnWindowClass(window);
            if (windowClass is null) continue;
            var capture = CaptureOwnClient(window);
            if (capture is null) continue;

            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, $"{stem}.own-window-{index++}.png"));
            using (var stream = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)capture.Width, (uint)capture.Height, 96, 96, capture.Pixels);
                await encoder.FlushAsync();
            }
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            images.Add(new(path, capture.Width, capture.Height, windowClass, hash, capture.HasVariation));
        }
        return images;
    }

    private static IReadOnlyList<nint> EnumerateOwnVisibleWindows()
    {
        var windows = new HashSet<nint>();
        EnumWindowCallback callback = (window, _) =>
        {
            // Do not request text, class names, visibility, geometry, or pixels for other processes.
            if (BelongsToCurrentProcess(window) && IsWindowVisible(window)) windows.Add(window);
            return true;
        };
        using var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            try { _ = EnumThreadWindows((uint)thread.Id, callback, nint.Zero); }
            finally { thread.Dispose(); }
        }
        GC.KeepAlive(callback);
        return windows.ToArray();
    }

    private static bool BelongsToCurrentProcess(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var ownerProcessId);
        return ownerProcessId == (uint)Environment.ProcessId;
    }

    private static string? OwnWindowClass(nint window)
    {
        if (!BelongsToCurrentProcess(window)) return null;
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "unknown-own-window";
    }

    private static ClientPixels? CaptureOwnClient(nint window)
    {
        if (!BelongsToCurrentProcess(window) || !IsWindowVisible(window)
            || !GetClientRect(window, out var rectangle)) return null;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension
            || (long)width * height > MaximumPixels) return null;

        var sourceDc = nint.Zero;
        var memoryDc = nint.Zero;
        var bitmap = nint.Zero;
        var previousBitmap = nint.Zero;
        try
        {
            if (!BelongsToCurrentProcess(window)) return null;
            sourceDc = GetDC(window);
            if (sourceDc == nint.Zero) return null;
            memoryDc = CreateCompatibleDC(sourceDc);
            if (memoryDc == nint.Zero) return null;
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = checked((uint)(width * height * 4)),
                },
            };
            bitmap = CreateDIBSection(sourceDc, ref info, DibRgbColors, out var pixelsAddress, nint.Zero, 0);
            if (bitmap == nint.Zero || pixelsAddress == nint.Zero) return null;
            previousBitmap = SelectObject(memoryDc, bitmap);
            if (previousBitmap == nint.Zero || previousBitmap == InvalidGdiObject) return null;

            // PrintWindow is the only window-rendering request and is limited to our own HWND.
            if (!BelongsToCurrentProcess(window)
                || !PrintWindow(window, memoryDc, PrintClientOnly | PrintRenderFullContent)) return null;
            if (!BelongsToCurrentProcess(window)) return null;
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(pixelsAddress, pixels, 0, pixels.Length);
            var variation = false;
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                if (offset > 0 && (pixels[offset] != pixels[0]
                    || pixels[offset + 1] != pixels[1] || pixels[offset + 2] != pixels[2])) variation = true;
                // GDI renderers commonly leave the DIB alpha channel unset.
                pixels[offset + 3] = 255;
            }
            return new(width, height, pixels, variation);
        }
        finally
        {
            if (previousBitmap != nint.Zero && previousBitmap != InvalidGdiObject && memoryDc != nint.Zero)
                _ = SelectObject(memoryDc, previousBitmap);
            if (bitmap != nint.Zero) _ = DeleteObject(bitmap);
            if (memoryDc != nint.Zero) _ = DeleteDC(memoryDc);
            if (sourceDc != nint.Zero) _ = ReleaseDC(window, sourceDc);
        }
    }

    private sealed record ClientPixels(int Width, int Height, byte[] Pixels, bool HasVariation);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint FirstColor; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint deviceContext, ref BitmapInfo info, uint usage,
        out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);
}
#endif
