using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GroceryErp.Core;

namespace GroceryErp.Printing;

internal static class TsplRasterText
{
    private const int Width = 440;
    private const int Height = 30;

    public static byte[] Render(string value, int x, int y)
    {
        byte[]? result = null;
        Exception? failure = null;
        void Work()
        {
            try { result = RenderOnSta(value, x, y); }
            catch (Exception exception) { failure = exception; }
        }
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) Work();
        else
        {
            var thread = new Thread(Work);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        if (failure is not null) throw new InvalidOperationException("Could not rasterize label text.", failure);
        return result ?? throw new InvalidOperationException("Label text rasterization returned no data.");
    }

    private static byte[] RenderOnSta(string value, int x, int y)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            var text = new FormattedText(InputNormalizer.Normalize(value),
                CultureInfo.GetCultureInfo("gu-IN"), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Nirmala UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                19, Brushes.Black, 1);
            text.MaxTextWidth = Width;
            text.MaxTextHeight = Height;
            context.DrawText(text, new Point(0, 0));
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        byte[] monochrome = new byte[(Width / 8) * Height];
        for (int row = 0; row < Height; row++)
            for (int column = 0; column < Width; column++)
                if (pixels[(row * Width + column) * 4 + 2] < 128)
                    monochrome[row * (Width / 8) + column / 8] |= (byte)(0x80 >> (column % 8));

        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"BITMAP {x},{y},{Width / 8},{Height},0,"));
        stream.Write(monochrome);
        stream.Write("\r\n"u8);
        return stream.ToArray();
    }
}
