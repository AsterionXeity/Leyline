using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace Leyline.Services;

public static class IconGenerator
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateAppIcon()
    {
        using var bmp = RenderLogoBitmap(64);
        IntPtr hIcon = bmp.GetHicon();
        using var tempIcon = Icon.FromHandle(hIcon);
        var cloned = (Icon)tempIcon.Clone();
        DestroyIcon(hIcon);
        return cloned;
    }

    public static string EnsureIconFileOnDisk(string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var bmp = RenderLogoBitmap(256);
            using var pngMs = new MemoryStream();
            bmp.Save(pngMs, System.Drawing.Imaging.ImageFormat.Png);
            byte[] pngBytes = pngMs.ToArray();

            using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // Standard ICO header with embedded 256x256 PNG
            bw.Write((ushort)0); // Reserved
            bw.Write((ushort)1); // Type 1 = ICO
            bw.Write((ushort)1); // 1 image

            bw.Write((byte)0);   // Width 0 = 256
            bw.Write((byte)0);   // Height 0 = 256
            bw.Write((byte)0);   // Color count
            bw.Write((byte)0);   // Reserved
            bw.Write((ushort)1); // Color planes
            bw.Write((ushort)32);// Bits per pixel
            bw.Write((uint)pngBytes.Length);
            bw.Write((uint)22);  // Offset to PNG data
            bw.Write(pngBytes);
        }
        catch
        {
        }
        return targetPath;
    }

    private static Bitmap RenderLogoBitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Rounded dark obsidian badge background
        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, size, size),
            Color.FromArgb(255, 22, 18, 40),
            Color.FromArgb(255, 10, 10, 18),
            45f);

        int pad = Math.Max(2, size / 16);
        int radius = size / 4;
        using var path = CreateRoundedRect(new Rectangle(pad, pad, size - pad * 2, size - pad * 2), radius);
        g.FillPath(bgBrush, path);

        using var borderPen = new Pen(Color.FromArgb(220, 124, 77, 255), Math.Max(2f, size / 24f));
        g.DrawPath(borderPen, path);

        // Draw stylized "V" + Audio Wave bars in cyan & electric violet
        using var wavePen = new Pen(Color.FromArgb(255, 0, 229, 255), Math.Max(2.5f, size / 12f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        PointF[] pts =
        [
            new(size * 0.22f, size * 0.42f),
            new(size * 0.34f, size * 0.26f),
            new(size * 0.50f, size * 0.74f),
            new(size * 0.66f, size * 0.26f),
            new(size * 0.78f, size * 0.52f)
        ];
        g.DrawLines(wavePen, pts);

        return bmp;
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
