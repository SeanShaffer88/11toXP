using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LunaDesktop.Appearance;

internal static class AppearanceAssets
{
    private const int DesignSize = 32;
    private static readonly int[] IconSizes = [16, 32, 48, 256];
    private static readonly int[] CursorSizes = [32, 48, 64];

    public static bool Ensure(string directory, string name)
    {
        CheckAssetDirectory(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temporary = path + ".new";
        CheckRegularPath(path);
        CheckRegularPath(temporary);

        var bytes = Create(name);
        if (Matches(path, bytes))
            return false;

        File.Delete(temporary);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return true;
    }

    public static void Remove(string directory, IEnumerable<string> names)
    {
        CheckAssetDirectory(directory);
        if (!Directory.Exists(directory))
            return;

        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            CheckRegularPath(path);
            var temporary = path + ".new";
            CheckRegularPath(temporary);
            if (!File.Exists(path) && !File.Exists(temporary))
                continue;

            var bytes = Create(name);
            if (Matches(path, bytes))
                File.Delete(path);
            if (Matches(temporary, bytes))
                File.Delete(temporary);
        }
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static bool Matches(string path, byte[] bytes) =>
        File.Exists(path) && new FileInfo(path).Length == bytes.Length &&
        File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    private static void CheckAssetDirectory(string directory)
    {
        var parent = Directory.GetParent(directory);
        if (parent is not null)
            CheckRegularPath(parent.FullName);
        CheckRegularPath(directory);
    }

    private static void CheckRegularPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An 11toXP appearance asset path points to another location.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static byte[] Create(string name) => name switch
    {
        "Arrow.cur" => Cursor(DrawArrow, 3, 2),
        "Hand.cur" => Cursor(DrawHand, 12, 2),
        "Wait.cur" => Cursor(drawing => DrawHourglass(drawing, 0, 0, 1), 16, 16),
        "AppStarting.cur" => Cursor(DrawStarting, 3, 2),
        "Computer.ico" => Icon(DrawComputer),
        "Documents.ico" => Icon(DrawDocuments),
        "Network.ico" => Icon(DrawNetwork),
        "BinEmpty.ico" => Icon(drawing => DrawBin(drawing, full: false)),
        "BinFull.ico" => Icon(drawing => DrawBin(drawing, full: true)),
        "Information.wav" => Wave(new(523.25, 0, 0.29), new(698.46, 0.07, 0.35), new(880, 0.12, 0.41)),
        "Warning.wav" => Wave(new(659.25, 0, 0.31), new(493.88, 0.13, 0.36)),
        "Error.wav" => Wave(new(329.63, 0, 0.19), new(246.94, 0.09, 0.24)),
        "Question.wav" => Wave(new(440, 0, 0.23), new(659.25, 0.12, 0.29)),
        "Notification.wav" => Wave(new(587.33, 0, 0.21), new(783.99, 0.08, 0.32)),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static byte[] Icon(Action<DrawingContext> draw)
    {
        var images = new List<Raster>();
        foreach (var size in IconSizes)
        {
            using var image = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(size, draw)));
            encoder.Save(image);
            images.Add(new Raster(size, 1, 32, image.ToArray()));
        }
        return Assemble(images, cursor: false);
    }

