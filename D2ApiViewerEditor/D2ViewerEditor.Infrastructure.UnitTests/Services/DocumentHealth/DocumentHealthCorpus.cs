using System.IO.Compression;
using System.Text;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using OpenMcdf;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.DocumentHealth;

/// <summary>
/// Korpus „chorych” plików: każdy izoluje jedno uszkodzenie, które narzędzie ma nazwać po imieniu.
/// Uszkodzenia kontenera są wytwarzane na bajtach (ucięcie, przekłamanie, duplikat wpisu) — tego
/// nie da się zapisać żadnym builderem pakietów.
/// </summary>
internal static class DocumentHealthCorpus
{
    private const string DocumentNamespaces =
        """xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" """ +
        """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" """ +
        """xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" """ +
        """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" """ +
        """xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" """ +
        """xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" """;

    public static string Document(string body, string extraAttributes = "") =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {DocumentNamespaces} {extraAttributes}><w:body>{body}</w:body></w:document>""";

    public const string SectionA4 =
        """<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1417" w:right="1417" w:bottom="1417" w:left="1417"/></w:sectPr>""";

    public static byte[] Healthy() => StructureInspectionCorpus.Normal();

    /// <summary>Plik ucięty w połowie — brak rekordu końca katalogu centralnego.</summary>
    public static byte[] Truncated()
    {
        var bytes = Healthy();
        return bytes[..(bytes.Length / 2)];
    }

    /// <summary>Wpisy bez kompresji, potem jeden bajt treści głównej części przekłamany → CRC się nie zgadza.</summary>
    public static byte[] CrcMismatch()
    {
        var repacked = Repack(Healthy(), CompressionLevel.NoCompression);
        var needle = Encoding.UTF8.GetBytes("Redundantne");
        var offset = IndexOf(repacked, needle);

        if (offset < 0)
        {
            throw new InvalidOperationException("Fixture nie zawiera oczekiwanego tekstu.");
        }

        repacked[offset] = (byte)'X';
        return repacked;
    }

