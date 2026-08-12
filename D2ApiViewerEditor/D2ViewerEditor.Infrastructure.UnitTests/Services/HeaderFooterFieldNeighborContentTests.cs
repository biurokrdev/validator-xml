using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class HeaderFooterFieldNeighborContentTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static Run FieldBegin() => new(new FieldChar { FieldCharType = FieldCharValues.Begin });
    private static Run FieldInstr(string instr) => new(new FieldCode(instr) { Space = SpaceProcessingModeValues.Preserve });
    private static Run FieldSep() => new(new FieldChar { FieldCharType = FieldCharValues.Separate });
    private static Run FieldEnd() => new(new FieldChar { FieldCharType = FieldCharValues.End });
    private static Run Txt(string t) => new(new Text(t) { Space = SpaceProcessingModeValues.Preserve });

    private static IEnumerable<OpenXmlElement> ComplexField(string instruction, string cachedValue)
    {
        yield return FieldBegin();
        yield return FieldInstr(instruction);
        yield return FieldSep();
        yield return Txt(cachedValue);
        yield return FieldEnd();
    }

    private static SdtRun InlineSdt(string tag, params OpenXmlElement[] contentChildren)
    {
        var content = new SdtContentRun();
        foreach (var child in contentChildren) content.Append(child);
        return new SdtRun(new SdtProperties(new Tag { Val = tag }), content);
    }

    private static MemoryStream DocxWithFooterParagraph(params OpenXmlElement[] paragraphChildren)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var footerPart = mainPart.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(new Paragraph(paragraphChildren));
            footerPart.Footer.Save();

            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Body"))),
                new SectionProperties(
                    new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) },
                    new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417, Header = 709, Footer = 709 })));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void SdtRunText_NextToPageField_IsPreserved_InDocumentOrder()
    {
        var children = new List<OpenXmlElement>
        {
            InlineSdt("DocTitle", Txt("Nazwa dokumentu")),
            Txt("Strona ")
        };
        children.AddRange(ComplexField(" PAGE ", "1"));
        children.Add(InlineSdt("Classification", Txt("Poufne")));

        using var stream = DocxWithFooterParagraph(children.ToArray());
        var html = _reader.Convert(stream).Footer!.Html;

        html.Should().Contain("Nazwa dokumentu");
        html.Should().Contain("Poufne");
        html.Should().Contain("class=\"field-page\"");

        var titleIdx = html.IndexOf("Nazwa dokumentu", StringComparison.Ordinal);
        var pageIdx = html.IndexOf("field-page", StringComparison.Ordinal);
        var classificationIdx = html.IndexOf("Poufne", StringComparison.Ordinal);
        titleIdx.Should().BeLessThan(pageIdx, "tekst formantu przed polem musi poprzedzać numer strony");
        pageIdx.Should().BeLessThan(classificationIdx, "tekst formantu po polu musi następować po numerze strony");
    }

    [Test]
    public void PageAndNumPagesFields_InsideSdtRun_RenderDynamicPlaceholders_AndKeepText()
    {
        var sdtContent = new List<OpenXmlElement> { Txt("Strona ") };
        sdtContent.AddRange(ComplexField(" PAGE ", "1"));
        sdtContent.Add(Txt(" z "));
        sdtContent.AddRange(ComplexField(" NUMPAGES ", "9"));

        using var stream = DocxWithFooterParagraph(
            Txt("Przed "),
            InlineSdt("PageXofY", sdtContent.ToArray()),
            Txt(" po"));
        var html = _reader.Convert(stream).Footer!.Html;

        html.Should().Contain("Przed ").And.Contain(" po");
        html.Should().Contain("sdt-inline");
        html.Should().Contain("{page}").And.Contain("{pages}", "numer strony w formancie musi zostać dynamiczny");
        html.Should().Contain("Strona ").And.Contain(" z ");
        html.Should().NotContain(">1<");
    }

    [Test]
    public void AutoDateField_InsideSdtRun_RendersCurrentDate_AndCarriesInstruction()
    {
        var sdtContent = new List<OpenXmlElement>();
        sdtContent.AddRange(ComplexField(" DATE \\@ \"dd.MM.yyyy\" ", "06.07.2026"));

        using var stream = DocxWithFooterParagraph(InlineSdt("Data", sdtContent.ToArray()));
        var html = _reader.Convert(stream).Footer!.Html;

        html.Should().Contain(DateTime.Now.ToString("dd.MM.yyyy"));
        html.Should().Contain("class=\"field-date\"");
        html.Should().Contain("data-fld-instr=\"DATE");
        html.Should().NotContain("{page}");
    }

    [Test]
    public void CachedValueField_InsideSdtRun_KeepsCachedValue_ForHistoricalDates()
    {
        var sdtContent = new List<OpenXmlElement>();
        sdtContent.AddRange(ComplexField(" CREATEDATE \\@ \"dd.MM.yyyy\" ", "06.07.2026"));

        using var stream = DocxWithFooterParagraph(InlineSdt("Data", sdtContent.ToArray()));
        var html = _reader.Convert(stream).Footer!.Html;

        html.Should().Contain("06.07.2026");
        html.Should().NotContain("{page}");
    }

    [Test]
    public void PlainTextRuns_AroundComplexPageFields_ArePreserved_InOrder()
    {
        var children = new List<OpenXmlElement> { Txt("Nazwa dokumentu"), Txt("Strona ") };
        children.AddRange(ComplexField(" PAGE ", "1"));
        children.Add(Txt(" z "));
        children.AddRange(ComplexField(" NUMPAGES ", "9"));
        children.Add(Txt("Poufne"));

        using var stream = DocxWithFooterParagraph(children.ToArray());
        var html = _reader.Convert(stream).Footer!.Html;

        html.Should().Contain("Nazwa dokumentu").And.Contain("Strona ").And.Contain(" z ").And.Contain("Poufne");
        html.Should().Contain("{page}").And.Contain("{pages}");
        html.IndexOf("Nazwa dokumentu", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("{page}", StringComparison.Ordinal));
        html.IndexOf("{page}", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Poufne", StringComparison.Ordinal));
    }

    [Test]
    public void Writer_SdtInlineWithPageField_ExportsFieldNotLiteralPlaceholder()
    {
        var footer = new HeaderFooterContent
        {
            Html = "<p><span class=\"sdt-inline\" data-sdt-tag=\"PageXofY\">Strona "
                 + "<span class=\"field-page\">{page}</span> z "
                 + "<span class=\"field-numpages\">{pages}</span></span></p>"
        };

        var bytes = _writer.Convert("<p>Body</p>", footer: footer);
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var footerXml = doc.MainDocumentPart!.FooterParts.First().Footer!;

        var sdt = footerXml.Descendants<SdtRun>().Should().ContainSingle().Subject;
        sdt.Descendants<SimpleField>().Select(f => f.Instruction!.Value!.Trim())
            .Should().Contain("PAGE").And.Contain("NUMPAGES");
        footerXml.InnerText.Should().NotContain("{page}").And.NotContain("{pages}");
        footerXml.InnerText.Should().Contain("Strona ").And.Contain(" z ");
    }

    [Test]
    public void FullRoundTrip_GalleryPageFieldInSdt_SurvivesExportAndReimport()
    {
        var sdtContent = new List<OpenXmlElement> { Txt("Strona ") };
        sdtContent.AddRange(ComplexField(" PAGE ", "1"));

        using var stream = DocxWithFooterParagraph(
            Txt("Przed "),
            InlineSdt("PageXofY", sdtContent.ToArray()),
            Txt(" po"));
        var imported = _reader.Convert(stream);

        var exported = _writer.Convert(imported.Html, footer: imported.Footer);
        using var reimportStream = new MemoryStream(exported);
        var reimported = new DocxToHtmlConverter().Convert(reimportStream);

        var html = reimported.Footer!.Html;
        html.Should().Contain("Przed ").And.Contain(" po").And.Contain("Strona ");
        html.Should().Contain("{page}", "po zapisie i ponownym otwarciu numer strony musi pozostać dynamiczny");
        html.Should().Contain("sdt-inline", "tożsamość formantu musi przetrwać round-trip");
    }
}
