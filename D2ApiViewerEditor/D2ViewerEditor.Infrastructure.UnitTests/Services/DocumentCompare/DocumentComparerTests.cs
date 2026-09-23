using System.Text;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentCompare;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentHealth;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentCompare;

[TestFixture]
public class DocumentComparerTests
{
    private const string Section = DocumentHealthCorpus.SectionA4;

    private static DocumentComparer Create(DocumentCompareOptions? options = null) => new(
        new FileContainerCheck(Options.Create(new DocumentHealthOptions())),
        new SafeOoxmlXmlLoader(Options.Create(new StructureInspectionOptions())),
        Options.Create(options ?? new DocumentCompareOptions()));

    private static DocumentComparisonReport Compare(byte[] left, byte[] right, bool ignoreRsids = true, DocumentCompareOptions? options = null) =>
        Create(options).Compare(new DocumentCompareRequest(left, "left.docx", right, "right.docx", ignoreRsids), CancellationToken.None);

    private static byte[] Doc(string body, Action<OoxmlTestPackageBuilder>? extra = null)
    {
        var builder = new OoxmlTestPackageBuilder().WithMainDocument(DocumentHealthCorpus.Document(body + Section));
        extra?.Invoke(builder);
        return builder.Build();
    }

    private static string Paragraph(string text, string runProperties = "") =>
        $"""<w:p><w:r>{runProperties}<w:t>{text}</w:t></w:r></w:p>""";

    [Test]
    public void IdenticalFiles_ReportNoDifferences()
    {
        var bytes = Doc(Paragraph("A") + Paragraph("B"));

        var report = Compare(bytes, bytes);

        report.Identical.Should().BeTrue();
        report.TotalDifferences.Should().Be(0);
        report.Parts.Should().OnlyContain(part => part.Status == ComparedPartStatus.Identical);
        report.Left.DetectedFormat.Should().Be("docx");
    }

    [Test]
    public void ChangedText_IsReportedWithValuesContextAndExcerpts()
    {
        var left = Doc(Paragraph("Pierwszy") + Paragraph("Umowa najmu") + Paragraph("Trzeci"));
        var right = Doc(Paragraph("Pierwszy") + Paragraph("Umowa sprzedaży") + Paragraph("Trzeci"));

        var report = Compare(left, right);

        report.TotalDifferences.Should().Be(1);
        var difference = report.Differences.Single();
        difference.Kind.Should().Be(DifferenceKind.TextChanged);
        difference.Category.Should().Be("Tekst");
        difference.PartPath.Should().Be("word/document.xml");
        difference.LeftValue.Should().Be("Umowa najmu");
        difference.RightValue.Should().Be("Umowa sprzedaży");
        difference.LeftPath.Should().Be("/w:document[1]/w:body[1]/w:p[2]/w:r[1]/w:t[1]");
        difference.LeftContext.Should().Be("Umowa najmu");
        difference.RightContext.Should().Be("Umowa sprzedaży");
        difference.LeftExcerpt.Should().Contain("<w:t").And.Contain("Umowa najmu");
        difference.RightExcerpt.Should().Contain("Umowa sprzedaży");
        difference.LeftLine.Should().NotBeNull();
        report.Parts.Single(part => part.Path == "word/document.xml").Status.Should().Be(ComparedPartStatus.Changed);
    }

    [Test]
    public void ChangedAttribute_IsReportedAttributeByAttribute()
    {
        var left = Doc(Paragraph("X", """<w:rPr><w:sz w:val="24"/><w:b/></w:rPr>"""));
        var right = Doc(Paragraph("X", """<w:rPr><w:sz w:val="28"/><w:i/></w:rPr>"""));

        var report = Compare(left, right);

        var size = report.Differences.Single(difference => difference.Kind == DifferenceKind.AttributeValueChanged);
        size.Name.Should().Be("w:val");
        size.LeftValue.Should().Be("24");
        size.RightValue.Should().Be("28");
        size.Category.Should().Be("Formatowanie znaku");
        size.LeftPath.Should().EndWith("/w:rPr[1]/w:sz[1]");

        report.Differences.Should().Contain(difference => difference.Kind == DifferenceKind.ElementOnlyInLeft && difference.Name == "w:b");
        report.Differences.Should().Contain(difference => difference.Kind == DifferenceKind.ElementOnlyInRight && difference.Name == "w:i");
        report.TotalDifferences.Should().Be(3);
    }

    [Test]
    public void InsertedParagraph_IsReportedAsWholeObjectWithoutNoise()
    {
        var left = Doc(Paragraph("A") + Paragraph("B") + Paragraph("C"));
        var right = Doc(Paragraph("A") + Paragraph("Nowy akapit") + Paragraph("B") + Paragraph("C"));

        var report = Compare(left, right);

        report.TotalDifferences.Should().Be(1);
        var inserted = report.Differences.Single();
        inserted.Kind.Should().Be(DifferenceKind.ElementOnlyInRight);
        inserted.Category.Should().Be("Akapit");
        inserted.Name.Should().Be("w:p");
        inserted.LeftExcerpt.Should().BeNull();
        inserted.RightExcerpt.Should().Contain("Nowy akapit");
        inserted.RightPath.Should().Be("/w:document[1]/w:body[1]/w:p[2]");
        inserted.RightContext.Should().Be("Nowy akapit");
    }

