using System.Globalization;
using System.Xml.Linq;

namespace Mass.AddressWindow.Docx;

internal static class Units
{
    public const double EmuPerMm = 36000.0;
    public const double TwipsPerMm = 1440.0 / 25.4;
    public const double MmPerPt = 25.4 / 72.0;

    public static double EmuToMm(double emu) => emu / EmuPerMm;

    public static double TwipsToMm(double twips) => twips / TwipsPerMm;

    public static double PtToMm(double pt) => pt * MmPerPt;

    public static double? TwipsAttrToMm(XElement? element, XName attribute)
    {
        var raw = element?.Attribute(attribute)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips))
        {
            return TwipsToMm(twips);
        }

        return TryParseLength(raw, defaultUnitMm: 1 / TwipsPerMm, out var mm) ? mm : null;
    }

    public static double? EmuAttrToMm(XElement? element, XName attribute)
    {
        var raw = element?.Attribute(attribute)?.Value;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var emu)
            ? EmuToMm(emu)
            : null;
    }

    public static double? ParseDouble(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static bool TryParseLength(string? raw, double defaultUnitMm, out double mm)
    {
        mm = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim().ToLowerInvariant();
        var unitStart = text.Length;
        while (unitStart > 0 && char.IsLetter(text[unitStart - 1]))
        {
            unitStart--;
        }

        if (!double.TryParse(text[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        double? factor = text[unitStart..] switch
        {
            "" => defaultUnitMm,
            "mm" => 1,
            "cm" => 10,
            "in" => 25.4,
            "pt" => MmPerPt,
            "pc" or "pi" => 12 * MmPerPt,
            "px" => 0.75 * MmPerPt,
            "emu" => 1 / EmuPerMm,
            _ => null,
        };

        if (factor is null)
        {
            return false;
        }

        mm = value * factor.Value;
        return true;
    }
}
