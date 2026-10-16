using Mass.AddressWindow.Pdf.Text;
using Mass.AddressWindow.Rules.Labels;

namespace Mass.AddressWindow.Rules;

/// <summary>
/// Okno z nalepką R. Nalepka to grafika, z której da się odczytać kod kreskowy z numerem nadawczym; sama grafika
/// w oknie tego nie potwierdza. Szuka jej na całej stronie, więc nalepka w niewłaściwym oknie jest zgłaszana
/// jako leżąca poza oknem, a nie jako brak nalepki.
/// </summary>
internal static class LabelWindowChecker
{
    private const double MinPlausibleAspect = 1.5;
    private const double MaxPlausibleAspect = 10;

    public static WindowCheckResult Check(
        WindowRole role,
        AddressWindowSpec window,
        RegisteredLabelRules rules,
        IReadOnlyList<ImageBlock> images,
        IReadOnlyList<TextBlock> texts,
        HashSet<TextBlock> claimedTexts,
        IReadOnlyList<NeighbourWindow>? neighbours = null)
    {
        var issues = new List<ValidationIssue>();
        var textsInWindow = texts
            .Where(t => !claimedTexts.Contains(t) && t.TextBounds.IntersectionArea(window.Area) > 0)
            .ToList();

        var found = Find(images, window, rules);
        if (found is null)
        {
            var hint = textsInWindow.Count > 0 ? " W oknie jest tekst, ale nalepka R musi być wstawiona jako grafika." : "";
            issues.Add(new ValidationIssue(
                IssueCodes.LabelNotFound, IssueSeverity.Error,
                $"Nie znaleziono nalepki R: {window.Name} {window.Area} na pierwszej stronie nie zawiera grafiki.{hint}",
                role));
            return new WindowCheckResult(role, window, WindowContent.RegisteredLabel, null, null, issues, WindowOverflow.None);
        }

        var (image, reading) = found.Value;
        var overflow = CheckGeometry(image.Bounds, window, neighbours, role, issues);
        if (rules.ReadBarcode)
        {
            CheckContent(image.Bounds, reading, rules, role, issues);
        }

        foreach (var other in textsInWindow)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.OtherTextInWindow, IssueSeverity.Warning,
                $"Obok nalepki R {window.Name} pokazuje także tekst: „{AddressMatcher.Snippet(other)}”.",
                role));
        }

        var label = new DetectedLabel(image.Bounds, rules.ReadBarcode ? reading : null);
        return new WindowCheckResult(role, window, WindowContent.RegisteredLabel, null, label, issues, overflow);
    }

    // Images overlapping the window come first; the rest only if they look like a label. The first image whose
    // barcode carries the expected (or any valid) registered number wins; otherwise the best image in the window.
    private static (ImageBlock Image, LabelReading Reading)? Find(IReadOnlyList<ImageBlock> images, AddressWindowSpec window, RegisteredLabelRules rules)
    {
        var ordered = images
            .Select(i => (Image: i, Score: Score(i.Bounds, window.Area)))
            .Where(x => x.Score > 0 || LooksLikeLabel(x.Image))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Image)
            .ToList();

        if (!rules.ReadBarcode)
        {
            var first = ordered.FirstOrDefault(i => Score(i.Bounds, window.Area) > 0);
            return first is null ? null : (first, LabelReader.Read(first));
        }

        var expected = rules.ExpectedNumber is null ? null : RegisteredNumberFormat.Normalize(rules.ExpectedNumber);
        (ImageBlock, LabelReading)? fallback = null;
        (ImageBlock, LabelReading)? anyValid = null;
        foreach (var image in ordered)
        {
            var reading = LabelReader.Read(image);
            if (reading.Number is { IsValid: true } number)
            {
                if (expected is null || number.Normalized == expected)
                {
                    return (image, reading);
                }

                anyValid ??= (image, reading);
            }

            if (fallback is null && Score(image.Bounds, window.Area) > 0)
            {
                fallback = (image, reading);
            }
        }

        return anyValid ?? fallback;
    }

    private static WindowOverflow CheckGeometry(
        RectangleMm bounds, AddressWindowSpec window, IReadOnlyList<NeighbourWindow>? neighbours, WindowRole role, List<ValidationIssue> issues)
    {
        var allowed = window.Area.Deflate(window.ClearanceMm);
        var overflow = WindowOverflow.Of(window.Area, bounds);

        if (bounds.IntersectionArea(window.Area) <= 0)
        {
            var neighbour = neighbours?.FirstOrDefault(n => bounds.IntersectionArea(n.Area) > 0);
            var where = neighbour is null ? "poza oknem" : $"w {neighbour.Name}, czyli w niewłaściwym oknie";
            issues.Add(new ValidationIssue(
                IssueCodes.LabelOutsideWindow, IssueSeverity.Error,
                $"Nalepka R leży w całości {where}: {bounds}, a {window.Name} to {window.Area}.",
                role));
        }
        else if (bounds.Width > allowed.Width + 0.05 || bounds.Height > allowed.Height + 0.05)
        {
            // Moving the label would not help, so say outright that it has to be smaller.
            issues.Add(new ValidationIssue(
                IssueCodes.LabelTooLarge, IssueSeverity.Error,
                $"Nalepka R ma {Size(bounds)}, a {window.Name} mieści najwyżej {Size(allowed)}"
                + (window.ClearanceMm > 0 ? $" (z odstępem {GeometryRules.Mm(window.ClearanceMm)} od krawędzi)" : "")
                + ". Zmniejsz nalepkę.",
                role));
        }
        else if (overflow.Any)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.LabelOutsideWindow, IssueSeverity.Error,
                $"Nalepka R wychodzi poza {window.Name} ({overflow.Describe()}). Okno: {window.Area}, nalepka: {bounds}.",
                role));
        }
        else if (WindowOverflow.Of(allowed, bounds) is { Any: true } tooClose)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.LabelTooCloseToEdge, IssueSeverity.Error,
                $"Nalepka R jest za blisko krawędzi: {window.Name} wymaga {GeometryRules.Mm(window.ClearanceMm)} odstępu, "
                + $"brakuje: {tooClose.Describe()}. Kartka przesuwa się w kopercie i nalepka może zostać zasłonięta.",
                role));
        }

        return overflow;
    }

    private static void CheckContent(RectangleMm bounds, LabelReading reading, RegisteredLabelRules rules, WindowRole role, List<ValidationIssue> issues)
    {
        if (reading.Barcode is null || reading.Number is null)
        {
            // Without a readable barcode we cannot tell a label from any other picture: inconclusive, not invalid.
            issues.Add(new ValidationIssue(
                IssueCodes.LabelUnreadable, IssueSeverity.Inconclusive,
                $"Nie udało się odczytać kodu kreskowego z grafiki w oknie: {reading.Problem}. "
                + "Nalepka R musi być wstawiona jako obraz PNG z generatora nalepek, bez skalowania i bez kompresji obrazów w dokumencie.",
                role));
            return;
        }

        var number = reading.Number;
        if (!number.IsValid)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.LabelNumberInvalid, IssueSeverity.Error,
                $"Numer nadawczy na nalepce R jest niepoprawny: {number.Problem}.",
                role));
        }
        else
        {
            CheckKind(number, reading.Barcode, rules, role, issues);
        }

        if (rules.ExpectedNumber is { } expectedRaw && RegisteredNumberFormat.Normalize(expectedRaw) is var expected && expected != number.Normalized)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.LabelNumberMismatch, IssueSeverity.Error,
                $"Na nalepce R jest numer {number.Normalized}, a dla tej przesyłki oczekiwany jest {expected}.",
                role));
        }

        if (reading.Barcode.ModulePx < rules.MinBarcodeModulePx)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.LabelLowResolution, IssueSeverity.Warning,
                $"Obraz nalepki R ma niską rozdzielczość: najwęższa kreska kodu ma {reading.Barcode.ModulePx.ToString("0.0", GeometryRules.Pl)} px "
                + $"(obraz {reading.WidthPx}×{reading.HeightPx} px na {Size(bounds)}). Obraz został zmniejszony, "
                + "najpewniej przy zapisie do PDF (Word zmniejsza obrazy do ok. 200 DPI); wydrukowany kod może być nieostry. "
                + "Sprawdź odczyt skanerem na wydruku albo twórz PDF narzędziem, które nie zmniejsza obrazów.",
                role));
        }
    }

    private static void CheckKind(RegisteredNumberInfo number, Code128Reading barcode, RegisteredLabelRules rules, WindowRole role, List<ValidationIssue> issues)
    {
        var allowed = number.Type == RegisteredMailType.Domestic ? rules.AllowDomestic : rules.AllowInternational;
        if (!allowed)
        {
            var other = number.Type == RegisteredMailType.Domestic ? "zagraniczny" : "krajowy";
            var mine = number.Type == RegisteredMailType.Domestic ? "krajowy" : "zagraniczny";
            issues.Add(new ValidationIssue(
                IssueCodes.LabelTypeNotAllowed, IssueSeverity.Error,
                $"Nalepka R ma numer {mine} ({number.HumanReadable}), a dla tej przesyłki dopuszczalny jest tylko numer {other}.",
                role));
        }

        if (number.Type == RegisteredMailType.Domestic && !barcode.IsGs1)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.LabelBarcodeNotGs1, IssueSeverity.Warning,
                "Numer krajowy jest zakodowany jako zwykły Code 128, bez znaku FNC1. Nalepka Poczty Polskiej używa GS1-128; "
                + "skanery nie rozpoznają tego kodu jako SSCC.",
                role));
        }
    }

    private static bool LooksLikeLabel(ImageBlock image)
    {
        var aspect = image.Bounds.Height > 0 ? image.Bounds.Width / image.Bounds.Height : 0;
        return aspect is >= MinPlausibleAspect and <= MaxPlausibleAspect && image.Bounds.Width >= 20 && image.Bounds.Height >= 4;
    }

    private static double Score(RectangleMm image, RectangleMm window)
    {
        var overlap = image.IntersectionArea(window);
        var area = image.Width * image.Height;
        return area <= 0 ? 0 : overlap * (overlap / area);
    }

    private static string Size(RectangleMm r) => $"{r.Width.ToString("0.0", GeometryRules.Pl)} × {GeometryRules.Mm(r.Height)}";
}
