using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.StructureInspection;

[TestFixture]
public class StructureSemanticAnalysisTests
{
    [Test]
    public void EffectiveFormatting_ResolvesValueAndItsSourceThroughStyleChain()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Normal());
        var runs = analysis.Elements("r");
        var plainRun = runs.Single(run => run.Preview == "Bez stylu");

        plainRun.Properties.Should().Contain(property =>
            property.Name == "Efektywne: rozmiar czcionki" &&
            property.Value == "11 pt" &&
            property.Source == PropertySources.Style);
        plainRun.Properties.Should().Contain(property =>
            property.Name == "Rozwiązana czcionka" && property.Value == "Calibri");
    }

    [Test]
    public void EffectiveFormatting_FlagsDirectValueIdenticalToInheritedOne()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Normal());
        var redundantRun = analysis.Elements("r").Single(run => run.Preview == "Redundantne 12 pt");

        redundantRun.HasIssue(StructureIssueCodes.RedundantDirectFormatting).Should().BeTrue();
        redundantRun.Issues
            .Single(issue => issue.Code == StructureIssueCodes.RedundantDirectFormatting)
            .Severity.Should().Be(StructureIssueSeverity.Warning);
    }

    [Test]
    public void EffectiveFormatting_DoesNotFlagDirectValueThatChangesTheInheritedOne()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Normal());
        var realChange = analysis.Elements("r").Single(run => run.Preview == "Realna zmiana");

        realChange.HasIssue(StructureIssueCodes.RedundantDirectFormatting).Should().BeFalse();
        realChange.Properties.Should().Contain(property =>
            property.Name == "Efektywne: rozmiar czcionki" &&
            property.Value == "20 pt" &&
            property.Source == PropertySources.Direct);
    }

    [Test]
    public void EffectiveFormatting_ReportsMissingBaseStyle()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Normal());

        analysis.Elements.Should().Contain(element =>
            element.Issues.Any(issue => issue.Code == StructureIssueCodes.StyleBasedOnNotFound));
    }

    [Test]
    public void Strict_IsRecognizedByNamespaceNotByPrefix()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.StrictOoxml());

        analysis.HasPackageIssue(StructureIssueCodes.StrictOoxml).Should().BeTrue();
        analysis.Element("p").Category.Should().Be(ElementCategories.Paragraph);
        analysis.Element("tbl").Category.Should().Be(ElementCategories.Table);
        analysis.Element("rPr").HasIssue(StructureIssueCodes.HiddenText).Should().BeTrue();
        analysis.Element("pPr").HasIssue(StructureIssueCodes.NegativeIndentation).Should().BeTrue();
    }

    [Test]
    public void Numbering_ResolvesLevelDefinitionAndStartOverride()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Numbering());
        var paragraph = analysis.Elements("p").First();

        paragraph.PropertyValue("Format listy").Should().Be("decimal");
        paragraph.PropertyValue("Wzorzec etykiety").Should().Be("%1.%2.");
        paragraph.PropertyValue("Wartość początkowa").Should().Be("5");
        paragraph.PropertyValue("Czcionka punktora").Should().Be("Symbol");
    }

    [Test]
    public void Numbering_ReportsParagraphPointingToMissingInstance()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Numbering());
        var orphan = analysis.Elements("p").Single(paragraph => paragraph.Preview == "Sierota");

        orphan.HasIssue(StructureIssueCodes.NumberingInstanceNotFound).Should().BeTrue();
    }

    [Test]
    public void Tables_ReportGridMismatchMergeAndNonSchemaCellProperties()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Tables());
        var outerTable = analysis.Elements("tbl").First();
        var firstCell = analysis.Elements("tc").First();

        outerTable.HasIssue(StructureIssueCodes.TableFloating).Should().BeTrue();
        outerTable.PropertyValue("Kolumny siatki").Should().Be("2");
        firstCell.HasIssue(StructureIssueCodes.TableCellDuplicateProperties).Should().BeTrue();
        firstCell.HasIssue(StructureIssueCodes.TableCellHorizontalMerge).Should().BeTrue();
        analysis.Elements("tbl").Last().HasIssue(StructureIssueCodes.TableNested).Should().BeTrue();
        analysis.Elements.Should().Contain(element =>
            element.Issues.Any(issue => issue.Code == StructureIssueCodes.TableGridMismatch));
    }

    [Test]
    public void Tables_ReportVerticalMergeContinuationWithoutRestart()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Tables());

        analysis.Elements.Should().Contain(element =>
            element.Issues.Any(issue => issue.Code == StructureIssueCodes.TableVerticalMergeWithoutRestart));
    }

    [Test]
    public void Drawings_ReportAnchorPositioningWrapTransformAndCrop()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());
        var anchor = analysis.Element("anchor");

        anchor.HasIssue(StructureIssueCodes.DrawingBehindDocument).Should().BeTrue();
        anchor.HasIssue(StructureIssueCodes.DrawingOutsideCellLayout).Should().BeTrue();
        anchor.HasIssue(StructureIssueCodes.DrawingComplexWrap).Should().BeTrue();
        anchor.HasIssue(StructureIssueCodes.DrawingEffectExtent).Should().BeTrue();
        anchor.HasIssue(StructureIssueCodes.DrawingTransform).Should().BeTrue();
        anchor.HasIssue(StructureIssueCodes.ImageCropped).Should().BeTrue();
        anchor.PropertyValue("Poziomo: odniesienie").Should().Be("page");
        anchor.Properties.Should().Contain(property =>
            property.Name == "Wielokąt oblewania" && property.Value!.Contains("punkty=2"));
    }

    [Test]
    public void Drawings_ReportNegativeOffsetOnTheOffsetElement()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());

        analysis.Elements("posOffset")
            .Count(offset => offset.HasIssue(StructureIssueCodes.DrawingNegativeOffset))
            .Should().Be(1);
    }

    [Test]
    public void Drawings_ClassifyLegacyAndExtensionContent()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());

        analysis.Element("shape").HasIssue(StructureIssueCodes.LegacyVmlShape).Should().BeTrue();
        analysis.Element("pict").HasIssue(StructureIssueCodes.LegacyPictureContainer).Should().BeTrue();
        analysis.Element("object").HasIssue(StructureIssueCodes.EmbeddedObject).Should().BeTrue();
        analysis.Element("svgBlip").Category.Should().Be(ElementCategories.SvgImage);
        analysis.Element("svgBlip").HasIssue(StructureIssueCodes.DrawingSvg).Should().BeTrue();
    }

    [Test]
    public void MarkupCompatibility_DescribesChoiceBranchesAndMissingFallback()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());
        var alternateContent = analysis.Element("AlternateContent");

        alternateContent.HasIssue(StructureIssueCodes.AlternateContent).Should().BeTrue();
        alternateContent.HasIssue(StructureIssueCodes.AlternateContentNoFallback).Should().BeTrue();
        alternateContent.PropertyValue("Liczba gałęzi Choice").Should().Be("1");
        alternateContent.Properties.Should().Contain(property =>
            property.Name == "Choice 1" && property.Value!.Contains("wordprocessingDrawing"));
    }

    [Test]
    public void MarkupCompatibility_ResolvesIgnorablePrefixesOnPartRoot()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());
        var documentRoot = analysis.Element("document");

        documentRoot.Properties.Should().Contain(property =>
            property.Name == "mc:Ignorable" && property.Value!.Contains("wp14="));
        documentRoot.HasIssue(StructureIssueCodes.CompatibilityPrefixUnresolved).Should().BeFalse();
    }

    [Test]
    public void Sections_ResolveDirectAndInheritedHeaderFooterBindings()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.SectionsWithHeaders());

        analysis.Sections.Should().HaveCount(2);

        var firstSection = analysis.Sections[0];
        var secondSection = analysis.Sections[1];

        firstSection.FirstPageDifferent.Should().BeTrue();
        firstSection.EvenAndOddHeaders.Should().BeTrue();
        Binding(firstSection, "Header", "Default").Source.Should().Be("Direct");
        Binding(firstSection, "Header", "Default").PartPath.Should().Be("word/header1.xml");
        Binding(firstSection, "Footer", "First").PartPath.Should().Be("word/footer2.xml");

        Binding(secondSection, "Header", "Default").Source.Should().Be("Inherited");
        Binding(secondSection, "Header", "Default").PartPath.Should().Be("word/header1.xml");
        Binding(secondSection, "Footer", "Default").Source.Should().Be("Direct");
        Binding(secondSection, "Footer", "Default").PartPath.Should().Be("word/footer3.xml");
        Binding(secondSection, "Header", "Even").Source.Should().Be("Missing");
    }

    [Test]
    public void Sections_ReportUnusedHeaderFooterPart()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.SectionsWithHeaders());
        var orphanRoot = analysis.Elements.First(element =>
            element.PartPath == "word/footer9.xml" && element.Depth == 0);

        orphanRoot.HasIssue(StructureIssueCodes.HeaderFooterPartOrphaned).Should().BeTrue();
    }

    [Test]
    public void Sections_DescribeLayoutOfEverySection()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.SectionsWithHeaders());
        var firstSectionProperties = analysis.Elements("sectPr").First();

        firstSectionProperties.PropertyValue("Numer sekcji").Should().Be("1");
        firstSectionProperties.HasIssue(StructureIssueCodes.SectionMultiColumn).Should().BeTrue();
        firstSectionProperties.Properties.Should().Contain(property => property.Name == "Rozmiar strony");
    }

    [Test]
    public void HeaderFooterParts_AreAnalyzedByTheSameEngineAsTheMainDocument()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.SectionsWithHeaders());
        var footerField = analysis.Elements
            .Where(element => element.PartPath == "word/footer1.xml" && element.LocalName == "fldChar")
            .ToArray();

        footerField.Should().NotBeEmpty();
        footerField[0].PropertyValue("Typ pola").Should().Be("PAGE");
    }

    [Test]
    public void Fields_AssembleCompleteFieldAndReportBrokenOnes()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Fields());
        var pageField = analysis.Elements("fldChar").First();

        pageField.PropertyValue("Typ pola").Should().Be("PAGE");
        pageField.PropertyValue("Wynik pola").Should().Be("7");
        analysis.Element("fldSimple").PropertyValue("Typ pola").Should().Be("NUMPAGES");
        analysis.Elements.Should().Contain(element =>
            element.Issues.Any(issue => issue.Code == StructureIssueCodes.FieldNotClosed));
        analysis.Elements.Should().Contain(element =>
            element.Issues.Any(issue => issue.Code == StructureIssueCodes.FieldNested));
        analysis.Elements("instrText").Should().Contain(element =>
            element.HasIssue(StructureIssueCodes.FieldInstructionOutsideField));
    }

    [Test]
    public void References_LinkFootnoteAndCommentReferencesToTheirTargets()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.References());
        var references = analysis.Elements("footnoteReference");

        references.Should().HaveCount(2);
        references[0].Properties.Should().Contain(property =>
            property.Name == "Przypis dolny: cel" && property.SourceReference == "word/footnotes.xml");
        references[1].HasIssue(StructureIssueCodes.ReferenceTargetNotFound).Should().BeTrue();
        analysis.Element("commentReference").Properties.Should().Contain(property =>
            property.Name == "Komentarz: cel");
    }

    [Test]
    public void ContentControls_ResolveDataBindingAgainstCustomXml()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.ContentControls());
        var controls = analysis.Elements("sdt");

        controls[0].PropertyValue("Część customXml").Should().Be("customXml/item1.xml");
        controls[0].PropertyValue("Trafienia XPath").Should().Be("1");
        controls[0].HasIssue(StructureIssueCodes.ContentControlCustomXmlItemNotFound).Should().BeFalse();
        controls[1].HasIssue(StructureIssueCodes.ContentControlCustomXmlItemNotFound).Should().BeTrue();
    }

    [Test]
    public void TrackedChanges_ReportRevisionsAndUnmatchedRange()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.TrackedChanges());

        analysis.Element("ins").HasIssue(StructureIssueCodes.TrackedRevision).Should().BeTrue();
        analysis.Element("ins").PropertyValue("Zmiana — autor").Should().Be("QA");
        analysis.Element("del").HasIssue(StructureIssueCodes.TrackedRevision).Should().BeTrue();
        analysis.Element("moveFromRangeStart").HasIssue(StructureIssueCodes.RevisionRangeEndMissing).Should().BeTrue();
    }

    [Test]
    public void EditorCompatibility_UsesConfiguredProfileAndNeverInventsSupport()
    {
        var profile = new EditorCompatibilityOptions
        {
            ProfileName = "Test",
            DefaultLevel = EditorCompatibilityLevels.Unknown,
            Features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [EditorFeatures.DrawingAnchor] = EditorCompatibilityLevels.Partial,
                ["drawing.wrapTight"] = EditorCompatibilityLevels.Unsupported
            }
        };

        var inspector = StructureInspectorTestHost.Create(compatibility: profile);
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings(), inspector);
        var anchor = analysis.Element("anchor");

        anchor.EditorCompatibility.Should().Contain(info =>
            info.Feature == EditorFeatures.DrawingAnchor && info.Level == EditorCompatibilityLevels.Partial);
        anchor.EditorCompatibility.Should().Contain(info =>
            info.Feature == "drawing.wrapTight" && info.Level == EditorCompatibilityLevels.Unsupported);
        anchor.HasIssue(StructureIssueCodes.EditorFeatureUnsupported).Should().BeTrue();
        anchor.EditorCompatibility.Should().Contain(info =>
            info.Feature == "drawing.anchor.behindDoc" && info.Level == EditorCompatibilityLevels.Unknown);
    }

    [Test]
    public void Analyze_SurvivesAnalyzerFailureAndReportsItOnTheDocumentRoot()
    {
        var inspector = StructureInspectorTestHost.Create(extraAnalyzers: [new ThrowingAnalyzer()]);

        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Normal(), inspector);

        analysis.Elements.Should().NotBeEmpty();
        analysis.Element("document").HasIssue(StructureIssueCodes.AnalyzerFailed).Should().BeTrue();
        analysis.Elements.Should().Contain(element =>
            element.Issues.Any(issue => issue.Code == StructureIssueCodes.RedundantDirectFormatting));
    }

    [Test]
    public void Analyze_PrecomputesLowercaseSearchTextIncludingDiagnostics()
    {
        var analysis = StructureInspectorTestHost.Analyze(StructureInspectionCorpus.Drawings());
        var anchor = analysis.Element("anchor");

        anchor.SearchText.Should().Contain("wp:anchor");
        anchor.SearchText.Should().Contain("drawing_behind_document");
        anchor.SearchText.Should().Be(anchor.SearchText.ToLowerInvariant());
    }

    private sealed class ThrowingAnalyzer : IStructureAnalyzer
    {
        public void Analyze(StructureAnalysisContext context) =>
            throw new InvalidOperationException("celowa awaria testowa");
    }

    private static HeaderFooterBinding Binding(DocumentSectionInfo section, string kind, string type) =>
        section.HeaderFooterBindings.Single(binding => binding.Kind == kind && binding.Type == type);
}
