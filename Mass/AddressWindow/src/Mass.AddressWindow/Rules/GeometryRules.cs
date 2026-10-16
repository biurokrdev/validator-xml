using System.Globalization;
using Mass.AddressWindow.Pdf.Text;

namespace Mass.AddressWindow.Rules;

internal static class GeometryRules
{
    internal static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static WindowOverflow Check(TextBlock block, AddressWindowSpec window, WindowRole role, List<ValidationIssue> issues)
    {
        if (block.IsRotated)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.TextRotated, IssueSeverity.Error,
                $"Tekst adresu w {window.Name} jest obrócony lub pionowy. Adres musi być poziomy, równoległy do dłuższego boku koperty.",
                role));
        }

        var text = block.TextBounds;
        var outside = WindowOverflow.Of(window.Area, text);
        if (outside.Any)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.AddressOutsideWindow, IssueSeverity.Error,
                $"Tekst adresu wychodzi poza {window.Name} ({outside.Describe()}). Okno: {window.Area}, tekst: {text}.",
                role));
        }
        else if (window.ClearanceMm > 0)
        {
            var tooClose = WindowOverflow.Of(window.Area.Deflate(window.ClearanceMm), text);
            if (tooClose.Any)
            {
                issues.Add(new ValidationIssue(
                    IssueCodes.AddressTooCloseToEdge, IssueSeverity.Error,
                    $"Tekst adresu jest za blisko krawędzi: {window.Name} wymaga {Mm(window.ClearanceMm)} odstępu, "
                    + $"brakuje: {tooClose.Describe()}. Kartka przesuwa się w kopercie i tekst może zostać zasłonięty.",
                    role));
            }
        }

        return outside;
    }

    internal static string Mm(double value) => value.ToString("0.0", Pl) + " mm";
}
