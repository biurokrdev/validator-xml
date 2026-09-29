using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed record RoundTripAnalysis(DocumentFeatureInventory Inventory, HealthFindingCollector Findings);

public sealed record ImplementationCoverage(IReadOnlyList<ImplementationCoverageItem> Items, string Summary);

public static class ImplementationCoverageBuilder
{
    public static ImplementationCoverage Build(
        DocumentFeatureInventory source,
        RoundTripAnalysis? roundTrip,
        HealthFindingCollector findings,
        IReadOnlyCollection<string> sourceFindingCodes)
    {
        var items = new List<ImplementationCoverageItem>();

        foreach (var key in source.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var occurrence = source.Get(key)!;
            var capability = EditorCapabilityRegistry.Find(key) ?? EditorCapabilityRegistry.Unregistered(key);
            int? roundTripCount = roundTrip is null ? null : roundTrip.Inventory.Count(key);
            var outcome = roundTripCount switch
            {
                null => RoundTripOutcome.NotVerified,
                0 => RoundTripOutcome.Lost,
                var count when count < occurrence.Count => RoundTripOutcome.Reduced,
                _ => RoundTripOutcome.Preserved
            };

            var status = Classify(capability.Effective, outcome);
            var note = capability.Note;

            if (status == CoverageStatus.UnexpectedLoss)
            {
                note = $"{note} ROUND-TRIP: w źródle {occurrence.Count}, po zapisie {roundTripCount} — rejestr deklaruje obsługę, więc to regresja w kodzie albo nieaktualny wpis rejestru.";
            }
            else if (capability.Effective == AppSupportLevel.Unsupported && outcome == RoundTripOutcome.Preserved)
            {
                note = $"{note} ROUND-TRIP: konstrukcja przeżyła zapis ({roundTripCount}) mimo deklarowanego braku obsługi — sprawdź, czy rejestr jest aktualny.";
            }

            items.Add(new ImplementationCoverageItem(
                key, capability.Label, occurrence.Count, occurrence.SampleLocation,
                capability.Reader, capability.Editor, capability.Writer,
                roundTripCount, outcome, status, note, capability.CodePointer));

            AddFeatureFinding(findings, capability, occurrence, status, roundTripCount);
        }

        if (roundTrip is not null)
        {
            AddIntroducedIssues(findings, roundTrip.Findings, sourceFindingCodes);
        }

        var ordered = items
            .OrderBy(item => Rank(item.Status))
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new ImplementationCoverage(ordered, Summarize(ordered, roundTrip is not null));
    }

    private static CoverageStatus Classify(AppSupportLevel declared, RoundTripOutcome outcome)
    {
        var lostOrReduced = outcome is RoundTripOutcome.Lost or RoundTripOutcome.Reduced;

        return declared switch
        {
            AppSupportLevel.Unsupported => CoverageStatus.Unsupported,
            AppSupportLevel.Unknown => CoverageStatus.Unverified,
            _ when lostOrReduced => CoverageStatus.UnexpectedLoss,
            AppSupportLevel.PassThrough => CoverageStatus.PassThrough,
            AppSupportLevel.Partial => CoverageStatus.Partial,
            _ => CoverageStatus.Supported
        };
    }

    private static void AddFeatureFinding(
        HealthFindingCollector findings,
        EditorCapability capability,
        FeatureOccurrence occurrence,
        CoverageStatus status,
        int? roundTripCount)
    {
        var count = occurrence.Count == 1 ? "1 wystąpienie" : $"{occurrence.Count} wystąpień";

        switch (status)
        {
            case CoverageStatus.UnexpectedLoss:
                findings.Add(DocumentHealthCodes.AppRoundTripLoss, StructureIssueSeverity.Warning, HealthStage.Application,
                    $"Round-trip edytora gubi: {capability.Label}",
                    $"W dokumencie źródłowym: {count}; po przejściu DOCX→HTML→DOCX przez nasz reader i writer: {roundTripCount}. Rejestr możliwości deklaruje obsługę ({Describe(capability.Effective)}) — to regresja w kodzie albo nieaktualny wpis rejestru. {capability.Note}",
                    occurrence.SampleLocation, WordOpenImpact.None, PdfConversionImpact.Possible,
                    $"Zbadaj: {capability.CodePointer ?? "reader/writer"}. Porównaj oryginał z wynikiem round-tripu w narzędziu „Porównanie dokumentów” i dopisz test regresji.",
                    capability.Effective, capability.Note);
                break;

            case CoverageStatus.Unsupported:
                findings.Add(DocumentHealthCodes.AppFeatureUnsupported,
                    capability.LossIsBenign ? StructureIssueSeverity.Info : StructureIssueSeverity.Warning, HealthStage.Application,
                    $"Nasza implementacja nie obsługuje: {capability.Label}",
                    $"{count}. {capability.Note}",
                    occurrence.SampleLocation, WordOpenImpact.None,
                    capability.LossIsBenign ? PdfConversionImpact.None : PdfConversionImpact.Possible,
                    capability.LossIsBenign
                        ? null
                        : $"Do czasu implementacji: edytuj taki dokument w Wordzie albo uprzedź użytkownika o utracie. Punkt startu w kodzie: {capability.CodePointer ?? "DocxToHtmlConverter"}.",
                    AppSupportLevel.Unsupported, capability.Note);
                break;

            case CoverageStatus.PassThrough:
                findings.Add(DocumentHealthCodes.AppFeaturePassThrough, StructureIssueSeverity.Info, HealthStage.Application,
                    $"Tylko podgląd, bez edycji: {capability.Label}",
                    $"{count}. {capability.Note}",
                    occurrence.SampleLocation, WordOpenImpact.None, PdfConversionImpact.None,
                    null, AppSupportLevel.PassThrough, capability.Note);
                break;

            case CoverageStatus.Partial:
                findings.Add(DocumentHealthCodes.AppFeaturePartial, StructureIssueSeverity.Info, HealthStage.Application,
                    $"Obsługa częściowa: {capability.Label}",
                    $"{count}. {capability.Note}",
                    occurrence.SampleLocation, WordOpenImpact.None, PdfConversionImpact.None,
                    capability.CodePointer is null ? null : $"Kod: {capability.CodePointer}.",
                    AppSupportLevel.Partial, capability.Note);
                break;

            case CoverageStatus.Unverified:
                findings.Add(DocumentHealthCodes.AppFeatureUnverified, StructureIssueSeverity.Info, HealthStage.Application,
                    $"Obsługa niezweryfikowana: {capability.Label}",
                    $"{count}. {capability.Note}{(roundTripCount is null ? string.Empty : $" Round-trip: {occurrence.Count} → {roundTripCount}.")}",
                    occurrence.SampleLocation, WordOpenImpact.None, PdfConversionImpact.None,
                    $"Sprawdź na tym dokumencie w edytorze i po zapisie; potem uzupełnij EditorCapabilityRegistry ({capability.Key}).",
                    AppSupportLevel.Unknown, capability.Note);
                break;
        }
    }

