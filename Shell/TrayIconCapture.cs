using System.Buffers;
using System.Runtime.InteropServices;
using Rectangle = LunaDesktop.Shell.ShellNative.Rectangle;

namespace LunaDesktop.Shell;

// Capture only Explorer's notification-area child, with no desktop or file output.
internal static class TrayIconCapture
{
    private const int MaxWidth = 2048;
    private const int MaxHeight = 256;
    private const int MaxPixels = 131072;
    private const int UntouchedPixel = 0x00FF01FE;
    private const uint DcCache = 0x00000002;
    private const uint DcClipSiblings = 0x00000010;
    private const uint SourceCopy = 0x00CC0020;
    private static readonly SemaphoreSlim CaptureSlot = new(1, 1);

    internal readonly record struct Windows(nint Taskbar, nint Tray, nint Clock);
    internal readonly record struct Source(Windows Windows, uint Process, Rectangle Client, int IconWidth);
    internal sealed record Frame(int Width, int Height, int[] Pixels);

    internal static Windows FindWindows()
    {
        nint taskbar = FindWindowW("Shell_TrayWnd", null);
        nint tray = taskbar == 0 ? 0 : FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        nint clock = tray == 0 ? 0 : FindWindowExW(tray, 0, "TrayClockWClass", null);
        return new Windows(taskbar, tray, clock);
    }

    internal static Source? Describe(Windows windows)
    {
        var origin = new NativePoint();
        if (windows.Tray == 0 || windows.Clock == 0 ||
            !ShellNative.IsWindowVisible(windows.Tray) || !ShellNative.IsWindowVisible(windows.Clock) ||
            !TryReadShellProcess(windows, out uint process) ||
            !GetClientRect(windows.Tray, out Rectangle client) ||
            !ClientToScreen(windows.Tray, ref origin) ||
            !ShellNative.GetWindowRect(windows.Clock, out Rectangle clock))
            return null;

        int width = client.Right;
        int height = client.Bottom;
        int iconWidth = clock.Left - origin.X;
        if (!ValidSize(width, height) || iconWidth < 12 || iconWidth >= width)
            return null;
        return new Source(windows, process,
            new Rectangle(origin.X, origin.Y, origin.X + width, origin.Y + height), iconWidth);
    }

