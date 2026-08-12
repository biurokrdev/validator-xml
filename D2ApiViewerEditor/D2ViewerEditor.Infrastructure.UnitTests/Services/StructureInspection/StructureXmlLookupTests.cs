using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.StructureInspection;

[TestFixture]
public class StructureXmlLookupTests
{
    private DocumentStructureInspector _inspector = null!;
    private byte[] _package = null!;

    [SetUp]
    public void SetUp()
    {
        _inspector = StructureInspectorTestHost.Create();
        _package = StructureInspectionCorpus.Drawings();
    }

    [Test]
    public void ReadElementXml_ReturnsExactlyTheRequestedNode()
    {
        var analysis = StructureInspectorTestHost.Analyze(_package, _inspector);
        var anchor = analysis.Element("anchor");

        var fragment = _inspector.ReadElementXml(_package, anchor.PartPath, anchor.NodePath, CancellationToken.None);

        fragment.Should().NotBeNull();
        fragment!.Xml.Should().StartWith("<wp:anchor");
        fragment.Xml.Should().EndWith("</wp:anchor>");
        fragment.Xml.Should().Contain("behindDoc=\"1\"");
        fragment.Xml.Should().Contain("-635000");
        fragment.Xml.Should().NotContain("<v:shape");
        fragment.Xml.Should().NotContain("Nowszy wariant");
    }

    [Test]
    public void ReadElementXml_KeepsNamespaceDeclarationsInScopeForTheFragment()
    {
        var analysis = StructureInspectorTestHost.Analyze(_package, _inspector);
        var inline = analysis.Element("inline");

        var fragment = _inspector.ReadElementXml(_package, inline.PartPath, inline.NodePath, CancellationToken.None);

        fragment!.Xml.Should().Contain("xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"");
        fragment.Xml.Should().Contain("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"");
    }

    [Test]
    public void ReadElementXml_DistinguishesSiblingsWithTheSameName()
    {
        var package = StructureInspectionCorpus.Normal();
        var analysis = StructureInspectorTestHost.Analyze(package, _inspector);
        var paragraphs = analysis.Elements("p");

        var first = _inspector.ReadElementXml(package, paragraphs[0].PartPath, paragraphs[0].NodePath, CancellationToken.None);
        var second = _inspector.ReadElementXml(package, paragraphs[1].PartPath, paragraphs[1].NodePath, CancellationToken.None);

        first!.Xml.Should().Contain("Redundantne 12 pt");
        first.Xml.Should().NotContain("Realna zmiana");
        second!.Xml.Should().Contain("Realna zmiana");
        second.Xml.Should().NotContain("Redundantne 12 pt");
    }

    [Test]
    public void ReadElementXml_ResolvesElementsInsideHeaderAndFooterParts()
    {
        var package = StructureInspectionCorpus.SectionsWithHeaders();
        var analysis = StructureInspectorTestHost.Analyze(package, _inspector);
        var headerParagraph = analysis.Elements
            .First(element => element.PartPath == "word/header1.xml" && element.LocalName == "p");

        var fragment = _inspector.ReadElementXml(package, headerParagraph.PartPath, headerParagraph.NodePath, CancellationToken.None);

        fragment!.Xml.Should().Contain("Nagłówek");
    }

    [Test]
    public void FindElementLine_ReturnsLineInsideThePartSource()
    {
        var analysis = StructureInspectorTestHost.Analyze(_package, _inspector);
        var anchor = analysis.Element("anchor");

        var line = _inspector.FindElementLine(_package, anchor.PartPath, anchor.NodePath, CancellationToken.None);

        line.Should().NotBeNull();
        line.Should().BeGreaterThan(0);
    }

    [Test]
    public void ReadPartXml_ReturnsRawZipEntryContent()
    {
        var xml = _inspector.ReadPartXml(_package, "word/document.xml", CancellationToken.None);

        xml.Should().NotBeNull();
        xml!.Should().StartWith("<?xml version=\"1.0\"");
        xml.Should().Contain("<w:document");
    }

    [Test]
    public void ReadPartXml_ReturnsNullForPathOutsideThePackage()
    {
        _inspector.ReadPartXml(_package, "word/nieistnieje.xml", CancellationToken.None).Should().BeNull();
    }

    [Test]
    public void ValidateSchema_RunsForTheSelectedOfficeProfile()
    {
        var package = StructureInspectionCorpus.Normal();
        var analysis = StructureInspectorTestHost.Analyze(package, _inspector);

        var issues = _inspector.ValidateSchema(package, "Office2013", analysis.Elements, CancellationToken.None);

        issues.Should().OnlyContain(issue => issue.TargetVersion == "Office2013");
        _inspector.GetSupportedSchemaTargets().Should().Contain("Microsoft365");
    }

    [Test]
    public void ValidateSchema_RejectsUnknownOfficeProfile()
    {
        var analysis = StructureInspectorTestHost.Analyze(_package, _inspector);

        var act = () => _inspector.ValidateSchema(_package, "Office1997", analysis.Elements, CancellationToken.None);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Analyze_MapsSchemaIssuesOntoIndexedElementsWhenPossible()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Tables(), _inspector);

        analysis.SchemaIssueCount.Should().BeGreaterThan(0);
        analysis.SchemaIssues.Should().Contain(issue => issue.ElementId != null);
        analysis.SchemaIssues.Should().OnlyContain(issue => issue.TargetVersion == "Microsoft365");
    }

    [Test]
    public void Analyze_StopsIndexingAtTheElementLimitWithoutFailing()
    {
        var limited = StructureInspectorTestHost.Create(new StructureInspectionOptions { MaxElements = 20 });

        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings(), limited);

        analysis.ElementsTruncated.Should().BeTrue();
        analysis.Elements.Should().HaveCount(20);
    }

    [Test]
    public void Analyze_ProducesUniqueStableElementIdentifiers()
    {
        var analysis = StructureInspectorTestHost.Analyze(_package, _inspector);
        var repeated = StructureInspectorTestHost.Analyze(_package, _inspector);

        analysis.Elements.Select(element => element.Id).Should().OnlyHaveUniqueItems();
        analysis.Elements.Select(element => element.Id)
            .Should().Equal(repeated.Elements.Select(element => element.Id));
    }
}
