namespace D2ViewerEditor.Domain.Models;

public enum DifferenceKind
{
    PartOnlyInLeft = 0,
    PartOnlyInRight = 1,
    BinaryPartChanged = 2,
    ContentTypeChanged = 3,
    ElementOnlyInLeft = 4,
    ElementOnlyInRight = 5,
    ElementNameChanged = 6,
    AttributeOnlyInLeft = 7,
    AttributeOnlyInRight = 8,
    AttributeValueChanged = 9,
    TextChanged = 10,

    ElementMoved = 11,

    PartRenamed = 12
}

public enum ComparedPartStatus
{
    Identical = 0,
    Changed = 1,
    OnlyInLeft = 2,
    OnlyInRight = 3,
    Unreadable = 4
}

public enum DifferenceCause
{
    Unknown = 0,

    WordNoise = 1,

    IdentifierRewrite = 2,

    PairingArtifact = 3,

    WriterNormalization = 4,

    PipelineRegenerated = 5,

    PipelinePartial = 6,

    PipelineUnsupported = 7,

    UserEdit = 8,

    UserEditOrLoss = 9
}

public enum DifferenceImpact
{
    None = 0,

    Cosmetic = 1,

    Layout = 2,

    PdfDifference = 3,

    WordRepair = 4,

    DataLoss = 5
}

public sealed record DifferenceAnalysis(
    DifferenceCause Cause,
    DifferenceImpact Impact,
    string Explanation,
    string? FeatureKey,
    string? CodePointer);

public sealed record DocumentDifference(
    DifferenceKind Kind,
    string Category,
    string PartPath,
    string? LeftPath,
    string? RightPath,
    int? LeftLine,
    int? RightLine,
    string? Name,
    string? LeftValue,
    string? RightValue,
    string? LeftExcerpt,
    string? RightExcerpt,
    bool ExcerptTruncated,
    string? LeftContext,
    string? RightContext,
    DifferenceAnalysis? Analysis = null);

public sealed record ComparedPart(
    string Path,
    ComparedPartStatus Status,
    bool IsXml,
    int DifferenceCount,
    long? LeftSize,
    long? RightSize,
    int? LeftElementCount,
    int? RightElementCount,
    string? Note);

public sealed record ComparedFile(string FileName, long SizeInBytes, string DetectedFormat, int PartCount, IReadOnlyList<string> Notes);

public sealed class DocumentComparisonReport
{
    public required ComparedFile Left { get; init; }
    public required ComparedFile Right { get; init; }
    public required bool IgnoreRevisionIds { get; init; }
    public required bool IgnoreDocumentProperties { get; init; }
    public required bool Identical { get; init; }
    public required int TotalDifferences { get; init; }
    public required bool Truncated { get; init; }
    public required int IgnoredAttributeCount { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByKind { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByCategory { get; init; }

    public IReadOnlyDictionary<string, int> CountsByCause { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> CountsByImpact { get; init; } = new Dictionary<string, int>();

    public required IReadOnlyList<ComparedPart> Parts { get; init; }
    public required IReadOnlyList<DocumentDifference> Differences { get; init; }
    public required DateTimeOffset ComparedAtUtc { get; init; }
    public required long DurationMs { get; init; }
}
