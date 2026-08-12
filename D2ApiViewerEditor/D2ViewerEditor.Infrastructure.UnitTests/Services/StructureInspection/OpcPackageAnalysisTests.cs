using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.StructureInspection;

[TestFixture]
public class OpcPackageAnalysisTests
{
    [Test]
    public void Analyze_ResolvesMainDocumentPartFromRootRelationship()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.CustomMainDocumentPath());

        analysis.MainDocumentPartPath.Should().Be("content/main-document.xml");
        analysis.Element("document").PartPath.Should().Be("content/main-document.xml");
        analysis.PackageIssues.Should().NotContain(issue => issue.Code == StructureIssueCodes.MainDocumentFallback);
    }

    [Test]
    public void Analyze_IndexesMainDocumentPartFirstRegardlessOfItsName()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.CustomMainDocumentPath());

        analysis.Elements[0].PartPath.Should().Be(analysis.MainDocumentPartPath);
        analysis.Elements[0].Depth.Should().Be(0);
    }

    [Test]
    public void Analyze_FallsBackToContentTypeWhenOfficeDocumentRelationshipIsMissing()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.MissingMainDocumentRelationship());

        analysis.MainDocumentPartPath.Should().Be("word/document.xml");
        analysis.HasPackageIssue(StructureIssueCodes.MainDocumentRelationshipMissing).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.MainDocumentFallback).Should().BeTrue();
    }

    [Test]
    public void Analyze_ReportsContentTypeProblems()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.MalformedContentTypes());

        analysis.HasPackageIssue(StructureIssueCodes.ContentTypeDefaultDuplicate).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.ContentTypeOverrideTargetNotFound).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.ContentTypeOverrideInvalid).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.ContentTypeMissing).Should().BeTrue();
    }

    [Test]
    public void Analyze_ReportsRelationshipProblemsAndUnreachableParts()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.MalformedRelationships());

        analysis.HasPackageIssue(StructureIssueCodes.RelationshipIdDuplicate).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.RelationshipTargetEscapesPackage).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.RelationshipTargetMissing).Should().BeTrue();
        analysis.HasPackageIssue(StructureIssueCodes.RelationshipExternal).Should().BeTrue();
        analysis.PackageIssues.Should().Contain(issue =>
            issue.Code == StructureIssueCodes.OrphanedPart && issue.Description.Contains("osierocony.png"));
    }

    [Test]
    public void Analyze_MarksElementRelationshipWithMissingTarget()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.MalformedRelationships());
        var inline = analysis.Element("inline");

        inline.HasIssue(StructureIssueCodes.ElementRelationshipTargetMissing).Should().BeTrue();
        inline.Relationships.Should().Contain(relationship =>
            relationship.Id == "rId70" && relationship.Status == StructureRelationshipStatus.TargetMissing);
    }

    [Test]
    public void Analyze_MarksExternalRelationshipOnTheReferencingElement()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.MalformedRelationships());
        var hyperlink = analysis.Element("hyperlink");

        hyperlink.Relationships.Should().ContainSingle(relationship =>
            relationship.Id == "rId72" && relationship.Status == StructureRelationshipStatus.External);
        hyperlink.HasIssue(StructureIssueCodes.ElementRelationshipExternal).Should().BeTrue();
    }

    [Test]
    public void Analyze_RecordsPackageEntriesIncludingBinaryOnes()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());

        analysis.Entries.Should().Contain(entry => entry.Path == "word/media/image1.png" && !entry.IsXml);
        analysis.Entries.Should().Contain(entry => entry.Path == "word/document.xml" && entry.IsXml);
    }

    [Test]
    public void Analyze_RejectsPackageThatIsNotZip()
    {
        var act = () => StructureInspectorTestHost.Analyze("to nie jest DOCX"u8.ToArray());

        act.Should().Throw<InvalidOoxmlPackageException>();
    }

    [Test]
    public void Analyze_RejectsPackageWithoutRootRelationships()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("[Content_Types].xml").Open();
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""");
            stream.Write(bytes, 0, bytes.Length);
        }

        var act = () => StructureInspectorTestHost.Analyze(buffer.ToArray());

        act.Should().Throw<InvalidOoxmlPackageException>().WithMessage("*_rels/.rels*");
    }
}