    [Test]
    public void RevisionIds_AreIgnoredByDefaultAndReportedOnDemand()
    {
        var left = Doc("""<w:p w:rsidR="00AB12CD"><w:r><w:t>A</w:t></w:r></w:p>""");
        var right = Doc("""<w:p><w:r><w:t>A</w:t></w:r></w:p>""");

        var ignoring = Compare(left, right);
        ignoring.TotalDifferences.Should().Be(0);
        ignoring.IgnoredAttributeCount.Should().BeGreaterThan(0);
        ignoring.Parts.Single(part => part.Path == "word/document.xml").Note.Should().Contain("ignorowanych");

        var literal = Compare(left, right, ignoreRsids: false);
        literal.Differences.Should().ContainSingle(difference =>
            difference.Kind == DifferenceKind.AttributeOnlyInLeft && difference.Name == "w:rsidR" && difference.LeftValue == "00AB12CD");
    }

    [Test]
    public void PartsPresentOnOneSideOnly_AreListed()
    {
        var left = Doc(Paragraph("A"));
        var right = Doc(Paragraph("A"), builder => builder
            .WithPart("word/styles.xml", """<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"/>""", StructureInspectionCorpus.StylesContentType)
            .WithRelationship("word/document.xml", "rId10", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml"));

        var report = Compare(left, right);

        report.Differences.Should().Contain(difference =>
            difference.Kind == DifferenceKind.PartOnlyInRight && difference.PartPath == "word/styles.xml" && difference.Category == "Style");
        report.Parts.Single(part => part.Path == "word/styles.xml").Status.Should().Be(ComparedPartStatus.OnlyInRight);
        report.Differences.Should().Contain(difference => difference.PartPath == "[Content_Types].xml" && difference.Kind == DifferenceKind.ElementOnlyInRight);
        report.Identical.Should().BeFalse();
    }

    [Test]
    public void BinaryPartWithDifferentBytes_IsReportedWithHashes()
    {
        var left = Doc(Paragraph("A"), builder => builder.WithBinaryPart("word/media/image1.png", OoxmlTestPackageBuilder.PngPixel()));
        var right = Doc(Paragraph("A"), builder => builder.WithBinaryPart("word/media/image1.png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]));

        var report = Compare(left, right);

        var binary = report.Differences.Single(difference => difference.Kind == DifferenceKind.BinaryPartChanged);
        binary.PartPath.Should().Be("word/media/image1.png");
        binary.Category.Should().Be("Obrazy");
        binary.LeftValue.Should().Contain("SHA-256");
        binary.LeftValue.Should().NotBe(binary.RightValue);
    }

    [Test]
    public void NonPackageInput_DoesNotThrowAndExplains()
    {
        var report = Compare(Encoding.ASCII.GetBytes("{\\rtf1 hello}"), Doc(Paragraph("A")));

        report.Identical.Should().BeFalse();
        report.Left.DetectedFormat.Should().Be("rtf");
        report.Left.Notes.Should().Contain(note => note.Contains("nie jest pakietem"));
        report.Parts.Should().BeEmpty();
        report.Right.PartCount.Should().BeGreaterThan(0);
    }

    [Test]
    public void ManyDifferences_AreCappedPerPartButCounted()
    {
        var left = Doc(string.Concat(Enumerable.Range(0, 10).Select(index => Paragraph($"L{index}"))));
        var right = Doc(string.Concat(Enumerable.Range(0, 10).Select(index => Paragraph($"R{index}"))));

        var report = Compare(left, right, options: new DocumentCompareOptions { MaxDifferencesPerPart = 3 });

        report.TotalDifferences.Should().Be(10);
        report.Differences.Should().HaveCount(3);
        report.Truncated.Should().BeTrue();
        report.Parts.Single(part => part.Path == "word/document.xml").Note.Should().Contain("Pokazano 3 z 10");
    }

    [Test]
    public void ExcerptsAreIndentedAndTruncatedToLimit()
    {
        var left = Doc(Paragraph("A"));
        var right = Doc(Paragraph("B"));

        var report = Compare(left, right, options: new DocumentCompareOptions { MaxExcerptChars = 20 });

        var difference = report.Differences.Single();
        difference.ExcerptTruncated.Should().BeTrue();
        difference.LeftExcerpt.Should().EndWith("(ucięto)");
    }

    [Test]
    public void ReorderedRelationships_AreNotDifferences()
    {
        var left = Doc(Paragraph("A"), builder => builder
            .WithRelationship("word/document.xml", "rId1", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml")
            .WithRelationship("word/document.xml", "rId2", OoxmlTestPackageBuilder.RelationshipType("settings"), "settings.xml")
            .WithRelationship("word/document.xml", "rId3", OoxmlTestPackageBuilder.RelationshipType("fontTable"), "fontTable.xml"));
        var right = Doc(Paragraph("A"), builder => builder
            .WithRelationship("word/document.xml", "rId3", OoxmlTestPackageBuilder.RelationshipType("fontTable"), "fontTable.xml")
            .WithRelationship("word/document.xml", "rId1", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml")
            .WithRelationship("word/document.xml", "rId2", OoxmlTestPackageBuilder.RelationshipType("settings"), "settings.xml"));

        var report = Compare(left, right);

        report.Differences.Where(difference => difference.PartPath == "word/_rels/document.xml.rels").Should().BeEmpty();
    }

    [Test]
    public void RelationshipWithSameIdAndNewTarget_IsSingleAttributeChange()
    {
        var left = Doc(Paragraph("A"), builder => builder
            .WithRelationship("word/document.xml", "rId1", OoxmlTestPackageBuilder.RelationshipType("image"), "media/image1.png")
            .WithRelationship("word/document.xml", "rId2", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml"));
        var right = Doc(Paragraph("A"), builder => builder
            .WithRelationship("word/document.xml", "rId2", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml")
            .WithRelationship("word/document.xml", "rId1", OoxmlTestPackageBuilder.RelationshipType("image"), "media/image2.png"));

        var report = Compare(left, right);

        var relationshipDifferences = report.Differences.Where(difference => difference.PartPath == "word/_rels/document.xml.rels").ToList();
        relationshipDifferences.Should().ContainSingle();
        relationshipDifferences[0].Kind.Should().Be(DifferenceKind.AttributeValueChanged);
        relationshipDifferences[0].Name.Should().Be("Target");
        relationshipDifferences[0].LeftValue.Should().Be("media/image1.png");
        relationshipDifferences[0].RightValue.Should().Be("media/image2.png");
    }

    [Test]
    public void RenumberedRelationship_IsPairedByTypeAndTarget()
    {
        var left = Doc(Paragraph("A"), builder => builder
            .WithRelationship("word/document.xml", "rId1", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml"));
        var right = Doc(Paragraph("A"), builder => builder
            .WithRelationship("word/document.xml", "rId7", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml"));

        var report = Compare(left, right);

        var relationshipDifferences = report.Differences.Where(difference => difference.PartPath == "word/_rels/document.xml.rels").ToList();
        relationshipDifferences.Should().ContainSingle(difference =>
            difference.Kind == DifferenceKind.AttributeValueChanged && difference.Name == "Id" && difference.LeftValue == "rId1" && difference.RightValue == "rId7");
    }

    [Test]
    public void MovedParagraph_IsReportedAsMovedNotRemovedAndAdded()
    {
        var left = Doc(Paragraph("A") + Paragraph("B") + Paragraph("C") + Paragraph("D"));
        var right = Doc(Paragraph("A") + Paragraph("C") + Paragraph("D") + Paragraph("B"));

        var report = Compare(left, right);

        report.TotalDifferences.Should().Be(1);
        var moved = report.Differences.Single();
        moved.Kind.Should().Be(DifferenceKind.ElementMoved);
        moved.LeftPath.Should().Be("/w:document[1]/w:body[1]/w:p[2]");
        moved.RightPath.Should().Be("/w:document[1]/w:body[1]/w:p[4]");
        moved.LeftContext.Should().Be("B");
        moved.LeftExcerpt.Should().NotBeNull();
        moved.RightExcerpt.Should().NotBeNull();
    }

    [Test]
    public void ParagraphsWithParaId_ArePairedByIdentityDespiteReorder()
    {
        const string ns = """xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" """;
        var left = new OoxmlTestPackageBuilder().WithMainDocument(DocumentHealthCorpus.Document(
            """<w:p w14:paraId="1A"><w:r><w:t>Pierwszy</w:t></w:r></w:p><w:p w14:paraId="2B"><w:r><w:t>Drugi</w:t></w:r></w:p>""" + Section, ns)).Build();
        var right = new OoxmlTestPackageBuilder().WithMainDocument(DocumentHealthCorpus.Document(
            """<w:p w14:paraId="2B"><w:r><w:t>Drugi zmieniony</w:t></w:r></w:p><w:p w14:paraId="1A"><w:r><w:t>Pierwszy</w:t></w:r></w:p>""" + Section, ns)).Build();

        var report = Compare(left, right);

        report.Differences.Where(difference => difference.PartPath == "word/document.xml").Should().ContainSingle(difference =>
            difference.Kind == DifferenceKind.TextChanged && difference.LeftValue == "Drugi" && difference.RightValue == "Drugi zmieniony");
    }

    [Test]
    public void SequenceAligner_LcsAndGreedyAgreeOnAnchors()
    {
        var left = new[] { "a", "b", "c", "d", "e" };
        var right = new[] { "a", "x", "c", "d", "y", "e" };

        var lcs = new SequenceAligner(lcsCellLimit: 1_000, greedyWindow: 10).Align(left, right);
        var greedy = new SequenceAligner(lcsCellLimit: 1, greedyWindow: 10).Align(left, right);

        lcs.Should().Equal((0, 0), (2, 2), (3, 3), (4, 5));
        greedy.Should().Equal((0, 0), (2, 2), (3, 3), (4, 5));
    }
}
