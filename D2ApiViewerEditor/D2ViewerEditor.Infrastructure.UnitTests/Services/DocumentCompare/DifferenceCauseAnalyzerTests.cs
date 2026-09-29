using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentCompare;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentCompare;

[TestFixture]
public class DifferenceCauseAnalyzerTests
{
    private static DocumentDifference Difference(
        DifferenceKind kind,
        string part = "word/document.xml",
        string? leftPath = "/w:document[1]/w:body[1]/w:p[3]",
        string? rightPath = null,
        string? name = null,
        string? leftValue = null,
        string? rightValue = null,
        string? leftExcerpt = null,
        string? rightExcerpt = null) =>
        new(kind, "Tekst", part, leftPath, rightPath, 1, 1, name, leftValue, rightValue, leftExcerpt, rightExcerpt, false, "Akapit", null);

    [Test]
    public void UnsupportedConstructLost_IsBlamedOnPipeline_WithRegistryNote()
    {
        var analysis = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:p[3]/w:r[2]/w:commentReference[1]"));

        analysis.Cause.Should().Be(DifferenceCause.PipelineUnsupported);
        analysis.Impact.Should().Be(DifferenceImpact.DataLoss);
        analysis.FeatureKey.Should().Be(FeatureKeys.Comments);
        analysis.Explanation.Should().Contain("NASZ pipeline tego nie obsługuje").And.Contain("KR-03");
        analysis.CodePointer.Should().Contain("DocxToHtmlConverter");
    }

