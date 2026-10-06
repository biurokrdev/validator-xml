namespace Mass.AddressWindow.Rules;

/// <summary>
/// Okno z nalepką R: nie ma tu reguł treści, liczy się tylko, czy w oknie jest grafika i czy mieści się
/// w obszarze widocznym przy każdym położeniu kartki. Treść nalepki (kod kreskowy) nie jest odczytywana.
/// </summary>
internal static class LabelWindowChecker
{
    public static WindowCheckResult Check(
        WindowRole role,
        AddressWindowSpec window,
        IReadOnlyList<ImageCandidate> images,
        IReadOnlyList<IAddressCandidate> texts,
        HashSet<IAddressCandidate> claimedTexts)
    {
        var issues = new List<ValidationIssue>();
        var textsInWindow = texts
            .Where(t => !claimedTexts.Contains(t) && t.TextBounds.IntersectionArea(window.Area) > 0)
            .ToList();

        var image = images
            .Select(i => (Image: i, Score: Score(i.Bounds, window.Area)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Image.Estimated)
            .Select(x => x.Image)
            .FirstOrDefault();

        if (image is null)
        {
            var hint = textsInWindow.Count > 0
                ? " W oknie jest tekst, ale nalepka R musi być wstawiona jako grafika."
                : "";
            issues.Add(new ValidationIssue(
                IssueCodes.LabelNotFound, IssueSeverity.Error,
                $"Nie znaleziono nalepki R: {window.Name} {window.Area} na pierwszej stronie nie zawiera grafiki.{hint}",
                role));
            return new WindowCheckResult(role, window, WindowContent.RegisteredLabel, null, null, issues, WindowOverflow.None);
        }

        var bounds = image.Bounds;
        var allowed = window.Area.Deflate(window.ClearanceMm);
        var overflow = WindowOverflow.Of(window.Area, bounds);
        if (bounds.Width > allowed.Width + 0.05 || bounds.Height > allowed.Height + 0.05)
        {
            // Przesunięcie nie pomoże, więc mówimy wprost, że trzeba zmniejszyć nalepkę.
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

        if (image.Estimated)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.PositionEstimated, IssueSeverity.Info,
                "Położenie nalepki R wyliczono w przybliżeniu, bo grafika jest osadzona w tekście. "
                + "Dokładne położenie daje grafika zakotwiczona do strony.",
                role));
        }

        foreach (var other in textsInWindow)
        {
            var snippet = string.Join(" / ", other.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));
            snippet = snippet.Length <= 60 ? snippet : snippet[..59] + "…";
            issues.Add(new ValidationIssue(
                IssueCodes.OtherTextInWindow, IssueSeverity.Warning,
                $"Obok nalepki R {window.Name} pokazuje także tekst ({GeometryRules.KindName(other.Kind)}): „{snippet}”.",
                role));
        }

        var label = new DetectedLabel(image.Part, image.Name, bounds, image.Estimated);
        return new WindowCheckResult(role, window, WindowContent.RegisteredLabel, null, label, issues, overflow);
    }

    private static double Score(RectangleMm image, RectangleMm window)
    {
        var overlap = image.IntersectionArea(window);
        var area = image.Width * image.Height;
        return area <= 0 ? 0 : overlap * (overlap / area);
    }

    private static string Size(RectangleMm r) =>
        $"{r.Width.ToString("0.0", GeometryRules.Pl)} × {GeometryRules.Mm(r.Height)}";
}
