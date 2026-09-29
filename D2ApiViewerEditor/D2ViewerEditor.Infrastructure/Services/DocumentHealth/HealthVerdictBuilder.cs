using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed record HealthVerdicts(
    HealthVerdict Verdict,
    string VerdictSummary,
    WordOpenVerdict WordOpen,
    string WordOpenSummary,
    PdfConversionVerdict PdfConversion,
    string PdfConversionSummary);

public static class HealthVerdictBuilder
{
    public static HealthVerdicts Build(
        HealthFindingCollector findings,
        IReadOnlyList<ConversionProbeResult> probes,
        string detectedFormat)
    {
        var all = findings.Findings;

        var staticFindings = all.Where(finding => finding.Stage < HealthStage.Conversion).ToList();
        var wordImpact = staticFindings.Count == 0 ? WordOpenImpact.None : staticFindings.Max(finding => finding.WordImpact);
        var wordOpen = wordImpact switch
        {
            WordOpenImpact.CannotOpen => WordOpenVerdict.CannotOpen,
            WordOpenImpact.Repair => WordOpenVerdict.Repair,
            _ => WordOpenVerdict.Ok
        };

        var pdfImpact = all.Count == 0 ? PdfConversionImpact.None : all.Max(finding => finding.PdfImpact);
        var failedProbes = probes.Where(probe => probe.Status == ProbeStatus.Failed).ToList();
        var pdfBlocked = pdfImpact == PdfConversionImpact.Blocking ||
                         failedProbes.Any(probe => probe.Id is ConversionProbeRunner.PdfConversionProbe or ConversionProbeRunner.UploadGateProbe or ConversionProbeRunner.EditorImportProbe);
        var pdfLikely = pdfImpact == PdfConversionImpact.Likely || failedProbes.Count > 0;
        var pdfAtRisk = pdfImpact == PdfConversionImpact.Possible || probes.Any(probe => probe.Status == ProbeStatus.Warning);

        var pdfConversion = pdfBlocked ? PdfConversionVerdict.Blocked
            : pdfLikely ? PdfConversionVerdict.Likely
            : pdfAtRisk ? PdfConversionVerdict.AtRisk
            : PdfConversionVerdict.Ok;

        var containerErrors = all.Any(finding =>
            finding.Severity == StructureIssueSeverity.Error &&
            finding.Stage is HealthStage.File or HealthStage.Package);

        var verdict = wordOpen == WordOpenVerdict.CannotOpen || containerErrors ? HealthVerdict.Corrupt
            : wordOpen == WordOpenVerdict.Repair ? HealthVerdict.NeedsRepair
            : findings.WarningCount > 0 || findings.ErrorCount > 0 || pdfConversion != PdfConversionVerdict.Ok ? HealthVerdict.Warnings
            : HealthVerdict.Healthy;

        return new HealthVerdicts(
            verdict,
            SummarizeVerdict(verdict, findings, detectedFormat, all),
            wordOpen,
            SummarizeWord(wordOpen, all, findings),
            pdfConversion,
            SummarizePdf(pdfConversion, all, failedProbes, probes));
    }

    private static string SummarizeVerdict(HealthVerdict verdict, HealthFindingCollector findings, string detectedFormat, IReadOnlyList<HealthFinding> all)
    {
        return verdict switch
        {
            HealthVerdict.Corrupt =>
                $"Plik jest uszkodzony albo nie jest dokumentem DOCX (rozpoznany format: {detectedFormat}). " +
                $"{findings.ErrorCount} błędów, w tym: {TopTitles(all, StructureIssueSeverity.Error, 3)}.",
            HealthVerdict.NeedsRepair =>
                $"Pakiet jest czytelny, ale narusza wymagania Worda — Word zażąda naprawy. {findings.ErrorCount} błędów: {TopTitles(all, StructureIssueSeverity.Error, 3)}.",
            HealthVerdict.Warnings =>
                $"Dokument otwiera się poprawnie; {findings.WarningCount} ostrzeżeń i {findings.InfoCount} informacji dotyczy zgodności konwerterów i luk naszej implementacji" +
                $"{(all.Any(finding => finding.Stage == HealthStage.Application && finding.Severity == StructureIssueSeverity.Warning) ? $" (etap „Aplikacja”: {all.Count(finding => finding.Stage == HealthStage.Application && finding.Severity == StructureIssueSeverity.Warning)} ostrzeżeń)" : string.Empty)}.",
            _ => "Nie znaleziono problemów: kontener, pakiet OPC, XML i struktura WordprocessingML są zgodne z wymaganiami Worda."
        };
    }

