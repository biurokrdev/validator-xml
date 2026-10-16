using Mass.AddressWindow.Pdf.Text;

namespace Mass.AddressWindow.Rules;

/// <summary>Inne okno tego samego układu; służy do nazwania miejsca, w którym znaleziono treść poza właściwym oknem.</summary>
internal sealed record NeighbourWindow(string Name, RectangleMm Area);

/// <summary>
/// Okno adresowe. Szuka bloku adresowego, sprawdza jego położenie względem okna i treść. Bez danych odbiorcy
/// potwierdza tylko, że blok przypomina adres; z danymi odbiorcy (<see cref="ValidationProfile.ExpectedRecipient"/>)
/// szuka tego konkretnego adresu na całej stronie i zgłasza, gdy leży poza oknem albo różni się treścią.
/// </summary>
internal static class WindowChecker
{
    public static WindowCheckResult Check(
        WindowRole role,
        AddressWindowSpec window,
        AddressContentRules rules,
        IReadOnlyList<TextBlock> candidates,
        HashSet<TextBlock> claimed,
        IReadOnlyList<string>? expectedRecipient = null,
        IReadOnlyList<NeighbourWindow>? neighbours = null)
    {
        var issues = new List<ValidationIssue>();
        var pool = candidates.Where(c => !claimed.Contains(c)).ToList();
        var block = expectedRecipient is { Count: > 0 }
            ? SelectExpected(pool, expectedRecipient, window, role, issues)
            : SelectAddressLike(pool, window, role, issues);

        if (block is null)
        {
            return new WindowCheckResult(role, window, WindowContent.Address, null, null, issues, WindowOverflow.None);
        }

        claimed.Add(block);

        var overflow = GeometryRules.Check(block, window, role, issues);
        DescribeWhenEntirelyOutside(block, window, neighbours, role, issues);
        var lines = ContentRules.NormalizeLines(block, role, issues);
        ContentRules.Check(block, lines, rules, role, issues);
        var match = CompareWithExpected(block, expectedRecipient, role, issues);
        ReportOtherText(block, pool, window, role, issues);

        return new WindowCheckResult(role, window, WindowContent.Address, Describe(block, lines), null, issues, overflow, match);
    }