    private static void AddIntroducedIssues(
        HealthFindingCollector findings,
        HealthFindingCollector roundTripFindings,
        IReadOnlyCollection<string> sourceFindingCodes)
    {
        var sourceCodes = new HashSet<string>(sourceFindingCodes, StringComparer.Ordinal);

        foreach (var group in roundTripFindings.Findings
                     .Where(finding => finding.Stage < HealthStage.Conversion && finding.Severity != StructureIssueSeverity.Info)
                     .Where(finding => !sourceCodes.Contains(finding.Code))
                     .GroupBy(finding => finding.Code, StringComparer.Ordinal))
        {
            var first = group.First();
            var total = roundTripFindings.Count(group.Key);

            findings.Add(DocumentHealthCodes.AppRoundTripIntroducedIssue, StructureIssueSeverity.Warning, HealthStage.Application,
                $"Zapis z edytora wprowadza problem: {first.Title}",
                $"Kondycja WYNIKU round-tripu zgłasza {group.Key} ({total}×), którego nie było w dokumencie źródłowym. {first.Description} Lokalizacja w wyniku zapisu: {first.Location ?? "—"}.",
                null, WordOpenImpact.None,
                first.WordImpact == WordOpenImpact.None ? PdfConversionImpact.None : PdfConversionImpact.Possible,
                $"Błąd leży w HtmlToDocxConverter (albo w tym, co reader przekazał). Wpływ na Worda dla WYNIKU zapisu: {DescribeWord(first.WordImpact)}. Odtwórz: zapisz dokument z edytora i sprawdź go tym narzędziem.",
                AppSupportLevel.Unsupported, first.Remedy);
        }
    }

    private static string Summarize(IReadOnlyList<ImplementationCoverageItem> items, bool roundTripRan)
    {
        if (items.Count == 0)
        {
            return "Inwentarz nie wykrył konstrukcji z rejestru możliwości edytora (albo analiza struktury nie doszła do skutku).";
        }

        var supported = items.Count(item => item.Status == CoverageStatus.Supported);
        var partial = items.Count(item => item.Status is CoverageStatus.Partial or CoverageStatus.PassThrough);
        var unsupported = items.Count(item => item.Status == CoverageStatus.Unsupported);
        var losses = items.Count(item => item.Status == CoverageStatus.UnexpectedLoss);
        var unverified = items.Count(item => item.Status == CoverageStatus.Unverified);

        var parts = new List<string> { $"{items.Count} konstrukcji w dokumencie: {supported} obsługiwanych w pełni" };

        if (partial > 0) parts.Add($"{partial} częściowo lub tylko w podglądzie");
        if (unsupported > 0) parts.Add($"{unsupported} nieobsługiwanych ({string.Join(", ", items.Where(item => item.Status == CoverageStatus.Unsupported).Select(item => item.Label).Take(4))})");
        if (losses > 0) parts.Add($"{losses} NIEOCZEKIWANYCH strat w round-tripie ({string.Join(", ", items.Where(item => item.Status == CoverageStatus.UnexpectedLoss).Select(item => item.Label).Take(4))})");
        if (unverified > 0) parts.Add($"{unverified} niezweryfikowanych");

        var suffix = roundTripRan
            ? string.Empty
            : " Round-trip nie został uruchomiony — kolumna „po zapisie” pochodzi wyłącznie z rejestru.";

        return string.Join("; ", parts) + "." + suffix;
    }

    private static int Rank(CoverageStatus status) => status switch
    {
        CoverageStatus.UnexpectedLoss => 0,
        CoverageStatus.Unsupported => 1,
        CoverageStatus.Unverified => 2,
        CoverageStatus.PassThrough => 3,
        CoverageStatus.Partial => 4,
        _ => 5
    };

    private static string Describe(AppSupportLevel level) => level switch
    {
        AppSupportLevel.Full => "pełna",
        AppSupportLevel.Partial => "częściowa",
        AppSupportLevel.PassThrough => "pass-through",
        AppSupportLevel.Unsupported => "brak",
        _ => "niezweryfikowana"
    };

    private static string DescribeWord(WordOpenImpact impact) => impact switch
    {
        WordOpenImpact.CannotOpen => "Word NIE otworzy zapisanego pliku",
        WordOpenImpact.Repair => "Word zażąda naprawy zapisanego pliku",
        _ => "bez wpływu na otwarcie w Wordzie"
    };
}
