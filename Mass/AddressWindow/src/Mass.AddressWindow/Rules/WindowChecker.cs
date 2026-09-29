namespace Mass.AddressWindow.Rules;

internal static class WindowChecker
{
    public static WindowCheckResult Check(
        WindowRole role,
        AddressWindowSpec window,
        AddressContentRules rules,
        IReadOnlyList<IAddressCandidate> candidates,
        HashSet<IAddressCandidate> claimed)
    {
        var issues = new List<ValidationIssue>();
        var block = SelectBlock(window, candidates, claimed);

        if (block is null)
        {
            var hint = window.ElementNameHint is null ? "" : $" ani elementu o nazwie „{window.ElementNameHint}”";
            issues.Add(new ValidationIssue(
                IssueCodes.WindowNotFound, IssueSeverity.Error,
                $"Nie znaleziono obszaru adresowego: w {window.Name} {window.Area} na pierwszej stronie nie ma tekstu{hint}.",
                role));
            return new WindowCheckResult(role, window, null, issues, WindowOverflow.None);
        }

        claimed.Add(block);

        var overflow = GeometryRules.Check(block, window, role, issues);
        var lines = ContentRules.NormalizeLines(block, role, issues);
        ContentRules.Check(block, lines, rules, role, issues);

        foreach (var other in candidates.Where(c => c != block && !claimed.Contains(c)
                                                    && c.TextBounds.IntersectionArea(window.Area) > 0))
        {
            var snippet = string.Join(" / ", other.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0)).Truncate(60);
            issues.Add(new ValidationIssue(
                IssueCodes.OtherTextInWindow, IssueSeverity.Warning,
                $"W {window.Name} widać także inny tekst ({GeometryRules.KindName(other.Kind)}): „{snippet}”.",
                role));
        }

        var sizes = lines.SelectMany(l => l.Source.FontSizes).ToList();
        var detected = new DetectedAddressBlock(
            block.Kind,
            block.Part,
            block.Name,
            block.Bounds,
            block.TextBounds,
            lines.Select(l => l.Text).ToArray(),
            block.Estimated,
            sizes.Count > 0 ? sizes.Min() : null,
            sizes.Count > 0 ? sizes.Max() : null);

        return new WindowCheckResult(role, window, detected, issues, overflow);
    }

    private static IAddressCandidate? SelectBlock(
        AddressWindowSpec window, IReadOnlyList<IAddressCandidate> candidates, HashSet<IAddressCandidate> claimed)
    {
        var pool = candidates.Where(c => !claimed.Contains(c)).ToList();

        if (window.ElementNameHint is { Length: > 0 } hint)
        {
            var named = pool.Where(c => c.Matches(hint)).ToList();
            if (named.Count > 0)
            {
                return named.MaxBy(c => Score(c, window.Area));
            }
        }

        return pool
            .Select(c => (Candidate: c, Score: Score(c, window.Area)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Estimated)
            .Select(x => x.Candidate)
            .FirstOrDefault();
    }

    private static double Score(IAddressCandidate candidate, RectangleMm window)
    {
        var overlap = candidate.TextBounds.IntersectionArea(window);
        var area = candidate.TextBounds.Width * candidate.TextBounds.Height;
        return area <= 0 ? 0 : overlap * (overlap / area);
    }

    private static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