    // Order of preference: an address-like block in the window, an address-like block elsewhere (windows swapped),
    // any text in the window (so the content rules can say what is wrong with it), nothing.
    private static TextBlock? SelectAddressLike(List<TextBlock> pool, AddressWindowSpec window, WindowRole role, List<ValidationIssue> issues)
    {
        var inWindow = pool
            .Select(c => (Block: c, Score: Overlap(c, window.Area)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Block)
            .ToList();

        var addressLike = inWindow.FirstOrDefault(AddressMatcher.LooksLikeAddress);
        if (addressLike is not null)
        {
            return addressLike;
        }

        var elsewhere = pool.Where(c => Overlap(c, window.Area) <= 0 && AddressMatcher.LooksLikeAddress(c))
            .OrderByDescending(c => AddressMatcher.NonEmptyLines(c).Count is >= 3 and <= 6)
            .ThenBy(c => Distance(c.TextBounds, window.Area))
            .FirstOrDefault();
        if (elsewhere is not null)
        {
            return elsewhere;
        }

        if (inWindow.Count > 0)
        {
            return inWindow[0];
        }

        issues.Add(new ValidationIssue(
            IssueCodes.WindowNotFound, IssueSeverity.Error,
            $"Nie znaleziono obszaru adresowego: w {window.Name} {window.Area} na pierwszej stronie nie ma tekstu.",
            role));
        return null;
    }

    private static TextBlock? SelectExpected(
        List<TextBlock> pool, IReadOnlyList<string> expected, AddressWindowSpec window, WindowRole role, List<ValidationIssue> issues)
    {
        var best = pool
            .Select(c => (Block: c, Matched: AddressMatcher.MatchedLines(c, expected)))
            .Where(x => x.Matched > 0)
            .OrderByDescending(x => x.Matched)
            .ThenByDescending(x => Overlap(x.Block, window.Area))
            .Select(x => x.Block)
            .FirstOrDefault();
        if (best is not null)
        {
            return best;
        }

        issues.Add(new ValidationIssue(
            IssueCodes.AddressNotFound, IssueSeverity.Error,
            $"Na pierwszej stronie nie ma adresu odbiorcy „{string.Join(" / ", expected)}”: żaden blok tekstu nie zawiera jego wierszy.",
            role));
        return null;
    }

    private static RecipientMatch CompareWithExpected(TextBlock block, IReadOnlyList<string>? expected, WindowRole role, List<ValidationIssue> issues)
    {
        if (expected is not { Count: > 0 })
        {
            return RecipientMatch.NotChecked;
        }

        var missing = AddressMatcher.MissingLines(block, expected);
        if (missing.Count == 0)
        {
            return RecipientMatch.Matched;
        }

        issues.Add(new ValidationIssue(
            IssueCodes.AddressMismatch, IssueSeverity.Error,
            $"Adres w dokumencie różni się od danych odbiorcy. Brakuje wierszy: {string.Join("; ", missing.Select(m => $"„{m}”"))}.",
            role));
        return RecipientMatch.Mismatch;
    }

    private static void DescribeWhenEntirelyOutside(
        TextBlock block, AddressWindowSpec window, IReadOnlyList<NeighbourWindow>? neighbours, WindowRole role, List<ValidationIssue> issues)
    {
        if (Overlap(block, window.Area) > 0)
        {
            return;
        }

        // Replace the generic "sticks out by N mm" message with one that says where the address actually is.
        issues.RemoveAll(i => i.Code == IssueCodes.AddressOutsideWindow);
        var neighbour = neighbours?.FirstOrDefault(n => block.TextBounds.IntersectionArea(n.Area) > 0);
        var where = neighbour is null ? "poza oknem" : $"w {neighbour.Name}, czyli w niewłaściwym oknie";
        issues.Add(new ValidationIssue(
            IssueCodes.AddressOutsideWindow, IssueSeverity.Error,
            $"Adres leży w całości {where}: {block.TextBounds}, a {window.Name} to {window.Area}.",
            role));
    }

    private static void ReportOtherText(TextBlock block, List<TextBlock> pool, AddressWindowSpec window, WindowRole role, List<ValidationIssue> issues)
    {
        foreach (var other in pool.Where(c => c != block && Overlap(c, window.Area) > 0))
        {
            issues.Add(new ValidationIssue(
                IssueCodes.OtherTextInWindow, IssueSeverity.Warning,
                $"W {window.Name} widać także inny tekst: „{AddressMatcher.Snippet(other)}”.",
                role));
        }
    }

    private static DetectedAddressBlock Describe(TextBlock block, List<(string Text, TextLine Source)> lines)
    {
        var sizes = lines.SelectMany(l => l.Source.FontSizes).ToList();
        return new DetectedAddressBlock(
            block.TextBounds,
            lines.Select(l => l.Text).ToArray(),
            sizes.Count > 0 ? sizes.Min() : null,
            sizes.Count > 0 ? sizes.Max() : null);
    }

    // Overlap area weighted by the share of the block inside the window: a small address fully inside wins
    // over a long body paragraph that merely crosses the window.
    private static double Overlap(TextBlock block, RectangleMm window)
    {
        var overlap = block.TextBounds.IntersectionArea(window);
        var area = block.TextBounds.Width * block.TextBounds.Height;
        return area <= 0 ? 0 : overlap * (overlap / area);
    }

    private static double Distance(RectangleMm a, RectangleMm b)
    {
        var dx = Math.Max(0, Math.Max(b.Left - a.Right, a.Left - b.Right));
        var dy = Math.Max(0, Math.Max(b.Top - a.Bottom, a.Top - b.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
