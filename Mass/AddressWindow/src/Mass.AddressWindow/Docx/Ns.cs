using System.Xml.Linq;

namespace Mass.AddressWindow.Docx;

internal static class Ns
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public static readonly XNamespace WP14 = "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    public static readonly XNamespace WPG = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    public static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    public static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    public static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static readonly IReadOnlyDictionary<string, XNamespace> StrictToTransitional = new Dictionary<string, XNamespace>
    {
        ["http://purl.oclc.org/ooxml/wordprocessingml/main"] = W,
        ["http://purl.oclc.org/ooxml/officeDocument/relationships"] = R,
        ["http://purl.oclc.org/ooxml/drawingml/main"] = A,
        ["http://purl.oclc.org/ooxml/drawingml/wordprocessingDrawing"] = WP,
    };
}
