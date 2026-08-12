namespace D2ViewerEditor.Infrastructure.UnitTests.Fixtures;

public static class StructureInspectionCorpus
{
    public const string WordprocessingTransitional = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string WordprocessingStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    public const string RelationshipsTransitional = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string RelationshipsStrict = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    public const string StylesContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml";
    public const string NumberingContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml";
    public const string HeaderContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml";
    public const string FooterContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml";
    public const string SettingsContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml";
    public const string FootnotesContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml";
    public const string CommentsContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml";
    public const string CustomXmlPropsContentType = "application/vnd.openxmlformats-officedocument.customXmlProperties+xml";

    private const string DocumentNamespaces =
        """xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" """ +
        """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" """ +
        """xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" """ +
        """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" """ +
        """xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" """ +
        """xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" """ +
        """xmlns:v="urn:schemas-microsoft-com:vml" """ +
        """xmlns:wp14="http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing" """ +
        """xmlns:asvg="http://schemas.microsoft.com/office/drawing/2016/SVG/main" """ +
        """mc:Ignorable="wp14 asvg" """;

    public static string Document(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {DocumentNamespaces}><w:body>{body}</w:body></w:document>""";

    public static byte[] Normal()
    {
        const string body =
            """<w:p><w:pPr><w:pStyle w:val="BodyText"/></w:pPr><w:r><w:rPr><w:sz w:val="24"/></w:rPr><w:t>Redundantne 12 pt</w:t></w:r></w:p>""" +
            """<w:p><w:pPr><w:pStyle w:val="BodyText"/></w:pPr><w:r><w:rPr><w:sz w:val="40"/><w:b/></w:rPr><w:t>Realna zmiana</w:t></w:r></w:p>""" +
            """<w:p><w:r><w:t>Bez stylu</w:t></w:r></w:p>""" +
            """<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1417" w:right="1417" w:bottom="1417" w:left="1417"/></w:sectPr>""";

        const string styles =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            """<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="22"/></w:rPr></w:rPrDefault>""" +
            """<w:pPrDefault><w:pPr><w:spacing w:after="160"/></w:pPr></w:pPrDefault></w:docDefaults>""" +
            """<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:rPr><w:sz w:val="22"/></w:rPr></w:style>""" +
            """<w:style w:type="paragraph" w:styleId="BodyText"><w:name w:val="Body Text"/><w:basedOn w:val="Normal"/><w:rPr><w:sz w:val="24"/></w:rPr></w:style>""" +
            """<w:style w:type="paragraph" w:styleId="Broken"><w:name w:val="Broken"/><w:basedOn w:val="NieMaTakiego"/></w:style>""" +
            """</w:styles>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("word/styles.xml", styles, StylesContentType)
            .WithRelationship("word/document.xml", "rId10", OoxmlTestPackageBuilder.RelationshipType("styles"), "styles.xml")
            .Build();
    }

    public static byte[] CustomMainDocumentPath()
    {
        const string body = """<w:p><w:r><w:t>Nietypowa ścieżka</w:t></w:r></w:p>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body), "content/main-document.xml")
            .Build();
    }

    public static byte[] StrictOoxml()
    {
        var document =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            $"""<w:document xmlns:w="{WordprocessingStrict}" xmlns:r="{RelationshipsStrict}">""" +
            """<w:body><w:p><w:pPr><w:ind w:left="-120"/></w:pPr><w:r><w:rPr><w:vanish/></w:rPr><w:t>Strict</w:t></w:r></w:p>""" +
            """<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/></w:tblPr><w:tblGrid><w:gridCol w:w="4000"/></w:tblGrid>""" +
            """<w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>""";

        return new OoxmlTestPackageBuilder().WithMainDocument(document).Build();
    }

    public static byte[] Numbering()
    {
        const string body =
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="7"/></w:numPr></w:pPr><w:r><w:t>Punkt</w:t></w:r></w:p>""" +
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="99"/></w:numPr></w:pPr><w:r><w:t>Sierota</w:t></w:r></w:p>""";

        const string numbering =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            """<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""" +
            """<w:abstractNum w:abstractNumId="3">""" +
            """<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl>""" +
            """<w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1.%2."/><w:suff w:val="tab"/>""" +
            """<w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Symbol"/></w:rPr></w:lvl>""" +
            """</w:abstractNum>""" +
            """<w:num w:numId="7"><w:abstractNumId w:val="3"/><w:lvlOverride w:ilvl="1"><w:startOverride w:val="5"/></w:lvlOverride></w:num>""" +
            """</w:numbering>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("word/numbering.xml", numbering, NumberingContentType)
            .WithRelationship("word/document.xml", "rId20", OoxmlTestPackageBuilder.RelationshipType("numbering"), "numbering.xml")
            .Build();
    }

    public static byte[] Tables()
    {
        const string nested =
            """<w:tbl><w:tblPr><w:tblW w:w="2000" w:type="dxa"/></w:tblPr><w:tblGrid><w:gridCol w:w="2000"/></w:tblGrid>""" +
            """<w:tr><w:tc><w:p><w:r><w:t>Zagnieżdżona</w:t></w:r></w:p></w:tc></w:tr></w:tbl>""";

        var body =
            """<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblpPr w:leftFromText="142" w:tblpY="1"/></w:tblPr>""" +
            """<w:tblGrid><w:gridCol w:w="4500"/><w:gridCol w:w="4500"/></w:tblGrid>""" +
            """<w:tr><w:tc><w:tcPr><w:tcW w:w="4500" w:type="dxa"/><w:vMerge w:val="restart"/></w:tcPr>""" +
            $"""<w:p><w:r><w:t>A1</w:t></w:r></w:p>{nested}<w:tcPr><w:hMerge/></w:tcPr></w:tc>""" +
            """<w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc></w:tr>""" +
            """<w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p/></w:tc></w:tr>""" +
            """<w:tr><w:tc><w:p/></w:tc></w:tr>""" +
            """<w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p/></w:tc></w:tr>""" +
            """</w:tbl>""";

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] Drawings()
    {
        const string anchor =
            """<w:p><w:r><w:drawing><wp:anchor distT="0" distB="0" distL="114300" distR="114300" simplePos="0" """ +
            """relativeHeight="251658240" behindDoc="1" locked="0" layoutInCell="0" allowOverlap="1">""" +
            """<wp:simplePos x="0" y="0"/>""" +
            """<wp:positionH relativeFrom="page"><wp:posOffset>-635000</wp:posOffset></wp:positionH>""" +
            """<wp:positionV relativeFrom="paragraph"><wp:posOffset>152400</wp:posOffset></wp:positionV>""" +
            """<wp:extent cx="2857500" cy="1428750"/><wp:effectExtent l="0" t="0" r="10" b="10"/>""" +
            """<wp:wrapTight wrapText="bothSides"><wp:wrapPolygon edited="0"><wp:start x="0" y="0"/><wp:lineTo x="0" y="21600"/></wp:wrapPolygon></wp:wrapTight>""" +
            """<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>""" +
            """<pic:blipFill><a:blip r:embed="rId404"/><a:srcRect l="5000" t="5000"/></pic:blipFill>""" +
            """<pic:spPr><a:xfrm rot="900000" flipH="1"/></pic:spPr></pic:pic></a:graphicData></a:graphic>""" +
            """</wp:anchor></w:drawing></w:r></w:p>""";

        const string inline =
            """<w:p><w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="1905000" cy="952500"/>""" +
            """<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>""" +
            """<pic:blipFill><a:blip r:embed="rId30"/></pic:blipFill></pic:pic></a:graphicData></a:graphic>""" +
            """</wp:inline></w:drawing></w:r></w:p>""";

        const string legacy =
            """<w:p><w:r><w:pict><v:shape id="_x0000_s1026" style="width:100pt;height:50pt"/></w:pict></w:r></w:p>""" +
            """<w:p><w:r><w:object w:dxaOrig="1440" w:dyaOrig="1440"/></w:r></w:p>""";

        const string alternateContent =
            """<w:p><w:r><mc:AlternateContent><mc:Choice Requires="wp14"><w:t>Nowszy wariant</w:t></mc:Choice></mc:AlternateContent></w:r></w:p>""";

        const string svg =
            """<w:p><w:r><w:drawing><wp:inline><wp:extent cx="0" cy="0"/>""" +
            """<a:graphic><a:graphicData uri="x"><pic:pic><pic:blipFill><a:blip r:embed="rId30">""" +
            """<a:extLst><a:ext uri="{96DAC541-7B7A-43D3-8B79-37D633B846F1}"><asvg:svgBlip r:embed="rId30"/></a:ext></a:extLst>""" +
            """</a:blip></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(anchor + inline + legacy + alternateContent + svg))
            .WithBinaryPart("word/media/image1.png", OoxmlTestPackageBuilder.PngPixel())
            .WithRelationship("word/document.xml", "rId30", OoxmlTestPackageBuilder.RelationshipType("image"), "media/image1.png")
            .Build();
    }

    public static byte[] SectionsWithHeaders()
    {
        const string body =
            """<w:p><w:r><w:t>Sekcja 1</w:t></w:r></w:p>""" +
            """<w:p><w:pPr><w:sectPr><w:headerReference w:type="default" r:id="rId40"/>""" +
            """<w:footerReference w:type="default" r:id="rId41"/><w:footerReference w:type="first" r:id="rId42"/>""" +
            """<w:titlePg/><w:pgSz w:w="11906" w:h="16838"/><w:cols w:num="2" w:space="708"/></w:sectPr></w:pPr></w:p>""" +
            """<w:p><w:r><w:t>Sekcja 2</w:t></w:r></w:p>""" +
            """<w:sectPr><w:footerReference w:type="default" r:id="rId43"/><w:pgSz w:w="11906" w:h="16838"/></w:sectPr>""";

        const string header = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:r><w:t>Nagłówek</w:t></w:r></w:p></w:hdr>""";
        const string footer = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p></w:ftr>""";
        const string settings = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:evenAndOddHeaders/></w:settings>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("word/header1.xml", header, HeaderContentType)
            .WithPart("word/footer1.xml", footer, FooterContentType)
            .WithPart("word/footer2.xml", footer, FooterContentType)
            .WithPart("word/footer3.xml", footer, FooterContentType)
            .WithPart("word/footer9.xml", footer, FooterContentType)
            .WithPart("word/settings.xml", settings, SettingsContentType)
            .WithRelationship("word/document.xml", "rId40", OoxmlTestPackageBuilder.RelationshipType("header"), "header1.xml")
            .WithRelationship("word/document.xml", "rId41", OoxmlTestPackageBuilder.RelationshipType("footer"), "footer1.xml")
            .WithRelationship("word/document.xml", "rId42", OoxmlTestPackageBuilder.RelationshipType("footer"), "footer2.xml")
            .WithRelationship("word/document.xml", "rId43", OoxmlTestPackageBuilder.RelationshipType("footer"), "footer3.xml")
            .WithRelationship("word/document.xml", "rId44", OoxmlTestPackageBuilder.RelationshipType("footer"), "footer9.xml")
            .WithRelationship("word/document.xml", "rId45", OoxmlTestPackageBuilder.RelationshipType("settings"), "settings.xml")
            .Build();
    }

    public static byte[] Fields()
    {
        const string body =
            """<w:p><w:r><w:instrText> POZAPOLEM </w:instrText></w:r></w:p>""" +
            """<w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> PAGE \\* MERGEFORMAT </w:instrText></w:r>""" +
            """<w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>7</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>""" +
            """<w:p><w:fldSimple w:instr=" NUMPAGES "><w:r><w:t>12</w:t></w:r></w:fldSimple></w:p>""" +
            """<w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> IF </w:instrText></w:r>""" +
            """<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>""";

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] References()
    {
        const string body =
            """<w:p><w:r><w:footnoteReference w:id="2"/></w:r><w:r><w:commentReference w:id="1"/></w:r>""" +
            """<w:r><w:footnoteReference w:id="404"/></w:r></w:p>""";

        const string footnotes = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:footnote w:id="2"><w:p><w:r><w:t>Treść przypisu</w:t></w:r></w:p></w:footnote></w:footnotes>""";
        const string comments = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:comment w:id="1" w:author="QA"><w:p><w:r><w:t>Uwaga</w:t></w:r></w:p></w:comment></w:comments>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("word/footnotes.xml", footnotes, FootnotesContentType)
            .WithPart("word/comments.xml", comments, CommentsContentType)
            .WithRelationship("word/document.xml", "rId50", OoxmlTestPackageBuilder.RelationshipType("footnotes"), "footnotes.xml")
            .WithRelationship("word/document.xml", "rId51", OoxmlTestPackageBuilder.RelationshipType("comments"), "comments.xml")
            .Build();
    }

    public static byte[] ContentControls()
    {
        const string body =
            """<w:p><w:sdt><w:sdtPr><w:alias w:val="Klient"/><w:tag w:val="klient"/><w:text/>""" +
            """<w:dataBinding w:prefixMappings="xmlns:ns0='urn:qutalo:dane'" w:xpath="/ns0:dane[1]/ns0:klient[1]" w:storeItemID="{11111111-1111-1111-1111-111111111111}"/>""" +
            """</w:sdtPr><w:sdtContent><w:r><w:t>Qutalo</w:t></w:r></w:sdtContent></w:sdt></w:p>""" +
            """<w:p><w:sdt><w:sdtPr><w:tag w:val="wiszacy"/>""" +
            """<w:dataBinding w:xpath="/brak" w:storeItemID="{99999999-9999-9999-9999-999999999999}"/>""" +
            """</w:sdtPr><w:sdtContent><w:r><w:t>X</w:t></w:r></w:sdtContent></w:sdt></w:p>""";

        const string item = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><dane xmlns="urn:qutalo:dane"><klient>Qutalo</klient></dane>""";
        const string itemProps = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><ds:datastoreItem xmlns:ds="http://schemas.openxmlformats.org/officeDocument/2006/customXml" ds:itemID="{11111111-1111-1111-1111-111111111111}"/>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithPart("customXml/item1.xml", item, "application/xml")
            .WithPart("customXml/itemProps1.xml", itemProps, CustomXmlPropsContentType)
            .WithRelationship("word/document.xml", "rId60", OoxmlTestPackageBuilder.RelationshipType("customXml"), "../customXml/item1.xml")
            .WithRelationship("customXml/item1.xml", "rId1", OoxmlTestPackageBuilder.RelationshipType("customXmlProps"), "itemProps1.xml")
            .Build();
    }

    public static byte[] TrackedChanges()
    {
        const string body =
            """<w:p><w:ins w:id="1" w:author="QA" w:date="2026-01-01T00:00:00Z"><w:r><w:t>Nowe</w:t></w:r></w:ins>""" +
            """<w:del w:id="2" w:author="QA"><w:r><w:delText>Stare</w:delText></w:r></w:del></w:p>""" +
            """<w:p><w:moveFromRangeStart w:id="5" w:name="blok"/><w:r><w:t>Przenoszone</w:t></w:r></w:p>""";

        return new OoxmlTestPackageBuilder().WithMainDocument(Document(body)).Build();
    }

    public static byte[] MalformedContentTypes()
    {
        const string contentTypes =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""" +
            """<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""" +
            """<Default Extension="rels" ContentType="application/duplikat"/>""" +
            """<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>""" +
            """<Override PartName="/word/nie-ma-mnie.xml" ContentType="application/xml"/>""" +
            """<Override PartName="bez-ukosnika.xml" ContentType="application/xml"/>""" +
            """</Types>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document("""<w:p><w:r><w:t>X</w:t></w:r></w:p>"""))
            .WithPart("word/bez-typu.xml", """<?xml version="1.0"?><x/>""", "application/xml")
            .WithRawContentTypes(contentTypes)
            .Build();
    }

    public static byte[] MalformedRelationships()
    {
        const string relationships =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
            """<Relationship Id="rId70" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/nie-ma.png"/>""" +
            """<Relationship Id="rId70" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>""" +
            """<Relationship Id="rId71" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../../poza-pakietem.png"/>""" +
            """<Relationship Id="rId72" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.invalid/strona" TargetMode="External"/>""" +
            """</Relationships>""";

        const string body =
            """<w:p><w:hyperlink r:id="rId72"><w:r><w:t>Link</w:t></w:r></w:hyperlink></w:p>""" +
            """<w:p><w:r><w:drawing><wp:inline><wp:extent cx="10" cy="10"/>""" +
            """<a:graphic><a:graphicData uri="x"><pic:pic><pic:blipFill><a:blip r:embed="rId70"/></pic:blipFill></pic:pic></a:graphicData></a:graphic>""" +
            """</wp:inline></w:drawing></w:r></w:p>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document(body))
            .WithBinaryPart("word/media/image1.png", OoxmlTestPackageBuilder.PngPixel())
            .WithBinaryPart("word/media/osierocony.png", OoxmlTestPackageBuilder.PngPixel())
            .WithRawRelationshipsPart("word/_rels/document.xml.rels", relationships)
            .Build();
    }

    public static byte[] MissingMainDocumentRelationship()
    {
        const string rootRelationships =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>""";

        return new OoxmlTestPackageBuilder()
            .WithMainDocument(Document("""<w:p><w:r><w:t>X</w:t></w:r></w:p>"""))
            .WithRawRelationshipsPart("_rels/.rels", rootRelationships)
            .Build();
    }
}