    internal static async Task<Frame?> CaptureAsync(Source source, CancellationToken cancellationToken)
    {
        await CaptureSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep native capture off the UI thread and serialize it across taskbar rebuilds.
            return await Task.Run(() => Capture(source, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { CaptureSlot.Release(); }
    }

    private static Frame? Capture(Source source, CancellationToken cancellationToken)
    {
        nint previousDpi = SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        if (previousDpi == 0)
            return null;
        try
        {
            if (Describe(FindWindows()) != source)
                return null;
            cancellationToken.ThrowIfCancellationRequested();

            // Clip the destination to the icon region. No clock pixels enter the DIB.
            int width = source.IconWidth;
            int height = source.Client.Height;
            nint tray = source.Windows.Tray;
            nint sourceDc = GetDCEx(tray, 0, DcCache | DcClipSiblings);
            if (sourceDc == 0)
                return null;
            try
            {
                nint dc = CreateCompatibleDC(sourceDc);
                if (dc == 0)
                    return null;
                try { return ReadClient(dc, sourceDc, source, width, height, cancellationToken); }
                finally { DeleteDC(dc); }
            }
            finally { ReleaseDC(tray, sourceDc); }
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    private static Frame? ReadClient(nint dc, nint sourceDc, Source source, int width, int height,
        CancellationToken cancellationToken)
    {
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // Top-down 32-bit DIB.
            Planes = 1,
            BitCount = 32
        };
        nint bitmap = CreateDIBSection(dc, ref info, 0, out nint pixels, 0, 0);
        if (bitmap == 0)
            return null;
        try
        {
            if (pixels == 0)
                return null;
            nint previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1)
                return null;
            try
            {
                var untouched = new int[width];
                Array.Fill(untouched, UntouchedPixel);
                for (int row = 0; row < height; row++)
                    Marshal.Copy(untouched, 0, pixels + row * width * sizeof(int), width);
                // SRCCOPY reads the tray window DC without CAPTUREBLT or a print message to Explorer.
                if (!BitBlt(dc, 0, 0, width, height, sourceDc, 0, 0, SourceCopy) || !GdiFlush() ||
                    Describe(FindWindows()) != source)
                    return null;
                cancellationToken.ThrowIfCancellationRequested();

                int size = width * height;
                int[] icons = ArrayPool<int>.Shared.Rent(size);
                try
                {
                    Marshal.Copy(pixels, icons, 0, size);
                    return Extract(icons.AsSpan(0, size), width, height);
                }
                finally { ArrayPool<int>.Shared.Return(icons, clearArray: true); }
            }
            finally { SelectObject(dc, previous); }
        }
        finally { DeleteObject(bitmap); }
    }

    internal static Frame? Extract(ReadOnlySpan<int> pixels, int width, int height)
    {
        if (!ValidSize(width, height) || pixels.Length != width * height)
            return null;

        // Exclude the native edge/divider and the horizontal edges outside the icon row.
        int xInset = Math.Clamp(height / 10, 2, 12);
        int yInset = Math.Clamp(height / 8, 2, 24);
        int iconWidth = width - 2 * xInset;
        int iconHeight = height - 2 * yInset;
        if (iconWidth < 8 || iconHeight < 6)
            return null;

        var result = new int[iconWidth * iconHeight];
        var histogram = new Dictionary<int, int>();
        int priorBackground = -1;
        int painted = 0;
        for (int row = 0; row < iconHeight; row++)
        {
            ReadOnlySpan<int> source = pixels.Slice((row + yInset) * width + xInset, iconWidth);
            histogram.Clear();
            int background = 0;
            int support = 0;
            foreach (int value in source)
            {
                int color = value & 0x00FFFFFF;
                if (color == UntouchedPixel)
                    return null;
                int count = histogram.GetValueOrDefault(color) + 1;
                histogram[color] = count;
                if (count > support)
                {
                    background = color;
                    support = count;
                }
            }
            // The window DC normally yields a flat background. An unknown texture or
            // partially painted frame must not reintroduce an opaque Windows tray strip.
            if (support * 3 < iconWidth || (priorBackground >= 0 && Difference(background, priorBackground) > 24))
                return null;
            priorBackground = background;

            for (int col = 0; col < iconWidth; col++)
            {
                int color = source[col] & 0x00FFFFFF;
                int difference = Difference(color, background);
                int alpha = Math.Clamp((difference - 6) * 255 / 24, 0, 255);
                if (alpha == 0)
                    continue;
                int blue = Unmix(color, background, 0, alpha);
                int green = Unmix(color, background, 8, alpha);
                int red = Unmix(color, background, 16, alpha);
                result[row * iconWidth + col] = alpha << 24 | red << 16 | green << 8 | blue;
                painted++;
            }
        }
        return painted >= 4 ? new Frame(iconWidth, iconHeight, result) : null;
    }

    private static int Difference(int first, int second) =>
        Math.Max(Math.Abs((first & 255) - (second & 255)),
            Math.Max(Math.Abs((first >> 8 & 255) - (second >> 8 & 255)),
                Math.Abs((first >> 16) - (second >> 16))));

    private static int Unmix(int color, int background, int shift, int alpha) =>
        Math.Clamp((color >> shift & 255) - ((background >> shift & 255) * (255 - alpha) + 127) / 255, 0, alpha);

    private static bool ValidSize(int width, int height) =>
        width is >= 12 and <= MaxWidth && height is >= 12 and <= MaxHeight && width * height <= MaxPixels;

    private static bool TryReadShellProcess(Windows windows, out uint process) =>
        ShellNative.GetWindowThreadProcessId(GetShellWindow(), out process) != 0 && process != 0 &&
        ShellNative.GetWindowThreadProcessId(windows.Taskbar, out uint taskbar) != 0 && taskbar == process &&
        ShellNative.GetWindowThreadProcessId(windows.Tray, out uint tray) != 0 && tray == process &&
        ShellNative.GetWindowThreadProcessId(windows.Clock, out uint clock) != 0 && clock == process;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, ImageSize;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rectangle area);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint GetDCEx(nint window, nint region, uint flags);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint rasterOperation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint bitmap);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint bitmap);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage,
        out nint pixels, nint section, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
