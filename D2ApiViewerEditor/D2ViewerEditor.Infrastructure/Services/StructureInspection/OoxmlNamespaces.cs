namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public static class OoxmlNamespaces
{
    public const string WordprocessingTransitional = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string WordprocessingStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    public const string WordprocessingDrawingTransitional = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public const string WordprocessingDrawingStrict = "http://purl.oclc.org/ooxml/drawingml/wordprocessingDrawing";

    public const string DrawingMainTransitional = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string DrawingMainStrict = "http://purl.oclc.org/ooxml/drawingml/main";

    public const string DrawingPictureTransitional = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public const string DrawingPictureStrict = "http://purl.oclc.org/ooxml/drawingml/picture";

    public const string OfficeRelationshipsTransitional = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string OfficeRelationshipsStrict = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    public const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public const string Vml = "urn:schemas-microsoft-com:vml";
    public const string VmlOffice = "urn:schemas-microsoft-com:office:office";
    public const string VmlWord = "urn:schemas-microsoft-com:office:word";

    public const string MainDocumentContentTypeFragment = "wordprocessingml.document.main+xml";

    public static bool IsWordprocessing(string namespaceUri) =>
        namespaceUri is WordprocessingTransitional or WordprocessingStrict;

    public static bool IsWordprocessingDrawing(string namespaceUri) =>
        namespaceUri is WordprocessingDrawingTransitional or WordprocessingDrawingStrict;

    public static bool IsDrawingMain(string namespaceUri) =>
        namespaceUri is DrawingMainTransitional or DrawingMainStrict;

    public static bool IsDrawingPicture(string namespaceUri) =>
        namespaceUri is DrawingPictureTransitional or DrawingPictureStrict;

    public static bool IsOfficeRelationship(string namespaceUri) =>
        namespaceUri is OfficeRelationshipsTransitional or OfficeRelationshipsStrict;

    public static bool IsVml(string namespaceUri) =>
        namespaceUri is Vml or VmlOffice or VmlWord;

    public static bool IsRelationshipType(string relationshipType, string suffix) =>
        relationshipType.EndsWith($"/{suffix}", StringComparison.OrdinalIgnoreCase);

    public static bool IsOfficeDocumentRelationshipType(string relationshipType) =>
        IsRelationshipType(relationshipType, "officeDocument");
}