    private static byte[] Cursor(Action<DrawingContext> draw, int x, int y)
    {
        var images = new List<Raster>();
        foreach (var size in CursorSizes)
        {
            var bitmap = new FormatConvertedBitmap(Render(size, draw), PixelFormats.Bgra32, null, 0);
            var stride = size * 4;
            var pixels = new byte[size * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            using var image = new MemoryStream();
            using var writer = new BinaryWriter(image, Encoding.ASCII, leaveOpen: true);
            writer.Write(40); // BITMAPINFOHEADER
            writer.Write(size);
            writer.Write(size * 2); // Combined XOR image and AND mask.
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            for (var field = 0; field < 6; field++)
                writer.Write(0);
            for (var row = size - 1; row >= 0; row--)
                writer.Write(pixels, row * stride, stride);

            var maskStride = ((size + 31) / 32) * 4;
            var mask = new byte[maskStride];
            for (var row = size - 1; row >= 0; row--)
            {
                Array.Clear(mask);
                for (var column = 0; column < size; column++)
                {
                    if (pixels[row * stride + column * 4 + 3] == 0)
                        mask[column / 8] |= (byte)(0x80 >> (column % 8));
                }
                writer.Write(mask);
            }
            images.Add(new Raster(size, (ushort)(x * size / DesignSize), (ushort)(y * size / DesignSize),
                image.ToArray()));
        }
        return Assemble(images, cursor: true);
    }

    private static byte[] Assemble(IReadOnlyList<Raster> images, bool cursor)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)(cursor ? 2 : 1));
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var image in images)
        {
            var dimension = (byte)(image.Size == 256 ? 0 : image.Size);
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((ushort)0);
            writer.Write(image.First);
            writer.Write(image.Second);
            writer.Write(image.Bytes.Length);
            writer.Write(offset);
            offset += image.Bytes.Length;
        }
        foreach (var image in images)
            writer.Write(image.Bytes);
        return output.ToArray();
    }

    private static BitmapSource Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform((double)size / DesignSize, (double)size / DesignSize));
            draw(drawing);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Wave(params Note[] notes)
    {
        const int rate = 22050;
        const int bytesPerSample = 2;
        var sampleCount = (int)Math.Ceiling(rate * notes.Max(note => note.Start + note.Duration));
        var sampleBytes = sampleCount * bytesPerSample;
        using var output = new MemoryStream(44 + sampleBytes);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1); // PCM, mono.
        writer.Write((ushort)1);
        writer.Write(rate);
        writer.Write(rate * bytesPerSample);
        writer.Write((ushort)bytesPerSample);
        writer.Write((ushort)(bytesPerSample * 8));
        writer.Write("data"u8);
        writer.Write(sampleBytes);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var time = (double)sample / rate;
            var value = 0d;
            foreach (var note in notes)
            {
                var age = time - note.Start;
                if (age < 0 || age >= note.Duration)
                    continue;

                var phase = age * Math.Tau * note.Hertz;
                var attack = Math.Min(1, age / 0.012);
                var release = Math.Min(1, (note.Duration - age) / 0.035);
                var envelope = attack * release * Math.Exp(-7 * age);
                var timbre = Math.Sin(phase) + 0.24 * Math.Sin(2 * phase) + 0.08 * Math.Sin(3 * phase);
                value += 0.20 * envelope * timbre;
            }
            writer.Write((short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue));
        }
        return output.ToArray();
    }

    private static void DrawArrow(DrawingContext drawing) =>
        Shape(drawing, "M3,2 L3,23 9,17 13,26 17,24 13,15 21,15 Z", Brushes.White, Brushes.Black, 1.15);

    private static void DrawHand(DrawingContext drawing) =>
        Shape(drawing, "M12,2 C10,2 10,4 10,5 L10,14 8,12 C6,10 4,12 5,14 L11,23 12,27 24,27 25,23 27,19 27,13 C27,11 24,10 23,12 C23,9 20,9 19,11 C18,8 15,9 15,11 L15,5 C15,3 14,2 12,2 Z",
            Brushes.White, Brushes.Black, 1.1);

    private static void DrawStarting(DrawingContext drawing)
    {
        drawing.PushTransform(new ScaleTransform(0.78, 0.78, 3, 2));
        DrawArrow(drawing);
        drawing.Pop();
        DrawHourglass(drawing, 15, 13, 0.54);
    }

    private static void DrawHourglass(DrawingContext drawing, double x, double y, double scale)
    {
        drawing.PushTransform(new TranslateTransform(x, y));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        Shape(drawing, "M9,5 H23 C23,12 19,13 16,16 C19,19 23,20 23,27 H9 C9,20 13,19 16,16 C13,13 9,12 9,5 Z",
            Shade("#ffffff", "#b5d2ef"), Brushes.Black, 1.2);
        Shape(drawing, "M11,10 H21 C20,12 18,13 16,14.5 C14,13 12,12 11,10 Z M10.5,25 C12,21 14,22 16,20 C18,22 20,21 21.5,25 Z",
            Shade("#ffda6b", "#ad6620"));
        Rect(drawing, 7, 3, 18, 3, Shade("#8b6442", "#3c2a1f"), Brushes.Black);
        Rect(drawing, 7, 26, 18, 3, Shade("#8b6442", "#3c2a1f"), Brushes.Black);
        drawing.Pop();
        drawing.Pop();
    }

    private static void DrawComputer(DrawingContext drawing)
    {
        Shadow(drawing);
        Shape(drawing, "M5,4 L24,4 28,7 28,22 24,25 5,25 Z", Shade("#ebe9d5", "#8994a1"), Paint("#394b63"));
        Rect(drawing, 4, 5, 21, 18, Shade("#f7f5e8", "#adb8b8"), Paint("#526479"), 2);
        Rect(drawing, 7, 8, 15, 11, Shade("#8af1ff", "#2c80c8"), Paint("#40556d"), 2);
        Shape(drawing, "M9,17 L13,13 17,15 21,11 V17 Z", Shade("#b7dd72", "#50912d"));
        Shape(drawing, "M12,23 H18 L19,27 H24 V29 H7 V27 H12 Z", Shade("#e5e1c8", "#7d8a9a"), Paint("#546273"));
        drawing.DrawEllipse(Paint("#4a9738"), null, new Point(21, 21), 0.8, 0.8);
    }

    private static void DrawDocuments(DrawingContext drawing)
    {
        Shadow(drawing);
        Shape(drawing, "M3,7 H13 L16,10 H29 V26 H3 Z", Shade("#ffe98d", "#b76d1e"), Paint("#885618"));
        Shape(drawing, "M9,5 H21 L25,9 V23 H9 Z", Shade("#ffffff", "#c4e5ff"), Paint("#547fa7"));
        Shape(drawing, "M21,5 V9 H25", null, Paint("#6588a6"));
        Shape(drawing, "M3,16 L9,13 H30 L27,28 H4 Z", Shade("#fff2a6", "#d09129"), Paint("#9a5e17"));
        Shape(drawing, "M7,17 L11,15 H27", null, Paint("#fff2c1"), 1.1);
    }

    private static void DrawNetwork(DrawingContext drawing)
    {
        Shadow(drawing);
        Shape(drawing, "M8,17 V25 H24 V16 M16,25 V28", null, Paint("#b5852f"), 1.8);
        Rect(drawing, 2, 10, 13, 11, Shade("#fffaf0", "#9faaa9"), Paint("#465d74"), 1.6);
        Rect(drawing, 4, 12, 9, 6, Shade("#c2f6ff", "#317fbe"), Paint("#405871"), 1);
        Rect(drawing, 16, 4, 14, 12, Shade("#fffaf0", "#9faaa9"), Paint("#465d74"), 1.6);
        Rect(drawing, 18, 6, 10, 7, Shade("#c2f6ff", "#317fbe"), Paint("#405871"), 1);
        Rect(drawing, 12, 27, 8, 3, Shade("#fff29c", "#ba7924"), Paint("#905821"), 1);
    }

    private static void DrawBin(DrawingContext drawing, bool full)
    {
        Shadow(drawing);
        Shape(drawing, "M5,10 H27 L23.5,28 C19,31 13,31 8.5,28 Z", Shade("#edf7f9", "#79a0a9"),
            Paint("#526d7b"), 1.1);
        drawing.DrawEllipse(Shade("#91b4b9", "#e3f2f5"), new Pen(Paint("#526d7b"), 1), new Point(16, 10), 11, 3.7);
        if (full)
        {
            Shape(drawing, "M9,10 L10,5 14,6 17,4 21,7 24,6 23,11 18,13 12,12 Z",
                Shade("#fffef2", "#c7dce1"), Paint("#718f9d"));
        }
        Shape(drawing, "M11,14 L13,27 M21,14 L19,27", null, Paint("#69949d"), 1);
        Shape(drawing, "M16,16 L20,19 18,20 21,21 21,17 20,18 18,15 Z M21,22 L17,25 16,23 15,26 19,28 18,26 22,24 Z M14,25 L11,21 13,20 10,19 9,23 11,22 12,26 Z",
            Paint("#237448"));
    }

    private static void Shadow(DrawingContext drawing) =>
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(45, 24, 44, 57)), null,
            new Point(16, 28.5), 12, 2.1);

    private static void Shape(DrawingContext drawing, string geometry, Brush? fill, Brush? outline = null,
        double thickness = 0.8) =>
        drawing.DrawGeometry(fill, outline is null ? null : new Pen(outline, thickness), Geometry.Parse(geometry));

    private static void Rect(DrawingContext drawing, double x, double y, double width, double height,
        Brush fill, Brush outline, double radius = 0) =>
        drawing.DrawRoundedRectangle(fill, new Pen(outline, 0.85), new Rect(x, y, width, height), radius, radius);

    private static SolidColorBrush Paint(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static LinearGradientBrush Shade(string top, string bottom) =>
        new(Paint(top).Color, Paint(bottom).Color, new Point(0, 0), new Point(0, 1));

    private sealed record Raster(int Size, ushort First, ushort Second, byte[] Bytes);
    private sealed record Note(double Hertz, double Start, double Duration);
}
