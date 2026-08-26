using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.DocxModel;

public enum PageOrientation
{
    Portrait,
    Landscape
}

public sealed class PageSettings
{
    public int? PageWidthTwips { get; init; }
    public int? PageHeightTwips { get; init; }
    public PageOrientation Orientation { get; init; } = PageOrientation.Portrait;

    public bool HasPageMargin { get; init; }

    public int? TopMarginTwips { get; init; }
    public int? BottomMarginTwips { get; init; }
    public int? LeftMarginTwips { get; init; }
    public int? RightMarginTwips { get; init; }
    public int? HeaderDistanceTwips { get; init; }
    public int? FooterDistanceTwips { get; init; }

    public ColumnLayout? Columns { get; init; }

    public string? DocGridType { get; init; }
    public int? DocGridLinePitchTwips { get; init; }
    public int? DocGridCharSpace { get; init; }
}