    [Test]
    public void HiddenTextLost_HasPdfImpact_AndMathLost_IsDataLoss()
    {
        DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
                leftPath: "/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:vanish[1]"))
            .Impact.Should().Be(DifferenceImpact.PdfDifference);

        var math = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:p[1]/m:oMathPara[1]/m:oMath[1]"));
        math.Cause.Should().Be(DifferenceCause.PipelineUnsupported);
        math.Impact.Should().Be(DifferenceImpact.DataLoss);
    }

    [Test]
    public void ContentLost_WithFullSupport_IsUserEditOrLoss_NotBlamedBlindly()
    {
        var paragraph = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft));
        paragraph.Cause.Should().Be(DifferenceCause.UserEditOrLoss);
        paragraph.Impact.Should().Be(DifferenceImpact.DataLoss);
        paragraph.Explanation.Should().Contain("użytkownik usunął").And.Contain("reader go pominął");

        var table = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft, leftPath: "/w:document[1]/w:body[1]/w:tbl[2]"));
        table.Cause.Should().Be(DifferenceCause.UserEditOrLoss);
        table.FeatureKey.Should().Be(FeatureKeys.Tables);
    }

    [Test]
    public void WordNoise_AndRewrittenIdentifiers_AreHarmless()
    {
        DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft, leftPath: "/w:document[1]/w:body[1]/w:p[1]/w:proofErr[1]"))
            .Cause.Should().Be(DifferenceCause.WordNoise);
        DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeOnlyInLeft, leftPath: "/w:document[1]/w:body[1]/w:p[1]", name: "w14:paraId", leftValue: "1A2B"))
            .Cause.Should().Be(DifferenceCause.WordNoise);

        var relationshipId = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged, part: "word/_rels/document.xml.rels",
            leftPath: "/Relationships[1]/Relationship[3]", rightPath: "/Relationships[1]/Relationship[3]", name: "Id", leftValue: "rId5", rightValue: "R1a2b",
            leftExcerpt: "<Relationship Id=\"rId5\" Type=\"x/image\" Target=\"media/image1.png\"/>", rightExcerpt: "<Relationship Id=\"R1a2b\" Type=\"x/image\" Target=\"media/image1.png\"/>"));
        relationshipId.Cause.Should().Be(DifferenceCause.IdentifierRewrite);
        relationshipId.Impact.Should().Be(DifferenceImpact.None);
    }

    [Test]
    public void DifferentRelationshipsPairedPositionally_IsPairingArtifact()
    {
        var analysis = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged, part: "_rels/.rels",
            leftPath: "/Relationships[1]/Relationship[1]", rightPath: "/Relationships[1]/Relationship[1]", name: "Id", leftValue: "rId3", rightValue: "Re6b6d5a8606c4966",
            leftExcerpt: "<Relationship Id=\"rId3\" Type=\"x/extended-properties\" Target=\"docProps/app.xml\"/>",
            rightExcerpt: "<Relationship Id=\"Re6b6d5a8606c4966\" Type=\"x/officeDocument\" Target=\"/word/document.xml\"/>"));

        analysis.Cause.Should().Be(DifferenceCause.PairingArtifact);
        analysis.Explanation.Should().Contain("nie jest realna różnica");
    }

    [Test]
    public void WriterAdditions_AreNormalization_AndNumberingLosses_AreRegeneration()
    {
        var fonts = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInRight, leftPath: null,
            rightPath: "/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:rFonts[1]"));
        fonts.Cause.Should().Be(DifferenceCause.WriterNormalization);
        fonts.Impact.Should().Be(DifferenceImpact.Cosmetic);
        fonts.Explanation.Should().Contain("zapieczone inline");

        var numbering = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeOnlyInLeft, part: "word/numbering.xml",
            leftPath: "/w:numbering[1]/w:abstractNum[2]", name: "w15:restartNumberingAfterBreak", leftValue: "0"));
        numbering.Cause.Should().Be(DifferenceCause.PipelineRegenerated);
        numbering.FeatureKey.Should().Be(FeatureKeys.Lists);
        numbering.Impact.Should().Be(DifferenceImpact.Cosmetic);

        var appXml = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.PartOnlyInLeft, part: "docProps/app.xml", leftPath: null));
        appXml.Cause.Should().Be(DifferenceCause.PipelinePartial);
        appXml.CodePointer.Should().Contain("ExtendedFilePropertiesPart");
    }

    [Test]
    public void ValueChanges_RoundingIsHarmless_LargeDeltaAndTextAreForHumans()
    {
        var rounding = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            leftPath: "/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:ind[1]", rightPath: "/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:ind[1]", name: "w:left", leftValue: "1417", rightValue: "1418"));
        rounding.Cause.Should().Be(DifferenceCause.WriterNormalization);

        var large = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            leftPath: "/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:ind[1]", rightPath: "/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:ind[1]", name: "w:left", leftValue: "1417", rightValue: "720"));
        large.Cause.Should().Be(DifferenceCause.UserEditOrLoss);
        large.Impact.Should().Be(DifferenceImpact.Layout);

        var text = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.TextChanged, leftValue: "najmu", rightValue: "sprzedaży"));
        text.Cause.Should().Be(DifferenceCause.UserEdit);

        var split = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.TextChanged, leftValue: "Umowa", rightValue: "Umowa najmu"));
        split.Cause.Should().Be(DifferenceCause.WriterNormalization);
        split.Explanation.Should().Contain("podziale na runy");
    }

    [Test]
    public void RealFilePatterns_FromEditorRoundTrip_AreExplained()
    {
        var compat = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft, part: "word/settings.xml", leftPath: "/w:settings[1]/w:compat[1]"));
        compat.Cause.Should().Be(DifferenceCause.PipelineRegenerated);
        compat.Impact.Should().Be(DifferenceImpact.PdfDifference);
        compat.Explanation.Should().Contain("TRYBIE ZGODNOŚCI");

        var style = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            leftPath: "/w:document[1]/w:body[1]/w:p[2]/w:pPr[1]/w:pStyle[1]", rightPath: "/w:document[1]/w:body[1]/w:p[2]/w:pPr[1]/w:pStyle[1]",
            name: "w:val", leftValue: "Nagwek1", rightValue: "Heading1"));
        style.Cause.Should().Be(DifferenceCause.PipelinePartial);
        style.Explanation.Should().Contain("Nagwek1").And.Contain("poziom konspektu");

        var indent = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            leftPath: "/w:document[1]/w:body[1]/w:p[13]/w:pPr[1]/w:ind[1]", rightPath: "/w:document[1]/w:body[1]/w:p[13]/w:pPr[1]/w:ind[1]",
            name: "w:left", leftValue: "280", rightValue: "270"));
        indent.Cause.Should().Be(DifferenceCause.WriterNormalization, "10 twips = zaokrąglenie do piksela, nie edycja");
        indent.Explanation.Should().Contain("1 px = 15 twips");

        var themeFont = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:p[2]/w:r[1]/w:rPr[1]/w:rFonts[1]", name: "w:asciiTheme", leftValue: "minorHAnsi"));
        themeFont.Cause.Should().Be(DifferenceCause.PipelinePartial);
        themeFont.Explanation.Should().Contain("MOTYWU");

        var paragraphMark = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft, leftPath: "/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:rPr[1]"));
        paragraphMark.Cause.Should().Be(DifferenceCause.PipelinePartial);
        paragraphMark.Explanation.Should().Contain("KOŃCA AKAPITU");

        var header = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            leftPath: "/w:document[1]/w:body[1]/w:sectPr[1]/w:pgMar[1]", rightPath: "/w:document[1]/w:body[1]/w:sectPr[1]/w:pgMar[1]",
            name: "w:header", leftValue: "708", rightValue: "142"));
        header.Cause.Should().Be(DifferenceCause.PipelinePartial);
        header.Impact.Should().Be(DifferenceImpact.Layout);

        var contentType = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged, part: "[Content_Types].xml",
            leftPath: "/Types[1]/Default[3]", rightPath: "/Types[1]/Default[1]", name: "ContentType",
            leftValue: "application/xml", rightValue: "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"));
        contentType.Explanation.Should().Contain("Default Extension");

        var lang = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft, leftPath: "/w:document[1]/w:body[1]/w:p[10]/w:r[1]/w:rPr[1]/w:lang[1]"));
        lang.Cause.Should().Be(DifferenceCause.PipelinePartial, "język runu to nie szum — decyduje o dzieleniu wyrazów");

        var renamed = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.PartRenamed, part: "word/media/image1.png",
            leftPath: "word/media/image1.png", rightPath: "media/image.png", leftValue: "word/media/image1.png", rightValue: "media/image.png"));
        renamed.Cause.Should().Be(DifferenceCause.WriterNormalization);
        renamed.Impact.Should().Be(DifferenceImpact.None);
    }

    [Test]
    public void StylesPartMissing_MeansPassThroughDidNotRun()
    {
        var analysis = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.PartOnlyInLeft, part: "word/styles.xml", leftPath: null));

        analysis.Cause.Should().Be(DifferenceCause.PipelineRegenerated);
        analysis.Impact.Should().Be(DifferenceImpact.Layout);
        analysis.Explanation.Should().Contain("pass-through");
    }

    [Test]
    public void RelationshipTypeOrTargetChange_BetweenDifferentRelationships_IsPairingArtifact()
    {
        var target = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            part: "word/_rels/document.xml.rels",
            leftPath: "/Relationships[1]/Relationship[7]", rightPath: "/Relationships[1]/Relationship[7]",
            name: "Target", leftValue: "media/image4.png", rightValue: "/word/styles.xml",
            leftExcerpt: "<Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image4.png\"/>",
            rightExcerpt: "<Relationship Id=\"R1a2b\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"/word/styles.xml\"/>"));

        target.Cause.Should().Be(DifferenceCause.PairingArtifact);
        target.Impact.Should().Be(DifferenceImpact.None);
        target.Explanation.Should().Contain("image").And.Contain("styles");

        var type = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            part: "_rels/.rels", leftPath: "/Relationships[1]/Relationship[1]", rightPath: "/Relationships[1]/Relationship[1]",
            name: "Type",
            leftValue: "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties",
            rightValue: "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"));

        type.Cause.Should().Be(DifferenceCause.PairingArtifact);
    }

    [Test]
    public void TableGridColumnWidthChange_IsWordLayoutCache_NotALoss()
    {
        var analysis = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[20]/w:tblGrid[1]/w:gridCol[1]", rightPath: "/w:document[1]/w:body[1]/w:tbl[20]/w:tblGrid[1]/w:gridCol[1]",
            name: "w:w", leftValue: "1800", rightValue: "3437"));

        analysis.Cause.Should().Be(DifferenceCause.WriterNormalization);
        analysis.Impact.Should().Be(DifferenceImpact.None);
        analysis.Explanation.Should().Contain("cache układu Worda");
        analysis.FeatureKey.Should().Be(FeatureKeys.Tables);
    }

    [Test]
    public void TablePropertyNormalizations_EmptyContainer_BidiSides_ZeroCellSpacing_AreHarmless()
    {
        var emptyTrPr = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[37]/w:tr[1]/w:trPr[1]",
            leftExcerpt: "<w:trPr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" />"));
        emptyTrPr.Cause.Should().Be(DifferenceCause.WriterNormalization);
        emptyTrPr.Impact.Should().Be(DifferenceImpact.None);
        emptyTrPr.Explanation.Should().Contain("Pusty w:trPr");

        var fullTrPr = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[37]/w:tr[1]/w:trPr[1]",
            leftExcerpt: "<w:trPr><w:trHeight w:val=\"2000\" w:hRule=\"exact\"/></w:trPr>"));
        fullTrPr.Cause.Should().NotBe(DifferenceCause.WriterNormalization);

        var start = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[14]/w:tblPr[1]/w:tblCellMar[1]/w:start[1]",
            leftExcerpt: "<w:start w:w=\"200\" w:type=\"dxa\"/>"));
        start.Cause.Should().Be(DifferenceCause.WriterNormalization);
        start.Impact.Should().Be(DifferenceImpact.None);
        start.Explanation.Should().Contain("w:left");

        var left = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInRight,
            leftPath: null, rightPath: "/w:document[1]/w:body[1]/w:tbl[14]/w:tblPr[1]/w:tblCellMar[1]/w:left[1]"));
        left.Cause.Should().Be(DifferenceCause.WriterNormalization);
        left.Impact.Should().Be(DifferenceImpact.None);

        var zeroSpacing = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[16]/w:tblPr[1]/w:tblCellSpacing[1]",
            leftExcerpt: "<w:tblCellSpacing w:w=\"0\" w:type=\"dxa\"/>"));
        zeroSpacing.Cause.Should().Be(DifferenceCause.WriterNormalization);
        zeroSpacing.Impact.Should().Be(DifferenceImpact.None);

        var realSpacing = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementOnlyInLeft,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[16]/w:tblPr[1]/w:tblCellSpacing[1]",
            leftExcerpt: "<w:tblCellSpacing w:w=\"60\" w:type=\"dxa\"/>"));
        realSpacing.Cause.Should().Be(DifferenceCause.PipelinePartial);
        realSpacing.Impact.Should().Be(DifferenceImpact.Layout);
        realSpacing.CodePointer.Should().Contain("HtmlToDocxConverter");
    }

    [Test]
    public void ElementMovedWithinSamePropertyContainer_IsSchemaOrdering_NotContentMove()
    {
        var moved = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementMoved,
            leftPath: "/w:document[1]/w:body[1]/w:tbl[29]/w:tr[1]/w:tc[2]/w:tcPr[1]/w:tcMar[1]",
            rightPath: "/w:document[1]/w:body[1]/w:tbl[29]/w:tr[1]/w:tc[2]/w:tcPr[1]/w:tcMar[1]"));
        moved.Cause.Should().Be(DifferenceCause.WriterNormalization);
        moved.Impact.Should().Be(DifferenceImpact.None);
        moved.Explanation.Should().Contain("wg schematu");

        var paragraphMoved = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.ElementMoved,
            leftPath: "/w:document[1]/w:body[1]/w:p[3]", rightPath: "/w:document[1]/w:body[1]/w:p[9]"));
        paragraphMoved.Cause.Should().Be(DifferenceCause.UserEditOrLoss);
    }

    [Test]
    public void RelationshipTarget_RelativeVersusAbsoluteSamePart_IsHarmlessNormalization()
    {
        var sameType = "<Relationship Id=\"x\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"?\"/>";
        var analysis = DifferenceCauseAnalyzer.Analyze(Difference(DifferenceKind.AttributeValueChanged,
            part: "word/_rels/document.xml.rels",
            leftPath: "/Relationships[1]/Relationship[3]", rightPath: "/Relationships[1]/Relationship[3]",
            name: "Target", leftValue: "media/image1.png", rightValue: "/word/media/image1.png",
            leftExcerpt: sameType, rightExcerpt: sameType));

        analysis.Cause.Should().Be(DifferenceCause.WriterNormalization);
        analysis.Impact.Should().Be(DifferenceImpact.None);
        analysis.Explanation.Should().Contain("względna vs bezwzględna");
    }
}
