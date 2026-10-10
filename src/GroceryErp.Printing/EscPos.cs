using System.Text;

namespace GroceryErp.Printing;

public sealed class EscPos
{
    private readonly List<byte> _buffer = [];

    public EscPos Init() => Add([0x1B, 0x40]);
    public EscPos Align(byte alignment) => Add([0x1B, 0x61, alignment]);
    public EscPos Bold(bool enabled) => Add([0x1B, 0x45, enabled ? (byte)1 : (byte)0]);
    public EscPos Text(string value) => Add(Encoding.ASCII.GetBytes(value + "\n"));
    public EscPos Feed(byte lines) => Add([0x1B, 0x64, lines]);
    public EscPos Cut() => Add([0x1D, 0x56, 0x42, 0x00]);
    public EscPos OpenDrawer() => Add([0x1B, 0x70, 0x00, 0x19, 0xFA]);

    public EscPos Raster(ReadOnlySpan<byte> bits, int widthBytes, int height, int bandRows = 128)
    {
        if (widthBytes <= 0 || height <= 0 || bits.Length != checked(widthBytes * height))
            throw new ArgumentException("Invalid monochrome image dimensions.", nameof(bits));
        if (bandRows is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(bandRows));
        for (int y = 0; y < height; y += bandRows)
        {
            int rows = Math.Min(bandRows, height - y);
            Add([0x1D, 0x76, 0x30, 0x00, (byte)widthBytes, (byte)(widthBytes >> 8),
                (byte)rows, (byte)(rows >> 8)]);
            Add(bits.Slice(y * widthBytes, rows * widthBytes));
        }
        return this;
    }

    public byte[] ToArray() => _buffer.ToArray();

    private EscPos Add(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) _buffer.Add(value);
        return this;
    }
}
