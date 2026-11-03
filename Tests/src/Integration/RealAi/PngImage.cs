namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public static class PngImage
{
    public static byte[] Solid(int width, int height, byte red, byte green, byte blue)
    {
        using MemoryStream raw = new();

        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);

            for (int x = 0; x < width; x++)
            {
                raw.WriteByte(red);
                raw.WriteByte(green);
                raw.WriteByte(blue);
            }
        }

        using MemoryStream compressed = new();

        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, true))
        {
            zlib.Write(raw.ToArray());
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [.. Big(width), .. Big(height), 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] Big(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(Big(data.Length));
        stream.Write(body);
        stream.Write(Big((int)Crc(body)));
    }

    private static uint Crc(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in bytes)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
