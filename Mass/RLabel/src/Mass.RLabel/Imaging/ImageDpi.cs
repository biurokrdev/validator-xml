using System.Buffers.Binary;

namespace Mass.RLabel.Imaging;

/// <summary>
/// Wpisuje rozdzielczość do pliku, żeby drukarka i przeglądarka znały fizyczny wymiar nalepki.
/// Skia nie zapisuje tych metadanych, więc dopisujemy je same: PNG dostaje fragment pHYs, JPEG gęstość w nagłówku JFIF.
/// </summary>
internal static class ImageDpi
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Apply(byte[] image, LabelImageFormat format, int dpi) => format switch
    {
        LabelImageFormat.Png => WithPngPhys(image, dpi),
        LabelImageFormat.Jpeg => WithJfifDensity(image, dpi),
        _ => image,
    };

    /// <summary>Wstawia fragment pHYs (piksele na metr) zaraz za IHDR.</summary>
    internal static byte[] WithPngPhys(byte[] png, int dpi)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            throw new InvalidOperationException("Koder nie zwrócił poprawnego PNG.");
        }

        var ihdrLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4));
        var insertAt = 8 + 4 + 4 + ihdrLength + 4;

        var pixelsPerMetre = (uint)Math.Round(dpi / 0.0254);
        var chunk = new byte[4 + 4 + 9 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8, 4), pixelsPerMetre);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12, 4), pixelsPerMetre);
        chunk[16] = 1; // jednostka: metr
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17, 4), Crc32(chunk.AsSpan(4, 4 + 9)));

        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result.AsSpan(insertAt));
        png.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        return result;
    }

    /// <summary>CRC-32 (wielomian 0xEDB88320) używany przez PNG dla typu i danych fragmentu.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>Ustawia gęstość w segmencie APP0 „JFIF” albo dodaje taki segment, gdy go nie ma.</summary>
    internal static byte[] WithJfifDensity(byte[] jpeg, int dpi)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidOperationException("Koder nie zwrócił poprawnego JPEG.");
        }

        var hasJfif = jpeg.Length >= 20 && jpeg[2] == 0xFF && jpeg[3] == 0xE0
                      && jpeg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8);
        if (hasJfif)
        {
            var result = (byte[])jpeg.Clone();
            result[13] = 1; // jednostka: punkty na cal
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(14, 2), (ushort)dpi);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(16, 2), (ushort)dpi);
            return result;
        }

        var app0 = new byte[18];
        app0[0] = 0xFF;
        app0[1] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(2, 2), 16);
        "JFIF\0"u8.CopyTo(app0.AsSpan(4, 5));
        app0[9] = 1;
        app0[10] = 1;
        app0[11] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(12, 2), (ushort)dpi);
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(14, 2), (ushort)dpi);

        var withApp0 = new byte[jpeg.Length + app0.Length];
        withApp0[0] = 0xFF;
        withApp0[1] = 0xD8;
        app0.CopyTo(withApp0.AsSpan(2));
        jpeg.AsSpan(2).CopyTo(withApp0.AsSpan(2 + app0.Length));
        return withApp0;
    }
}
