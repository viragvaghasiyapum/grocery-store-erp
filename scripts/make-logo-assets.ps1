# Makes the app's logo files from the owner's logo, logo/logo.png (navy ink on white, any size):
#   logo/logo-app.png  the picture in the shell, the sign-in window and the title bars, at -AppSize
#                      pixels, each pixel one of -Shades blends of white and the ink, as an 8-bit
#                      indexed PNG (the source's 1.35 MB of colour noise becomes about 60 KB);
#   logo/logo.ico      the exe, taskbar and installer icon: cut to the logo's rounded border,
#                      transparent outside it, 16 to 256 pixels (BMP entries, PNG at 256).
# Run it after replacing logo/logo.png, then rebuild. Windows only (System.Drawing).
param(
    [string] $Source,
    [int] $AppSize = 840,   # the largest place shows it at 420 DIP: 840 pixels at 200% scaling
    [int] $Shades = 32
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repository 'logo\logo.png' }
$appPng = Join-Path $repository 'logo\logo-app.png'
$icon = Join-Path $repository 'logo\logo.ico'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class LogoAssets
{
    static byte[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++)
            Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), px, y * bmp.Width * 4, bmp.Width * 4);
        bmp.UnlockBits(data);
        return px;
    }

    static Bitmap FromPixels(byte[] px, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            Marshal.Copy(px, y * w * 4, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), w * 4);
        bmp.UnlockBits(data);
        return bmp;
    }

    // The ink: the mean of the darkest pixels (BGRA order).
    static double[] Ink(byte[] px)
    {
        double b = 0, g = 0, r = 0; long n = 0;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] + px[i + 1] + px[i + 2] < 200) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
        if (n == 0) throw new InvalidOperationException("The logo has no dark ink.");
        return new[] { b / n, g / n, r / n };
    }

    // Each pixel's place between white (0) and the ink (levels - 1).
    static byte[] Shades(byte[] px, double[] ink, int levels)
    {
        double db = ink[0] - 255, dg = ink[1] - 255, dr = ink[2] - 255, len2 = db * db + dg * dg + dr * dr;
        var shade = new byte[px.Length / 4];
        for (int p = 0; p < shade.Length; p++)
        {
            int i = p * 4;
            double t = ((px[i] - 255) * db + (px[i + 1] - 255) * dg + (px[i + 2] - 255) * dr) / len2;
            t = Math.Max(0, Math.Min(1, t));
            shade[p] = (byte)Math.Round(t * (levels - 1), MidpointRounding.AwayFromZero);
        }
        return shade;
    }

    static Color Blend(double[] ink, int k, int levels)
    {
        double t = (double)k / (levels - 1);
        return Color.FromArgb(255,
            (int)Math.Round(255 + t * (ink[2] - 255), MidpointRounding.AwayFromZero),
            (int)Math.Round(255 + t * (ink[1] - 255), MidpointRounding.AwayFromZero),
            (int)Math.Round(255 + t * (ink[0] - 255), MidpointRounding.AwayFromZero));
    }

    static Bitmap Draw(Bitmap src, int w, int h)
    {
        var next = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(next))
        using (var ia = new ImageAttributes())
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        return next;
    }

    // Halves in steps first, so a large reduction keeps thin lines.
    static Bitmap Resize(Bitmap src, int size)
    {
        Bitmap cur = src;
        while (cur.Width >= 2 * size) cur = Draw(cur, Math.Max(size, cur.Width / 2), Math.Max(size, cur.Height / 2));
        if (cur.Width != size || cur.Height != size) cur = Draw(cur, size, size);
        return cur;
    }

    public static string MakeAppImage(string source, string dest, int size, int levels)
    {
        using (var orig = new Bitmap(source))
        {
            if (orig.Width != orig.Height) throw new InvalidOperationException("The logo must be square.");
            double[] ink = Ink(Pixels(orig));
            var resized = Resize(orig, size);
            byte[] shade = Shades(Pixels(resized), ink, levels);
            using (var output = new Bitmap(size, size, PixelFormat.Format8bppIndexed))
            {
                ColorPalette palette = output.Palette;
                for (int k = 0; k < palette.Entries.Length; k++) palette.Entries[k] = Blend(ink, Math.Min(k, levels - 1), levels);
                output.Palette = palette;
                var data = output.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                for (int y = 0; y < size; y++)
                    Marshal.Copy(shade, y * size, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), size);
                output.UnlockBits(data);
                output.Save(dest, ImageFormat.Png);
            }
            return string.Format("ink #{0:X2}{1:X2}{2:X2}", (int)Math.Round(ink[2], MidpointRounding.AwayFromZero),
                (int)Math.Round(ink[1], MidpointRounding.AwayFromZero), (int)Math.Round(ink[0], MidpointRounding.AwayFromZero));
        }
    }

    static bool IsInk(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return px[i] + px[i + 1] + px[i + 2] < 450;
    }

    public static string MakeIcon(string source, string dest, int[] sizes)
    {
        using (var orig = new Bitmap(source))
        {
            int w = orig.Width, h = orig.Height;
            byte[] src = Pixels(orig);
            double[] ink = Ink(src);
            // Remove the source's colour noise: every pixel onto the white-to-ink line.
            byte[] shade = Shades(src, ink, 256);
            var clean = new byte[src.Length];
            for (int p = 0; p < shade.Length; p++)
            {
                Color c = Blend(ink, shade[p], 256);
                clean[p * 4] = c.B; clean[p * 4 + 1] = c.G; clean[p * 4 + 2] = c.R; clean[p * 4 + 3] = 255;
            }
            // The rounded border: the outermost ink on the middle row and column, and its corner
            // radius from where the diagonal from the top-left corner meets it.
            int midX = w / 2, midY = h / 2, l = 0, r = w - 1, t = 0, b = h - 1, k = 0;
            while (!IsInk(clean, w, l, midY)) l++;
            while (!IsInk(clean, w, r, midY)) r--;
            while (!IsInk(clean, w, midX, t)) t++;
            while (!IsInk(clean, w, midX, b)) b--;
            while (!IsInk(clean, w, l + k, t + k)) k++;
            int radius = (int)Math.Round(k / (1 - 1 / Math.Sqrt(2)), MidpointRounding.AwayFromZero);
            int bw = r - l + 1, bh = b - t + 1, side = Math.Max(bw, bh), ox = (side - bw) / 2, oy = (side - bh) / 2;

            var master = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var cleanBmp = FromPixels(clean, w, h))
            using (var g = Graphics.FromImage(master))
            using (var brush = new TextureBrush(cleanBmp))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                brush.TranslateTransform(ox - l, oy - t);
                int d = 2 * radius;
                path.AddArc(ox, oy, d, d, 180, 90);
                path.AddArc(ox + bw - d, oy, d, d, 270, 90);
                path.AddArc(ox + bw - d, oy + bh - d, d, d, 0, 90);
                path.AddArc(ox, oy + bh - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }

            var entries = new List<byte[]>();
            foreach (int size in sizes)
            {
                Bitmap bmp = Resize(master, size);
                if (size >= 256)
                {
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); entries.Add(ms.ToArray()); }
                    continue;
                }
                byte[] px = Pixels(bmp);
                int maskRow = (size + 31) / 32 * 4;
                using (var ms = new MemoryStream())
                using (var bwr = new BinaryWriter(ms))
                {
                    bwr.Write(40); bwr.Write(size); bwr.Write(size * 2); bwr.Write((short)1); bwr.Write((short)32);
                    bwr.Write(0); bwr.Write(size * size * 4 + maskRow * size); bwr.Write(0); bwr.Write(0); bwr.Write(0); bwr.Write(0);
                    for (int y = size - 1; y >= 0; y--) bwr.Write(px, y * size * 4, size * 4);
                    for (int y = size - 1; y >= 0; y--)
                    {
                        var row = new byte[maskRow];
                        for (int x = 0; x < size; x++)
                            if (px[(y * size + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                        bwr.Write(row);
                    }
                    bwr.Flush();
                    entries.Add(ms.ToArray());
                }
            }

            using (var fs = new FileStream(dest, FileMode.Create))
            using (var bwr = new BinaryWriter(fs))
            {
                bwr.Write((short)0); bwr.Write((short)1); bwr.Write((short)entries.Count);
                int offset = 6 + 16 * entries.Count;
                for (int e = 0; e < entries.Count; e++)
                {
                    byte dim = sizes[e] >= 256 ? (byte)0 : (byte)sizes[e];
                    bwr.Write(dim); bwr.Write(dim); bwr.Write((byte)0); bwr.Write((byte)0);
                    bwr.Write((short)1); bwr.Write((short)32); bwr.Write(entries[e].Length); bwr.Write(offset);
                    offset += entries[e].Length;
                }
                foreach (byte[] entry in entries) bwr.Write(entry);
            }
            return string.Format("border {0}x{1} at ({2},{3}), corner radius {4}", bw, bh, l, t, radius);
        }
    }
}
'@

$app = [LogoAssets]::MakeAppImage($Source, $appPng, $AppSize, $Shades)
"logo-app.png: $AppSize px, $Shades shades, $app, $([math]::Round((Get-Item $appPng).Length / 1KB)) KB"
$ico = [LogoAssets]::MakeIcon($Source, $icon, [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256))
"logo.ico: 16 to 256 px, $ico, $([math]::Round((Get-Item $icon).Length / 1KB)) KB"
