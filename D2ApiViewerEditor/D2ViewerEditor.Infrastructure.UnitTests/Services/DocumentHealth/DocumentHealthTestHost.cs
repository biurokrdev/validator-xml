using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using D2ViewerEditor.Infrastructure.Services.PdfConversion;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentHealth;

/// <summary>
/// Składa inspektor „Kondycja dokumentu” z prawdziwych etapów (kontener, OPC, XML, struktura,
/// bramka uploadu, SDK, atrapa konwertera PDF) i podstawianego konwertera DOCX→HTML. LibreOffice
/// jest wyłączony — testy nie mogą zależeć od binarki spoza repo.
/// </summary>
internal static class DocumentHealthTestHost
{
    public static DocumentHealthInspector Create(
        DocumentHealthOptions? options = null,
        IDocxToHtmlConverter? htmlConverter = null,
        IDocxToPdfConversionService? pdfConverter = null,
        IHtmlToDocxConverter? docxWriter = null)
    {
        var healthOptions = Options.Create(options ?? new DocumentHealthOptions { EnableLibreOfficeProbe = false });
        var structureOptions = Options.Create(new StructureInspectionOptions());
        var loader = new SafeOoxmlXmlLoader(structureOptions);
        var uploadSecurity = new FileUploadSecurityService(Options.Create(new UploadSecurityOptions()), new NoOpFileScanner());

        var html = htmlConverter ?? DefaultHtmlConverter();
        var writer = docxWriter ?? IdentityWriter();
        var pdf = pdfConverter ?? new MockDocxToPdfConversionService(NullLogger<MockDocxToPdfConversionService>.Instance);

        var probes = new ConversionProbeRunner(
            new OpenXmlSchemaValidatorRunner(structureOptions),
            uploadSecurity,
            html,
            writer,
            pdf,
            new LibreOfficeConverterProbe(healthOptions, NullLogger<LibreOfficeConverterProbe>.Instance),
            healthOptions,
            NullLogger<ConversionProbeRunner>.Instance);

        return new DocumentHealthInspector(
            new FileContainerCheck(healthOptions),
            new PackageHealthCheck(new OpcPackageAnalyzer(loader)),
            new XmlPartsHealthCheck(loader),
            new WordStructureHealthCheck(healthOptions),
            probes,
            healthOptions,
            NullLogger<DocumentHealthInspector>.Instance);
    }

    public static IDocxToHtmlConverter DefaultHtmlConverter()
    {
        var converter = Substitute.For<IDocxToHtmlConverter>();
        converter.Convert(Arg.Any<Stream>()).Returns(new DocumentContent { Html = "<p>ok</p>" });
        return converter;
    }

    /// <summary>
    /// Writer „tożsamościowy”: zwraca ORYGINALNY pakiet przekazany jako strumień pass-through —
    /// round-trip niczego nie gubi, więc testy reguł nie zależą od prawdziwego HtmlToDocxConverter.
    /// </summary>
    public static IHtmlToDocxConverter IdentityWriter()
    {
        var writer = Substitute.For<IHtmlToDocxConverter>();
        writer.ConvertPreservingPackage(
                Arg.Any<string>(), Arg.Any<Stream?>(), Arg.Any<DocumentMetadata?>(), Arg.Any<HeaderFooterContent?>(),
                Arg.Any<HeaderFooterContent?>(), Arg.Any<PageMargins?>(), Arg.Any<PageSize?>(),
                Arg.Any<IReadOnlyList<SectionHeaderFooter>?>(), Arg.Any<IReadOnlyList<Footnote>?>(),
                Arg.Any<IReadOnlyList<Endnote>?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(call =>
            {
                var original = call.Arg<Stream?>();

                if (original is null)
                {
                    return [];
                }

                using var buffer = new MemoryStream();
                original.CopyTo(buffer);
                return buffer.ToArray();
            });
        return writer;
    }

    /// <summary>Writer zwracający zawsze ten sam, z góry ustalony pakiet — do testów „co round-trip zgubił”.</summary>
    public static IHtmlToDocxConverter WriterReturning(byte[] package)
    {
        var writer = Substitute.For<IHtmlToDocxConverter>();
        writer.ConvertPreservingPackage(
                Arg.Any<string>(), Arg.Any<Stream?>(), Arg.Any<DocumentMetadata?>(), Arg.Any<HeaderFooterContent?>(),
                Arg.Any<HeaderFooterContent?>(), Arg.Any<PageMargins?>(), Arg.Any<PageSize?>(),
                Arg.Any<IReadOnlyList<SectionHeaderFooter>?>(), Arg.Any<IReadOnlyList<Footnote>?>(),
                Arg.Any<IReadOnlyList<Endnote>?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(package);
        return writer;
    }

    public static ImplementationCoverageItem Coverage(this DocumentHealthReport report, string featureKey) =>
        report.Coverage.Single(item => item.FeatureKey == featureKey);

    public static Task<DocumentHealthReport> Inspect(byte[] package, DocumentHealthInspector? inspector = null, bool probes = true) =>
        (inspector ?? Create()).InspectAsync(package, "test.docx", probes, CancellationToken.None);

    public static bool Has(this DocumentHealthReport report, string code) =>
        report.Findings.Any(finding => finding.Code == code);

    public static HealthFinding First(this DocumentHealthReport report, string code) =>
        report.Findings.First(finding => finding.Code == code);

    public static ConversionProbeResult Probe(this DocumentHealthReport report, string id) =>
        report.Probes.Single(probe => probe.Id == id);
}