    private static string SummarizeWord(WordOpenVerdict verdict, IReadOnlyList<HealthFinding> all, HealthFindingCollector findings)
    {
        return verdict switch
        {
            WordOpenVerdict.CannotOpen =>
                $"Word nie otworzy tego pliku: {TopTitlesByImpact(all, WordOpenImpact.CannotOpen, 3)}.",
            WordOpenVerdict.Repair =>
                $"Word otworzy plik dopiero po komunikacie „znaleziono nieczytelną zawartość” i naprawie: {TopTitlesByImpact(all, WordOpenImpact.Repair, 3)}.",
            _ => findings.WarningCount > 0
                ? "Word otworzy plik bez naprawy; ostrzeżenia dotyczą wyłącznie innych konsumentów (konwertery, edytor)."
                : "Word otworzy plik bez zastrzeżeń."
        };
    }

    private static string SummarizePdf(
        PdfConversionVerdict verdict,
        IReadOnlyList<HealthFinding> all,
        IReadOnlyList<ConversionProbeResult> failedProbes,
        IReadOnlyList<ConversionProbeResult> probes)
    {
        var reasons = new List<string>();

        switch (verdict)
        {
            case PdfConversionVerdict.Blocked:
                reasons.AddRange(all.Where(finding => finding.PdfImpact == PdfConversionImpact.Blocking).Select(finding => finding.Title).Distinct().Take(3));
                reasons.AddRange(failedProbes
                    .Where(probe => probe.Id is ConversionProbeRunner.PdfConversionProbe or ConversionProbeRunner.UploadGateProbe or ConversionProbeRunner.EditorImportProbe)
                    .Select(probe => $"{probe.Name}: {probe.Message}"));
                return $"Konwersja do PDF nie powiedzie się: {string.Join("; ", reasons.Distinct().Take(4))}.";

            case PdfConversionVerdict.Likely:
                reasons.AddRange(all.Where(finding => finding.PdfImpact == PdfConversionImpact.Likely).Select(finding => finding.Title).Distinct().Take(3));
                reasons.AddRange(failedProbes.Select(probe => $"{probe.Name}: {probe.Message}"));
                return $"Konwersja prawdopodobnie zakończy się błędem lub utratą treści: {string.Join("; ", reasons.Distinct().Take(4))}.";

            case PdfConversionVerdict.AtRisk:
                reasons.AddRange(all.Where(finding => finding.PdfImpact == PdfConversionImpact.Possible).Select(finding => finding.Title).Distinct().Take(3));
                reasons.AddRange(probes.Where(probe => probe.Status == ProbeStatus.Warning).Select(probe => probe.Name));
                return $"Konwersja powinna się udać, ale wynik może odbiegać od Worda: {string.Join("; ", reasons.Distinct().Take(4))}.";

            default:
                var passed = probes.Count(probe => probe.Status == ProbeStatus.Passed);
                return probes.Count == 0
                    ? "Brak ustaleń wpływających na konwersję."
                    : $"Brak ustaleń wpływających na konwersję; {passed} z {probes.Count(probe => probe.Status != ProbeStatus.Skipped)} prób przetworzenia zakończyło się powodzeniem.";
        }
    }

    private static string TopTitles(IReadOnlyList<HealthFinding> all, StructureIssueSeverity severity, int count)
    {
        var titles = all.Where(finding => finding.Severity == severity).Select(finding => finding.Title).Distinct().Take(count).ToList();

        return titles.Count == 0 ? "—" : string.Join("; ", titles);
    }

    private static string TopTitlesByImpact(IReadOnlyList<HealthFinding> all, WordOpenImpact impact, int count)
    {
        var titles = all.Where(finding => finding.WordImpact == impact).Select(finding => finding.Title).Distinct().Take(count).ToList();

        return titles.Count == 0 ? "—" : string.Join("; ", titles);
    }
}
