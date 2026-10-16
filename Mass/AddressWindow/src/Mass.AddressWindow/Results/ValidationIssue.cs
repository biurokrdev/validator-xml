namespace Mass.AddressWindow;

public enum IssueSeverity
{
    Info,

    Warning,

    Error,

    /// <summary>Nie da się wiarygodnie rozstrzygnąć (skan bez tekstu, nieczytelna grafika); to nie jest błąd dokumentu.</summary>
    Inconclusive,
}

public sealed record ValidationIssue(string Code, IssueSeverity Severity, string Message, WindowRole? Window = null);

public static class IssueCodes
{
    public const string InvalidDocument = "INVALID_DOCUMENT";

    public const string UnexpectedPageFormat = "UNEXPECTED_PAGE_FORMAT";

    public const string NoTextLayer = "NO_TEXT_LAYER";

    public const string WindowNotFound = "WINDOW_NOT_FOUND";

    public const string AddressNotFound = "ADDRESS_NOT_FOUND";

    public const string AddressMismatch = "ADDRESS_MISMATCH";

    public const string AddressOutsideWindow = "ADDRESS_OUTSIDE_WINDOW";

    public const string AddressTooCloseToEdge = "ADDRESS_TOO_CLOSE_TO_EDGE";

    public const string TextRotated = "TEXT_ROTATED";

    public const string OtherTextInWindow = "OTHER_TEXT_IN_WINDOW";

    public const string AddressEmpty = "ADDRESS_EMPTY";

    public const string TooFewLines = "TOO_FEW_LINES";

    public const string TooManyLines = "TOO_MANY_LINES";

    public const string LineTooLong = "LINE_TOO_LONG";

    public const string EmptyLineInside = "EMPTY_LINE_INSIDE";

    public const string PostalCodeLineMissing = "POSTAL_CODE_LINE_MISSING";

    public const string TextAfterPostalCodeLine = "TEXT_AFTER_POSTAL_CODE_LINE";

    public const string FontTooSmall = "FONT_TOO_SMALL";

    public const string FontTooLarge = "FONT_TOO_LARGE";

    public const string DecoratedText = "DECORATED_TEXT";

    public const string NonLeftAlignment = "NON_LEFT_ALIGNMENT";

    public const string UnusualCharacters = "UNUSUAL_CHARACTERS";

    public const string MergeFieldsPresent = "MERGE_FIELDS_PRESENT";

    public const string LabelNotFound = "LABEL_NOT_FOUND";

    public const string LabelOutsideWindow = "LABEL_OUTSIDE_WINDOW";

    public const string LabelTooCloseToEdge = "LABEL_TOO_CLOSE_TO_EDGE";

    public const string LabelTooLarge = "LABEL_TOO_LARGE";

    public const string LabelUnreadable = "LABEL_UNREADABLE";

    public const string LabelNumberInvalid = "LABEL_NUMBER_INVALID";

    public const string LabelTypeNotAllowed = "LABEL_TYPE_NOT_ALLOWED";

    public const string LabelNumberMismatch = "LABEL_NUMBER_MISMATCH";

    public const string LabelBarcodeNotGs1 = "LABEL_BARCODE_NOT_GS1";

    public const string LabelLowResolution = "LABEL_LOW_RESOLUTION";
}
