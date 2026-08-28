using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class EditProtectionExportTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private byte[] RoundTrip(byte[] original)
    {
        string html;
        using (var s = new MemoryStream(original)) html = _reader.Convert(s).Html;
        using var orig = new MemoryStream(original);
        return _writer.ConvertPreservingPackage(html, orig);
    }

    [Test]
    public void Enforced_documentProtection_survives_export()
    {
        using var saved = new MemoryStream(RoundTrip(Build(documentProtection: true)));
        using var doc = WordprocessingDocument.Open(saved, false);
        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        var prot = settings.GetFirstChild<DocumentProtection>();
        prot.Should().NotBeNull();
        prot!.Edit!.Value.Should().Be(DocumentProtectionValues.ReadOnly);
        prot.Enforcement!.Value.Should().BeTrue();
        var idxProt = settings.ChildElements.ToList().IndexOf(prot);
        var compat = settings.GetFirstChild<Compatibility>();
        if (compat != null) idxProt.Should().BeLessThan(settings.ChildElements.ToList().IndexOf(compat));
    }

    [Test]
    public void WriteProtection_recommended_survives_export_as_first_setting()
    {
        using var saved = new MemoryStream(RoundTrip(Build(writeProtectionRecommended: true)));
        using var doc = WordprocessingDocument.Open(saved, false);
        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        settings.FirstChild.Should().BeOfType<WriteProtection>();
        ((WriteProtection)settings.FirstChild!).Recommended!.Value.Should().BeTrue();
    }

    [Test]
    public void MarkAsFinal_custom_property_survives_export()
    {
        using var saved = new MemoryStream(RoundTrip(Build(markAsFinal: true)));
        using var doc = WordprocessingDocument.Open(saved, false);
        var props = doc.CustomFilePropertiesPart?.Properties;
        props.Should().NotBeNull();
        props!.Elements<CustomDocumentProperty>().Should().Contain(p => p.Name!.Value == "_MarkAsFinal");
    }

    [Test]
    public void Unprotected_document_gets_no_protection()
    {
        using var saved = new MemoryStream(RoundTrip(Build()));
        using var doc = WordprocessingDocument.Open(saved, false);
        doc.MainDocumentPart!.DocumentSettingsPart?.Settings?.GetFirstChild<DocumentProtection>().Should().BeNull();
        doc.MainDocumentPart.DocumentSettingsPart?.Settings?.GetFirstChild<WriteProtection>().Should().BeNull();
        doc.CustomFilePropertiesPart.Should().BeNull();
    }

    private static byte[] Build(bool documentProtection = false, bool writeProtectionRecommended = false, bool markAsFinal = false)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mp = doc.AddMainDocumentPart();
            mp.Document = new Document(new Body(new Paragraph(new Run(new Text("Treść chroniona."))),
                new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417, Header = 708, Footer = 708 })));
            var settings = new Settings();
            if (writeProtectionRecommended) settings.Append(new WriteProtection { Recommended = true });
            if (documentProtection)
                settings.Append(new DocumentProtection { Edit = DocumentProtectionValues.ReadOnly, Enforcement = true });
            settings.Append(new DefaultTabStop { Val = 708 });
            settings.Append(new Compatibility());
            var sp = mp.AddNewPart<DocumentSettingsPart>();
            sp.Settings = settings;
            sp.Settings.Save();
            if (markAsFinal)
            {
                var cp = doc.AddCustomFilePropertiesPart();
                cp.Properties = new Properties(new CustomDocumentProperty(new VTBool("true"))
                { FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}", PropertyId = 2, Name = "_MarkAsFinal" });
                cp.Properties.Save();
            }
            mp.Document.Save();
        }
        return ms.ToArray();
    }
}
