using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace GroceryErp.Printing;

/// <summary>Creates a real Code 128 image for the on-screen label preview.</summary>
public static class BarcodePreview
{
    public static BitmapSource Create(string barcode, int width = 360, int height = 72)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(barcode);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Width = width, Height = height, Margin = 2, PureBarcode = true },
        };
        var pixels = writer.Write(barcode);
        BitmapSource bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
