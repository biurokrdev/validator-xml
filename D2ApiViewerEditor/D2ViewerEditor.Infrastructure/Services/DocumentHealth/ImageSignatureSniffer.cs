using System.Text;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Rozpoznanie formatu obrazu po sygnaturze bajtów. Zadeklarowany content type części bywa
/// kłamstwem (bajty JPEG w części image/png) — Word to toleruje, część konwerterów DOCX→PDF nie
/// dekoduje takiego obrazu albo przerywa konwersję.
/// </summary>
public static class ImageSignatureSniffer
{
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
        {
            return null;
        }

        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return "image/gif";
        if (b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0x00) ||
            (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00 && b[3] == 0x2A)) return "image/tiff";
        if (b.Length > 44 && b[0] == 0x01 && b[1] == 0x00 && b[2] == 0x00 && b[3] == 0x00 &&
            b[40] == 0x20 && b[41] == 0x45 && b[42] == 0x4D && b[43] == 0x46) return "image/x-emf";
        if (b[0] == 0xD7 && b[1] == 0xCD && b[2] == 0xC6 && b[3] == 0x9A) return "image/x-wmf";
        if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0x09 && b[3] == 0x00) return "image/x-wmf";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (b[0] == 0x1F && b[1] == 0x8B) return "application/gzip";
        if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return "image/x-icon";

        var head = Encoding.ASCII.GetString(b[..Math.Min(b.Length, 512)]).TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
             head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            return "image/svg+xml";
        }

        return null;
    }

    /// <summary>Typy, dla których brak rozpoznanej sygnatury nie jest podejrzany (kontenery, metapliki spakowane).</summary>
    public static bool IsOpaqueContentType(string? contentType) =>
        contentType is null ||
        contentType.Contains("gzip", StringComparison.OrdinalIgnoreCase) ||
        contentType.EndsWith("emz", StringComparison.OrdinalIgnoreCase) ||
        contentType.EndsWith("wmz", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("pict", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase);

    public static bool IsEquivalent(string declared, string sniffed)
    {
        static string Normalize(string type) => type.ToLowerInvariant() switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/x-png" => "image/png",
            "image/emf" or "image/x-emf" => "image/x-emf",
            "image/wmf" or "image/x-wmf" => "image/x-wmf",
            "image/x-ms-bmp" => "image/bmp",
            "image/tif" => "image/tiff",
            var other => other
        };

        return Normalize(declared) == Normalize(sniffed);
    }
}