    /// <summary>Dwa wpisy o tej samej ścieżce w archiwum.</summary>
    public static byte[] DuplicateEntry()
    {
        using var buffer = new MemoryStream();

        using (var source = new ZipArchive(new MemoryStream(Healthy()), ZipArchiveMode.Read))
        using (var target = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                Copy(entry, target, entry.FullName);

                if (entry.FullName == "word/document.xml")
                {
                    Copy(entry, target, entry.FullName);
                }
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Rtf() => Encoding.ASCII.GetBytes(@"{\rtf1\ansi\deff0 {\fonttbl {\f0 Calibri;}}\f0\fs22 Hello\par}");

    public static byte[] Html() => Encoding.UTF8.GetBytes("<!DOCTYPE html><html><head><title>Sign in</title></head><body>Login</body></html>");

    /// <summary>Kontener CFB ze strumieniem WordDocument — binarny .doc.</summary>
    public static byte[] LegacyDoc()
    {
        using var buffer = new MemoryStream();

        using (var root = RootStorage.Create(buffer, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            using CfbStream stream = root.CreateStream("WordDocument");
            stream.Fill(new byte[64]);
        }

        return buffer.ToArray();
    }

    /// <summary>Kontener CFB z EncryptionInfo + EncryptedPackage — DOCX zaszyfrowany hasłem.</summary>
    public static byte[] EncryptedPackage()
    {
        using var buffer = new MemoryStream();

        using (var root = RootStorage.Create(buffer, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            using (CfbStream info = root.CreateStream("EncryptionInfo")) info.Fill(new byte[32]);
            using (CfbStream package = root.CreateStream("EncryptedPackage")) package.Fill(new byte[128]);
        }

        return buffer.ToArray();
    }

    public static byte[] ZipWithoutContentTypes()
    {
        using var buffer = new MemoryStream();

        using (var source = new ZipArchive(new MemoryStream(Healthy()), ZipArchiveMode.Read))
        using (var target = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries.Where(entry => entry.FullName != "[Content_Types].xml"))
            {
                Copy(entry, target, entry.FullName);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] MalformedMainXml() =>
        new OoxmlTestPackageBuilder()
            .WithMainDocument("""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>urwane""")
            .Build();

    public static byte[] MalformedStylesXml() =>
        new OoxmlTestPackageBuilder()
            .WithMainDocument(Document("""<w:p><w:r><w:t>ok</w:t></w:r></w:p>""" + SectionA4))
            .WithPart("word/styles.xml", "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style", StructureInspectionCorpus.StylesContentType)
            .WithRelationship("word/document.xml", "rId10", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml")
            .Build();

    public static byte[] UndeclaredIgnorablePrefix() =>
        new OoxmlTestPackageBuilder()
            .WithMainDocument(Document("""<w:p><w:r><w:t>ok</w:t></w:r></w:p>""" + SectionA4,
                """xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" mc:Ignorable="w14 zzz" """))
            .Build();

    public static byte[] TableProblems()
    {
        const string nested =
            """<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w="2000"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>N</w:t></w:r></w:p></w:tc></w:tr></w:tbl>""";
        var body =
            """<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w="4000"/></w:tblGrid>""" +
            $"""<w:tr><w:tc>{nested}</w:tc></w:tr>""" +
            """<w:tr/>""" +
            """</w:tbl>""" +
            """<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w="4000"/></w:tblGrid></w:tbl>""" +
            """<w:p/>""" + SectionA4;

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] UnbalancedField()
    {
        const string body =
            """<w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r><w:r><w:t>bez końca</w:t></w:r></w:p>""" +
            """<w:p><w:r><w:fldChar w:fldCharType="end"/></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>""" + SectionA4;

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] StructuralNesting()
    {
        const string body =
            """<w:p><w:p><w:r><w:t>zagnieżdżony</w:t></w:r></w:p></w:p>""" +
            """<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w="4000"/></w:tblGrid><w:tr><w:tc><w:r><w:t>run w komórce</w:t></w:r><w:p/></w:tc></w:tr></w:tbl>""" +
            """<w:p><w:t>tekst bez runu</w:t></w:p>""" +
            """<w:sdt><w:sdtPr/></w:sdt>""" + SectionA4;

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] Images()
    {
        const string drawing =
            """<w:p><w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="914400"/><wp:docPr id="1" name="a"/>""" +
            """<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:blipFill><a:blip r:embed="{0}"/></pic:blipFill></pic:pic></a:graphicData></a:graphic>""" +
            """</wp:inline></w:drawing></w:r></w:p>""";
        var body =
            drawing.Replace("{0}", "rId99") +
            drawing.Replace("{0}", "rId5") +
            drawing.Replace("{0}", "rId6") +
            """<w:p><w:r><w:drawing><wp:inline><wp:docPr id="1" name="b"/><a:graphic><a:graphicData uri="x"/></a:graphic></wp:inline></w:drawing></w:r></w:p>""" +
            SectionA4;

        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithBinaryPart("word/media/image1.png", jpegBytes)
            .WithBinaryPart("word/media/image2.png", [])
            .WithRelationship("word/document.xml", "rId5", OoxmlTestPackageBuilder.RelationshipType("image"), "media/image1.png")
            .WithRelationship("word/document.xml", "rId6", OoxmlTestPackageBuilder.RelationshipType("image"), "media/image2.png")
            .Build();
    }

    public static byte[] AltChunkAndMailMerge()
    {
        const string body = """<w:p><w:r><w:t>Przed</w:t></w:r></w:p><w:altChunk r:id="rId7"/>""" + SectionA4;
        const string settings =
            """<?xml version="1.0" encoding="UTF-8"?><w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:mailMerge><w:mainDocumentType w:val="formLetters"/></w:mailMerge><w:updateFields w:val="true"/></w:settings>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("word/chunk.html", "<html><body><p>chunk</p></body></html>", "text/html")
            .WithRelationship("word/document.xml", "rId7", OoxmlTestPackageBuilder.RelationshipType("aFChunk"), "chunk.html")
            .WithPart("word/settings.xml", settings, StructureInspectionCorpus.SettingsContentType)
            .WithRelationship("word/document.xml", "rId8", OoxmlTestPackageBuilder.RelationshipType("settings"), "settings.xml")
            .Build();
    }

    /// <summary>
    /// Dokument „bogaty” w konstrukcje o różnym poziomie obsługi w naszej implementacji: tabela
    /// (pełna), przypis dolny (pełna), formant (częściowa), równanie OMML i tekst ukryty (brak),
    /// komentarz w części comments.xml (brak) oraz ustawienia korespondencji seryjnej (brak, nieszkodliwe).
    /// </summary>
    public static byte[] FeatureRich()
    {
        const string body =
            """<w:p><w:r><w:t>Akapit</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>ukryty</w:t></w:r></w:p>""" +
            """<w:tbl><w:tblGrid><w:gridCol w:w="5000"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>komórka</w:t></w:r></w:p></w:tc></w:tr></w:tbl>""" +
            """<w:p><w:r><w:t>Tekst</w:t></w:r><w:r><w:footnoteReference w:id="1"/></w:r><w:commentRangeStart w:id="0"/><w:r><w:t>komentowany</w:t></w:r><w:commentRangeEnd w:id="0"/><w:r><w:commentReference w:id="0"/></w:r></w:p>""" +
            """<w:sdt><w:sdtPr><w:alias w:val="Pole"/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>formant</w:t></w:r></w:p></w:sdtContent></w:sdt>""" +
            """<w:p><m:oMathPara><m:oMath><m:r><m:t>x=1</m:t></m:r></m:oMath></m:oMathPara></w:p>""" +
            SectionA4;

        const string mathNamespace = """xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math" """;

        const string footnotes =
            """<?xml version="1.0" encoding="UTF-8"?><w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>""" +
            """<w:footnote w:id="1"><w:p><w:r><w:t>Treść przypisu</w:t></w:r></w:p></w:footnote></w:footnotes>""";

        const string comments =
            """<?xml version="1.0" encoding="UTF-8"?><w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:comment w:id="0" w:author="QA"><w:p><w:r><w:t>Uwaga</w:t></w:r></w:p></w:comment></w:comments>""";

        const string settings =
            """<?xml version="1.0" encoding="UTF-8"?><w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:mailMerge><w:mainDocumentType w:val="formLetters"/></w:mailMerge></w:settings>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body, mathNamespace))
            .WithPart("word/footnotes.xml", footnotes, "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml")
            .WithRelationship("word/document.xml", "rId20", OoxmlTestPackageBuilder.RelationshipType("footnotes"), "footnotes.xml")
            .WithPart("word/comments.xml", comments, "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml")
            .WithRelationship("word/document.xml", "rId21", OoxmlTestPackageBuilder.RelationshipType("comments"), "comments.xml")
            .WithPart("word/settings.xml", settings, StructureInspectionCorpus.SettingsContentType)
            .WithRelationship("word/document.xml", "rId22", OoxmlTestPackageBuilder.RelationshipType("settings"), "settings.xml")
            .Build();
    }

    /// <summary>Wynik „zapisu”, który zgubił tabelę i przypis (regresja) i wprowadził komórkę bez akapitu.</summary>
    public static byte[] FeatureRichAfterLossyRoundTrip()
    {
        const string body =
            """<w:p><w:r><w:t>Akapit</w:t></w:r></w:p>""" +
            """<w:tbl><w:tblGrid><w:gridCol w:w="5000"/></w:tblGrid><w:tr><w:tc><w:tbl><w:tblGrid><w:gridCol w:w="5000"/></w:tblGrid><w:tr><w:tc><w:p/></w:tc></w:tr></w:tbl></w:tc></w:tr></w:tbl>""" +
            """<w:p><w:r><w:t>Tekst komentowany</w:t></w:r></w:p>""" +
            SectionA4;

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    /// <summary>Pakiet poprawny dla Worda, ale z powtarzalnymi błędami schematu: nieznany element w rPr i zła wartość w:jc (×3).</summary>
    public static byte[] SchemaViolations()
    {
        const string paragraph =
            """<w:p><w:pPr><w:jc w:val="nonsense"/></w:pPr><w:r><w:rPr><w:notAnElement/></w:rPr><w:t>x</w:t></w:r></w:p>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(paragraph + paragraph + paragraph + SectionA4))
            .Build();
    }

    public static byte[] InvalidPageSize()
    {
        const string body =
            """<w:p><w:r><w:t>ok</w:t></w:r></w:p>""" +
            """<w:sectPr><w:pgSz w:w="100000" w:h="16838"/><w:pgMar w:top="1417" w:right="1417" w:bottom="1417" w:left="1417"/></w:sectPr>""";

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] ManyUnclosedBookmarks(int count)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < count; index++)
        {
            builder.Append($"""<w:p><w:bookmarkStart w:id="{index}" w:name="b{index}"/><w:r><w:t>x</w:t></w:r></w:p>""");
        }

        builder.Append(SectionA4);

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(builder.ToString())).Build();
    }

    public static byte[] MissingNoteTargets()
    {
        const string body =
            """<w:p><w:r><w:footnoteReference w:id="7"/></w:r><w:r><w:commentReference w:id="3"/></w:r></w:p>""" + SectionA4;
        const string footnotes =
            """<?xml version="1.0" encoding="UTF-8"?><w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:footnote w:id="1"><w:p/></w:footnote><w:footnote w:id="1"><w:p/></w:footnote></w:footnotes>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("word/footnotes.xml", footnotes, StructureInspectionCorpus.FootnotesContentType)
            .WithRelationship("word/document.xml", "rId20", OoxmlTestPackageBuilder.RelationshipType("footnotes"), "footnotes.xml")
            .Build();
    }

    private static byte[] Repack(byte[] package, CompressionLevel level)
    {
        using var buffer = new MemoryStream();

        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                Copy(entry, target, entry.FullName, level);
            }
        }

        return buffer.ToArray();
    }

    private static void Copy(ZipArchiveEntry entry, ZipArchive target, string name, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var input = entry.Open();
        using var output = target.CreateEntry(name, level).Open();
        input.CopyTo(output);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            var match = true;

            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] != needle[offset])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return index;
            }
        }

        return -1;
    }
}
