using System.Buffers.Binary;
using System.IO.Compression;

namespace McpDebugAdapter.Ui;

/// <summary>
/// Minimal PNG encoder (8-bit RGBA, no filtering) so screenshots need no imaging library.
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Encodes top-down 32-bit BGRA pixels as PNG. Alpha is forced to opaque because
    /// screen captures (BitBlt) leave it undefined.
    /// </summary>
    public static byte[] EncodeBgra(byte[] pixels, int width, int height, int stride)
    {
        var raw = new byte[height * (1 + width * 4)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: none
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                raw[o++] = pixels[i + 2];
                raw[o++] = pixels[i + 1];
                raw[o++] = pixels[i];
                raw[o++] = 255;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // color type: RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
