using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentCompare;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentCompare;

/// <summary>
/// Writer zapisuje obrazy pod własnymi nazwami (<c>word/media/image1.png</c> → <c>media/image.png</c>); identyczne bajty pod
/// inną ścieżką mają być JEDNĄ różnicą „część pod inną ścieżką”, a nie parą „zginął obraz” + „nowy obraz” — inaczej raport
/// obwinia oryginał o utratę, której nie było.
/// </summary>
[TestFixture]
public class DocumentComparerRenamedPartTests
{
    private static DocumentComparer CreateComparer()
    {
        var structureOptions = Options.Create(new StructureInspectionOptions());
        return new DocumentComparer(
            new FileContainerCheck(Options.Create(new DocumentHealthOptions())),
            new SafeOoxmlXmlLoader(structureOptions),
            Options.Create(new DocumentCompareOptions()));
    }

    private const string Body = """<w:p><w:r><w:t>x</w:t></w:r></w:p>""";

    [Test]
    public void IdenticalBinaryUnderDifferentPath_IsReportedAsRenamed_NotLostAndAdded()
    {
        var image = OoxmlTestPackageBuilder.PngPixel();
        var left = new OoxmlTestPackageBuilder()
            .WithMainDocument(StructureInspectionCorpus.Document(Body))
            .WithBinaryPart("word/media/image1.png", image)
            .Build();
        var right = new OoxmlTestPackageBuilder()
            .WithMainDocument(StructureInspectionCorpus.Document(Body))
            .WithBinaryPart("media/image.png", image)
            .Build();

        var report = CreateComparer().Compare(new DocumentCompareRequest(left, "l.docx", right, "r.docx", true, true), CancellationToken.None);

        var renamed = report.Differences.Should().ContainSingle(d => d.Kind == DifferenceKind.PartRenamed).Subject;
        renamed.PartPath.Should().Be("word/media/image1.png");
        renamed.RightValue.Should().Be("media/image.png");
        renamed.Analysis!.Cause.Should().Be(DifferenceCause.WriterNormalization);
        renamed.Analysis.Impact.Should().Be(DifferenceImpact.None);
        report.Differences.Should().NotContain(d => d.Kind == DifferenceKind.PartOnlyInLeft || d.Kind == DifferenceKind.PartOnlyInRight);
        report.Parts.Should().Contain(p => p.Path == "media/image.png" && p.Note != null && p.Note.Contains("word/media/image1.png"));
        report.CountsByKind.Should().ContainKey(nameof(DifferenceKind.PartRenamed));
    }

    [Test]
    public void DifferentBytesUnderDifferentPaths_StayLostAndAdded()
    {
        var left = new OoxmlTestPackageBuilder()
            .WithMainDocument(StructureInspectionCorpus.Document(Body))
            .WithBinaryPart("word/media/image1.png", OoxmlTestPackageBuilder.PngPixel())
            .Build();
        var right = new OoxmlTestPackageBuilder()
            .WithMainDocument(StructureInspectionCorpus.Document(Body))
            .WithBinaryPart("media/image.png", [1, 2, 3, 4, 5])
            .Build();

        var report = CreateComparer().Compare(new DocumentCompareRequest(left, "l.docx", right, "r.docx", true, true), CancellationToken.None);

        report.Differences.Should().Contain(d => d.Kind == DifferenceKind.PartOnlyInLeft && d.PartPath == "word/media/image1.png");
        report.Differences.Should().Contain(d => d.Kind == DifferenceKind.PartOnlyInRight && d.PartPath == "media/image.png");
        report.Differences.Should().NotContain(d => d.Kind == DifferenceKind.PartRenamed);
    }
}
