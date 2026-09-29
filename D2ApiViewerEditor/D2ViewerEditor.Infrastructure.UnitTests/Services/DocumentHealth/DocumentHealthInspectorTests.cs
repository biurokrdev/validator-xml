using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentHealth;

/// <summary>
/// „Kondycja dokumentu”: każde uszkodzenie z korpusu ma dać ustalenie o stabilnym kodzie i
/// właściwy werdykt (Word: otworzy / naprawi / nie otworzy; PDF: ok / ryzyko / prawdopodobny
/// błąd / blokada). Narzędzie nigdy nie rzuca — uszkodzony plik jest wynikiem, nie wyjątkiem.
/// </summary>
[TestFixture]
public class DocumentHealthInspectorTests
{
    [Test]
    public async Task HealthyDocument_HasNoErrorsAndPassesProbes()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy());

        report.DetectedFormat.Should().Be("docx");
        report.MainDocumentPartPath.Should().Be("word/document.xml");
        report.ErrorCount.Should().Be(0);
        report.WordOpen.Should().Be(WordOpenVerdict.Ok);
        report.Verdict.Should().BeOneOf(HealthVerdict.Healthy, HealthVerdict.Warnings);
        report.Probe(ConversionProbeRunner.SdkOpenProbe).Status.Should().Be(ProbeStatus.Passed);
        report.Probe(ConversionProbeRunner.UploadGateProbe).Status.Should().Be(ProbeStatus.Passed);
        report.Probe(ConversionProbeRunner.EditorImportProbe).Status.Should().Be(ProbeStatus.Passed);
        report.Probe(ConversionProbeRunner.PdfConversionProbe).Status.Should().Be(ProbeStatus.Passed);
        report.Probe(LibreOfficeConverterProbe.ProbeId).Status.Should().Be(ProbeStatus.Skipped);
        report.Statistics.Paragraphs.Should().Be(3);
        report.Statistics.Sections.Should().Be(1);
    }

    [Test]
    public async Task WithoutConversionProbes_RunsOnlyCheapProbes()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), probes: false);

        report.Probes.Select(probe => probe.Id).Should().BeEquivalentTo(
            ConversionProbeRunner.SdkOpenProbe, ConversionProbeRunner.SchemaProbe, ConversionProbeRunner.UploadGateProbe);
    }

    [Test]
    public async Task TruncatedFile_IsCorruptAndBlocksEverything()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Truncated());

        report.Has(DocumentHealthCodes.FileZipUnreadable).Should().BeTrue();
        report.First(DocumentHealthCodes.FileZipUnreadable).Description.Should().Contain("UCIĘTY");
        report.Verdict.Should().Be(HealthVerdict.Corrupt);
        report.WordOpen.Should().Be(WordOpenVerdict.CannotOpen);
        report.PdfConversion.Should().Be(PdfConversionVerdict.Blocked);
        report.Probe(ConversionProbeRunner.SdkOpenProbe).Status.Should().Be(ProbeStatus.Failed);
        report.Probe(ConversionProbeRunner.UploadGateProbe).Status.Should().Be(ProbeStatus.Failed);
    }

    [Test]
    public async Task CorruptedEntryBytes_ReportCrcMismatch()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.CrcMismatch());

        report.Has(DocumentHealthCodes.ZipEntryCrcMismatch).Should().BeTrue();
        report.First(DocumentHealthCodes.ZipEntryCrcMismatch).Location.Should().Be("word/document.xml");
        report.Verdict.Should().Be(HealthVerdict.Corrupt);
        report.WordOpen.Should().Be(WordOpenVerdict.CannotOpen);
    }

    [Test]
    public async Task DuplicateZipEntry_IsReported()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.DuplicateEntry());

        report.Has(DocumentHealthCodes.ZipEntryDuplicate).Should().BeTrue();
        report.Verdict.Should().Be(HealthVerdict.Corrupt);
    }

    [TestCase("rtf")]
    [TestCase("html")]
    public async Task NonZipInput_IsNamedByFormat(string format)
    {
        var bytes = format == "rtf" ? DocumentHealthCorpus.Rtf() : DocumentHealthCorpus.Html();

        var report = await DocumentHealthTestHost.Inspect(bytes);

        report.DetectedFormat.Should().Be(format);
        report.Has(DocumentHealthCodes.FileNotOoxmlPackage).Should().BeTrue();
        report.Verdict.Should().Be(HealthVerdict.Corrupt);
        report.PdfConversion.Should().Be(PdfConversionVerdict.Blocked);
        // Word rozpoznaje RTF/HTML po treści — to nie jest „nie otworzy”, tylko „to nie DOCX”.
        report.First(DocumentHealthCodes.FileNotOoxmlPackage).WordImpact.Should().Be(WordOpenImpact.None);
    }

    [Test]
    public async Task LegacyBinaryDoc_IsRecognized()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.LegacyDoc());

        report.DetectedFormat.Should().Be("doc");
        report.Has(DocumentHealthCodes.FileLegacyBinaryDoc).Should().BeTrue();
        report.WordOpen.Should().Be(WordOpenVerdict.Ok, "Word otwiera .doc niezależnie od rozszerzenia; awaria SDK nie jest ustaleniem o Wordzie");
        report.Probe(ConversionProbeRunner.SdkOpenProbe).Status.Should().Be(ProbeStatus.Failed);
        report.PdfConversion.Should().Be(PdfConversionVerdict.Blocked, "bramka uploadu odrzuca plik, który nie jest ZIP-em");
    }

    [Test]
    public async Task EncryptedPackage_IsRecognizedAsPasswordProtected()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.EncryptedPackage());

        report.DetectedFormat.Should().Be("encrypted-ooxml");
        report.Has(DocumentHealthCodes.FileEncryptedPackage).Should().BeTrue();
        report.First(DocumentHealthCodes.FileEncryptedPackage).PdfImpact.Should().Be(PdfConversionImpact.Blocking);
    }

    [Test]
    public async Task EmptyInput_IsReportedWithoutThrowing()
    {
        var report = await DocumentHealthTestHost.Inspect([]);

        report.DetectedFormat.Should().Be("empty");
        report.Has(DocumentHealthCodes.FileEmpty).Should().BeTrue();
        report.Verdict.Should().Be(HealthVerdict.Corrupt);
    }

    [Test]
    public async Task ZipWithoutContentTypes_CannotBeOpened()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.ZipWithoutContentTypes());

        report.DetectedFormat.Should().Be("zip");
        report.Has(DocumentHealthCodes.ContentTypesPartMissing).Should().BeTrue();
        report.WordOpen.Should().Be(WordOpenVerdict.CannotOpen);
    }

    [Test]
    public async Task MalformedMainXml_LocatesTheError()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.MalformedMainXml());

        var finding = report.First(DocumentHealthCodes.XmlNotWellFormed);
        finding.Location.Should().StartWith("word/document.xml:");
        finding.WordImpact.Should().Be(WordOpenImpact.CannotOpen);
        finding.PdfImpact.Should().Be(PdfConversionImpact.Blocking);
        report.Verdict.Should().Be(HealthVerdict.Corrupt);
        report.Probe(ConversionProbeRunner.SdkOpenProbe).Status.Should().Be(ProbeStatus.Failed);
    }

    [Test]
    public async Task MalformedSecondaryPart_RequiresRepairButKeepsStructureAnalysis()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.MalformedStylesXml());

        var finding = report.First(DocumentHealthCodes.XmlNotWellFormed);
        finding.Location.Should().StartWith("word/styles.xml:");
        finding.WordImpact.Should().Be(WordOpenImpact.Repair);
        report.Statistics.Paragraphs.Should().Be(1, "główna część nadal jest analizowana");
    }

    [Test]
    public async Task UndeclaredIgnorablePrefix_RequiresRepair()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.UndeclaredIgnorablePrefix());

        var finding = report.First(DocumentHealthCodes.XmlIgnorablePrefixUndeclared);
        finding.Description.Should().Contain("'zzz'").And.NotContain("'w14' nie ma");
        report.Findings.Count(item => item.Code == DocumentHealthCodes.XmlIgnorablePrefixUndeclared).Should().Be(1);
        finding.Remedy.Should().Contain("xmlns:");
        report.WordOpen.Should().Be(WordOpenVerdict.Repair);
        report.Verdict.Should().Be(HealthVerdict.NeedsRepair);
    }

    [Test]
    public async Task TableProblems_AreNamedIndividually()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.TableProblems());

        report.Has(DocumentHealthCodes.TableCellWithoutParagraph).Should().BeTrue();
        report.First(DocumentHealthCodes.TableCellWithoutParagraph).Location.Should().Contain("w:tc[1]");
        report.Has(DocumentHealthCodes.TableRowWithoutCells).Should().BeTrue();
        report.Has(DocumentHealthCodes.TableWithoutRows).Should().BeTrue();
        report.Has(DocumentHealthCodes.TablesAdjacent).Should().BeTrue();
        report.Statistics.Tables.Should().Be(3);
        report.Statistics.MaxTableNesting.Should().Be(2);
        report.WordOpen.Should().Be(WordOpenVerdict.Repair);
    }

    [Test]
    public async Task UnbalancedFields_AreReportedPerOccurrence()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.UnbalancedField());

        // begin bez end (1) + end bez begin (1: drugi end po zamknięciu pierwszego).
        report.Findings.Count(finding => finding.Code == DocumentHealthCodes.FieldUnbalanced).Should().Be(1);
        report.Findings.Where(finding => finding.Code == DocumentHealthCodes.FieldUnbalanced)
            .Should().OnlyContain(finding => finding.PdfImpact == PdfConversionImpact.Likely);
        report.Statistics.Fields.Should().Be(1);
    }

    [Test]
    public async Task StructuralNesting_ReportsEachRule()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.StructuralNesting());

        report.Has(DocumentHealthCodes.ParagraphNested).Should().BeTrue();
        report.Has(DocumentHealthCodes.RunOutsideParagraph).Should().BeTrue();
        report.Has(DocumentHealthCodes.TextOutsideRun).Should().BeTrue();
        report.Has(DocumentHealthCodes.ContentControlWithoutContent).Should().BeTrue();
        report.WordOpen.Should().Be(WordOpenVerdict.Repair);
    }

    [Test]
    public async Task Images_MissingRelationshipTypeMismatchEmptyPartAndExtent()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Images());

        report.First(DocumentHealthCodes.ImageRelationshipMissing).Description.Should().Contain("rId99");
        report.First(DocumentHealthCodes.ImageContentTypeMismatch).Description.Should().Contain("image/jpeg");
        report.Has(DocumentHealthCodes.ImagePartEmpty).Should().BeTrue();
        report.Has(DocumentHealthCodes.DrawingExtentMissing).Should().BeTrue();
        report.Has(DocumentHealthCodes.DrawingDocPrDuplicate).Should().BeTrue();
        report.Statistics.Drawings.Should().Be(4);
        report.Statistics.ImageParts.Should().Be(2);
    }

    [Test]
    public async Task AltChunkAndMailMerge_AreLikelyConversionBlockers()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.AltChunkAndMailMerge());

        report.First(DocumentHealthCodes.AltChunkPresent).PdfImpact.Should().Be(PdfConversionImpact.Likely);
        report.First(DocumentHealthCodes.MailMergeSettings).PdfImpact.Should().Be(PdfConversionImpact.Likely);
        report.Has(DocumentHealthCodes.UpdateFieldsOnOpen).Should().BeTrue();
        report.Statistics.AltChunks.Should().Be(1);
        report.WordOpen.Should().Be(WordOpenVerdict.Ok, "Word scala altChunk i obsługuje korespondencję seryjną");
        report.PdfConversion.Should().BeOneOf(PdfConversionVerdict.Likely, PdfConversionVerdict.Blocked);
    }

    [Test]
    public async Task InvalidPageSize_RequiresRepair()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.InvalidPageSize());

        report.First(DocumentHealthCodes.PageSizeOutOfRange).Description.Should().Contain("100000");
        report.WordOpen.Should().Be(WordOpenVerdict.Repair);
    }

    [Test]
    public async Task MissingNoteTargets_AndDuplicateNoteIds()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.MissingNoteTargets());

        var missing = report.Findings.Where(finding => finding.Code == DocumentHealthCodes.NoteReferenceTargetMissing).ToList();
        missing.Should().HaveCount(2);
        missing.Should().Contain(finding => finding.Description.Contains("footnoteReference"));
        missing.Should().Contain(finding => finding.Description.Contains("comments.xml"));
        report.Has(DocumentHealthCodes.NoteIdDuplicate).Should().BeTrue();
    }

    [Test]
    public async Task RepeatedFindings_AreCappedPerCodeButCounted()
    {
        var inspector = DocumentHealthTestHost.Create(new DocumentHealthOptions
        {
            EnableLibreOfficeProbe = false,
            MaxFindingsPerCode = 5
        });

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.ManyUnclosedBookmarks(12), inspector);

        var shown = report.Findings.Where(finding => finding.Code == DocumentHealthCodes.BookmarkUnclosed).ToList();
        shown.Should().HaveCount(5);
        shown[0].Description.Should().Contain("Łącznie wystąpień: 12");
        report.FindingsTruncated.Should().BeTrue();
        report.WarningCount.Should().BeGreaterThanOrEqualTo(12);
    }

    [Test]
    public async Task OpcIssues_AreCarriedOverWithImpact()
    {
        var report = await DocumentHealthTestHost.Inspect(Fixtures.StructureInspectionCorpus.MalformedRelationships());

        report.Findings.Should().Contain(finding =>
            finding.Code == StructureIssueCodes.RelationshipTargetMissing &&
            finding.Stage == HealthStage.Package &&
            finding.WordImpact == WordOpenImpact.Repair);
        report.Verdict.Should().Be(HealthVerdict.Corrupt, "błędy warstwy pakietu są traktowane jak uszkodzenie");
    }

    [Test]
    public async Task EditorImportFailure_BlocksPdfInThisApplication()
    {
        var html = Substitute.For<IDocxToHtmlConverter>();
        html.Convert(Arg.Any<Stream>()).Throws(new InvalidOperationException("Nieobsługiwana konstrukcja: w:altChunk"));
        var inspector = DocumentHealthTestHost.Create(htmlConverter: html);

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), inspector);

        var probe = report.Probe(ConversionProbeRunner.EditorImportProbe);
        probe.Status.Should().Be(ProbeStatus.Failed);
        probe.Message.Should().Contain("Nieobsługiwana konstrukcja");
        probe.Details.Should().Contain("InvalidOperationException");
        report.Has(DocumentHealthCodes.EditorImportFailed).Should().BeTrue();
        report.PdfConversion.Should().Be(PdfConversionVerdict.Blocked);
        report.PdfConversionSummary.Should().Contain("Nieobsługiwana konstrukcja");
        report.WordOpen.Should().Be(WordOpenVerdict.Ok, "awaria edytora nie mówi nic o Wordzie");
    }

    [Test]
    public async Task PdfConverterFailure_IsQuotedVerbatim()
    {
        var pdf = Substitute.For<IDocxToPdfConversionService>();
        pdf.ConvertAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("503 Service Unavailable: converter pool exhausted"));
        var inspector = DocumentHealthTestHost.Create(pdfConverter: pdf);

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), inspector);

        var probe = report.Probe(ConversionProbeRunner.PdfConversionProbe);
        probe.Status.Should().Be(ProbeStatus.Failed);
        probe.Message.Should().Contain("converter pool exhausted");
        report.First(DocumentHealthCodes.PdfConversionFailed).PdfImpact.Should().Be(PdfConversionImpact.Blocking);
        report.PdfConversion.Should().Be(PdfConversionVerdict.Blocked);
    }

    [Test]
    public async Task PdfConverterReturningGarbage_IsWarning()
    {
        var pdf = Substitute.For<IDocxToPdfConversionService>();
        pdf.ConvertAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocxToPdfConversionResult("<html>error</html>"u8.ToArray(), "x.pdf"));
        var inspector = DocumentHealthTestHost.Create(pdfConverter: pdf);

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), inspector);

        report.Probe(ConversionProbeRunner.PdfConversionProbe).Status.Should().Be(ProbeStatus.Warning);
        report.Has(DocumentHealthCodes.PdfOutputInvalid).Should().BeTrue();
    }

    // ── Etap „Aplikacja”: pokrycie przez naszą implementację ──────────────────

    [Test]
    public async Task FeatureRich_CoverageIsClassifiedByRegistry_AndIdentityRoundTripPreservesAll()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.FeatureRich());

        report.Coverage(FeatureKeys.Tables).Status.Should().Be(CoverageStatus.Supported);
        report.Coverage(FeatureKeys.Tables).RoundTrip.Should().Be(RoundTripOutcome.Preserved);
        report.Coverage(FeatureKeys.Footnotes).Status.Should().Be(CoverageStatus.Supported);
        report.Coverage(FeatureKeys.ContentControls).Status.Should().Be(CoverageStatus.Partial);
        report.Coverage(FeatureKeys.Comments).Status.Should().Be(CoverageStatus.Unsupported);
        report.Coverage(FeatureKeys.Math).Status.Should().Be(CoverageStatus.Unsupported);
        report.Coverage(FeatureKeys.HiddenText).Status.Should().Be(CoverageStatus.Unsupported);
        report.Coverage(FeatureKeys.HiddenText).CodePointer.Should().Contain("DocxToHtmlConverter");
        report.Coverage(FeatureKeys.MailMerge).Status.Should().Be(CoverageStatus.Unsupported);

        // Luki wracają też jako ustalenia etapu Aplikacja — ostrzeżenie dla utraty treści, informacja dla strat nieszkodliwych.
        var unsupported = report.Findings.Where(finding => finding.Code == DocumentHealthCodes.AppFeatureUnsupported).ToList();
        unsupported.Should().Contain(finding => finding.Title.Contains("Komentarze") && finding.Severity == StructureIssueSeverity.Warning);
        unsupported.Should().Contain(finding => finding.Title.Contains("Równania") && finding.Severity == StructureIssueSeverity.Warning);
        unsupported.Should().Contain(finding => finding.Title.Contains("Korespondencja") && finding.Severity == StructureIssueSeverity.Info);
        unsupported.Should().OnlyContain(finding => finding.Stage == HealthStage.Application && finding.AppSupport == AppSupportLevel.Unsupported);
        report.Findings.Should().Contain(finding => finding.Code == DocumentHealthCodes.AppFeaturePartial && finding.Title.Contains("Formanty"));

        report.CoverageSummary.Should().Contain("nieobsługiwanych").And.Contain("Komentarze");
        report.WordOpen.Should().Be(WordOpenVerdict.Ok, "luki naszej implementacji nie mówią nic o Wordzie");
        report.Probe(ConversionProbeRunner.RoundTripProbe).Status.Should().Be(ProbeStatus.Passed);
    }

    [Test]
    public async Task LossyRoundTrip_FlagsUnexpectedLossAndIssuesIntroducedByWriter()
    {
        var inspector = DocumentHealthTestHost.Create(
            docxWriter: DocumentHealthTestHost.WriterReturning(DocumentHealthCorpus.FeatureRichAfterLossyRoundTrip()));

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.FeatureRich(), inspector);

        var footnotes = report.Coverage(FeatureKeys.Footnotes);
        footnotes.RoundTrip.Should().Be(RoundTripOutcome.Lost);
        footnotes.RoundTripCount.Should().Be(0);
        footnotes.Status.Should().Be(CoverageStatus.UnexpectedLoss, "rejestr deklaruje pełną obsługę przypisów");
        footnotes.Note.Should().Contain("ROUND-TRIP");
        report.Coverage(FeatureKeys.Tables).RoundTrip.Should().Be(RoundTripOutcome.Preserved, "2 tabele po zapisie ≥ 1 w źródle");
        report.Coverage(FeatureKeys.Comments).Status.Should().Be(CoverageStatus.Unsupported, "utrata zadeklarowana w rejestrze nie jest niespodzianką");

        // Formant (obsługa częściowa) też zniknął z wyniku — to również nieoczekiwana strata, bo rejestr nie deklaruje braku obsługi.
        report.Coverage(FeatureKeys.ContentControls).Status.Should().Be(CoverageStatus.UnexpectedLoss);
        var losses = report.Findings.Where(finding => finding.Code == DocumentHealthCodes.AppRoundTripLoss).ToList();
        losses.Select(finding => finding.Title).Should().BeEquivalentTo(
            "Round-trip edytora gubi: Przypisy dolne", "Round-trip edytora gubi: Formanty (w:sdt)");
        losses.Should().OnlyContain(finding => finding.Severity == StructureIssueSeverity.Warning);
        losses.First(finding => finding.Title.Contains("Przypisy")).Remedy.Should().Contain("HtmlToDocxConverter");

        var introduced = report.Findings.Where(finding => finding.Code == DocumentHealthCodes.AppRoundTripIntroducedIssue).ToList();
        introduced.Select(finding => System.Text.RegularExpressions.Regex.Match(finding.Description, @"zgłasza ([A-Z_]+)").Groups[1].Value)
            .Should().BeEquivalentTo([DocumentHealthCodes.TableCellWithoutParagraph], "tylko błędy WPROWADZONE przez zapis, bez szumu");
        introduced[0].Remedy.Should().Contain("zażąda naprawy");
        report.Coverage.Should().NotContain(item => item.FeatureKey == FeatureKeys.NestedTables, "konstrukcje obecne tylko w wyniku nie są pokryciem źródła");
        report.CoverageSummary.Should().Contain("NIEOCZEKIWANYCH");
    }

    [Test]
    public async Task WithoutProbes_CoverageComesFromRegistryOnly()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.FeatureRich(), probes: false);

        report.Probes.Should().NotContain(probe => probe.Id == ConversionProbeRunner.RoundTripProbe);
        report.Coverage.Should().NotBeEmpty();
        report.Coverage.Should().OnlyContain(item => item.RoundTrip == RoundTripOutcome.NotVerified && item.RoundTripCount == null);
        report.Coverage(FeatureKeys.Comments).Status.Should().Be(CoverageStatus.Unsupported);
        report.CoverageSummary.Should().Contain("Round-trip nie został uruchomiony");
        report.Findings.Should().NotContain(finding => finding.Code == DocumentHealthCodes.AppRoundTripLoss);
    }

    [Test]
    public async Task SilentPassThroughFallback_IsDetectedByMissingOriginalStyles()
    {
        // Źródło ma styles.xml; „writer” zwraca pakiet bez tej części — tak wygląda wynik cichego fallbacku ConvertPreservingPackage.
        var inspector = DocumentHealthTestHost.Create(
            docxWriter: DocumentHealthTestHost.WriterReturning(DocumentHealthCorpus.FeatureRichAfterLossyRoundTrip()));

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), inspector);

        report.Probe(ConversionProbeRunner.RoundTripProbe).Status.Should().Be(ProbeStatus.Warning);
        var fallback = report.First(DocumentHealthCodes.RoundTripFallback);
        fallback.Description.Should().Contain("styles.xml");
        fallback.Remedy.Should().Contain("PreserveOriginalParts");
    }

    [Test]
    public async Task WriterException_IsQuotedAndDoesNotBreakVerdicts()
    {
        var writer = Substitute.For<IHtmlToDocxConverter>();
        writer.ConvertPreservingPackage(
                Arg.Any<string>(), Arg.Any<Stream?>(), Arg.Any<DocumentMetadata?>(), Arg.Any<HeaderFooterContent?>(),
                Arg.Any<HeaderFooterContent?>(), Arg.Any<PageMargins?>(), Arg.Any<PageSize?>(),
                Arg.Any<IReadOnlyList<SectionHeaderFooter>?>(), Arg.Any<IReadOnlyList<Footnote>?>(),
                Arg.Any<IReadOnlyList<Endnote>?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Throws(new InvalidOperationException("Nieznany węzeł HTML: <docx-bookmark>"));
        var inspector = DocumentHealthTestHost.Create(docxWriter: writer);

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), inspector);

        var probe = report.Probe(ConversionProbeRunner.RoundTripProbe);
        probe.Status.Should().Be(ProbeStatus.Failed);
        probe.Message.Should().Contain("docx-bookmark");
        report.First(DocumentHealthCodes.RoundTripFailed).AppSupport.Should().Be(AppSupportLevel.Unsupported);
        report.WordOpen.Should().Be(WordOpenVerdict.Ok);
        report.Coverage.Should().OnlyContain(item => item.RoundTrip == RoundTripOutcome.NotVerified);
    }

    [Test]
    public async Task Findings_CarryHowOurApplicationHandlesThem()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.AltChunkAndMailMerge());

        var altChunk = report.First(DocumentHealthCodes.AltChunkPresent);
        altChunk.AppSupport.Should().Be(AppSupportLevel.Unsupported);
        altChunk.AppNote.Should().Contain("DocxToHtmlConverter");
        report.Coverage(FeatureKeys.AltChunk).Status.Should().Be(CoverageStatus.Unsupported);
        report.First(DocumentHealthCodes.UpdateFieldsOnOpen).AppSupport.Should().Be(AppSupportLevel.Full);

        // Kody bez wpisu w mapie pozostają „nieznane” bez notatki — nie zmyślamy zachowania aplikacji.
        report.Findings.Where(finding => finding.AppSupport == AppSupportLevel.Unknown)
            .Should().OnlyContain(finding => finding.AppNote == null || finding.Stage == HealthStage.Application);
    }

    [Test]
    public void CapabilityRegistry_CoversEveryInventoryKey()
    {
        var keys = typeof(FeatureKeys).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(key => EditorCapabilityRegistry.Find(key) != null, "każda wykrywana konstrukcja musi mieć zadeklarowany poziom obsługi");
        EditorCapabilityRegistry.All.Values.Should().OnlyContain(capability => !string.IsNullOrWhiteSpace(capability.Note));
    }

    [Test]
    public async Task SchemaProbe_ListsEveryErrorGroupedByCause()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.SchemaViolations(), probes: false);

        var probe = report.Probe(ConversionProbeRunner.SchemaProbe);
        probe.Status.Should().Be(ProbeStatus.Warning);
        probe.Details.Should().NotBeNull();

        var details = probe.Details!;
        var total = int.Parse(System.Text.RegularExpressions.Regex.Match(probe.Message!, @"^(\d+) błędów").Groups[1].Value);
        total.Should().BeGreaterThanOrEqualTo(3, "trzy akapity z tym samym błędem");

        var fullList = details.Split('\n').SkipWhile(line => !line.StartsWith("Pełna lista")).Skip(1).Count(line => line.StartsWith("- ["));
        fullList.Should().Be(total, "każdy błąd ma trafić do szczegółów, nie tylko pierwsze osiem");
        details.Should().StartWith("Podsumowanie wg przyczyny");
        details.Should().MatchRegex(@"- 3× \[", "powtarzalny błąd jest zgrupowany z licznikiem");
        details.Should().Contain("word/document.xml").And.Contain("/w:document");

        var finding = report.First(DocumentHealthCodes.SchemaErrors);
        finding.Description.Should().Contain("grupach przyczyn").And.Contain("Najczęstsze: 3×");
    }

    [Test]
    public async Task RoundTripSchemaErrors_NotPresentInSource_AreAttributedToWriter()
    {
        var inspector = DocumentHealthTestHost.Create(
            docxWriter: DocumentHealthTestHost.WriterReturning(DocumentHealthCorpus.SchemaViolations()));

        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.Healthy(), inspector);

        var finding = report.First(DocumentHealthCodes.AppRoundTripSchemaErrors);
        finding.Stage.Should().Be(HealthStage.Application);
        finding.Severity.Should().Be(StructureIssueSeverity.Warning);
        finding.Description.Should().Contain("nowych (0 w źródle").And.Contain("Sch_");
        finding.Location.Should().Contain("w WYNIKU zapisu");
        finding.Remedy.Should().Contain("HtmlToDocxConverter");
        report.WordOpen.Should().Be(WordOpenVerdict.Ok);
    }

    [Test]
    public async Task RoundTripSchemaErrors_AlreadyInSource_AreNotBlamedOnWriter()
    {
        var report = await DocumentHealthTestHost.Inspect(DocumentHealthCorpus.SchemaViolations());

        report.Has(DocumentHealthCodes.SchemaErrors).Should().BeTrue();
        report.Has(DocumentHealthCodes.AppRoundTripSchemaErrors).Should().BeFalse("writer tożsamościowy nie dodał żadnego błędu");
    }

    [Test]
    public void Crc32_MatchesKnownVector()
    {
        Crc32.Compute("123456789"u8).Should().Be(0xCBF43926);
    }

    [Test]
    public void ImageSniffer_RecognizesCommonSignaturesAndEquivalence()
    {
        ImageSignatureSniffer.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]).Should().Be("image/png");
        ImageSignatureSniffer.Sniff([0xFF, 0xD8, 0xFF, 0xE1, 0x00]).Should().Be("image/jpeg");
        ImageSignatureSniffer.Sniff("<svg xmlns='http://www.w3.org/2000/svg'/>"u8).Should().Be("image/svg+xml");
        ImageSignatureSniffer.Sniff([0x00, 0x01, 0x02]).Should().BeNull();
        ImageSignatureSniffer.IsEquivalent("image/jpg", "image/jpeg").Should().BeTrue();
        ImageSignatureSniffer.IsEquivalent("image/png", "image/jpeg").Should().BeFalse();
    }
}
