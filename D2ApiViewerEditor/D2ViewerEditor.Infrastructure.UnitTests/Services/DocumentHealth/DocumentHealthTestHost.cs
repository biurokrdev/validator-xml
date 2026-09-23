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

internal static class DocumentHealthTestHost
{
    public static DocumentHealthInspector Create(
        DocumentHealthOptions? options = null,
        IDocxToHtmlConverter? htmlConverter = null,
        IDocxToPdfConversionService? pdfConverter = null)
    {
        var healthOptions = Options.Create(options ?? new DocumentHealthOptions { EnableLibreOfficeProbe = false });
        var structureOptions = Options.Create(new StructureInspectionOptions());
        var loader = new SafeOoxmlXmlLoader(structureOptions);
        var uploadSecurity = new FileUploadSecurityService(Options.Create(new UploadSecurityOptions()), new NoOpFileScanner());

        var html = htmlConverter ?? DefaultHtmlConverter();
        var pdf = pdfConverter ?? new MockDocxToPdfConversionService(NullLogger<MockDocxToPdfConversionService>.Instance);

        var probes = new ConversionProbeRunner(
            new OpenXmlSchemaValidatorRunner(structureOptions),
            uploadSecurity,
            html,
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

    public static Task<DocumentHealthReport> Inspect(byte[] package, DocumentHealthInspector? inspector = null, bool probes = true) =>
        (inspector ?? Create()).InspectAsync(package, "test.docx", probes, CancellationToken.None);

    public static bool Has(this DocumentHealthReport report, string code) =>
        report.Findings.Any(finding => finding.Code == code);

    public static HealthFinding First(this DocumentHealthReport report, string code) =>
        report.Findings.First(finding => finding.Code == code);

    public static ConversionProbeResult Probe(this DocumentHealthReport report, string id) =>
        report.Probes.Single(probe => probe.Id == id);
}
