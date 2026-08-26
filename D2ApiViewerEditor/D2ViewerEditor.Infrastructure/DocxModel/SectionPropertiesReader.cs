using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Domain.Models;
using OoxmlPageSize = DocumentFormat.OpenXml.Wordprocessing.PageSize;

namespace D2ViewerEditor.Infrastructure.DocxModel;

public static class SectionPropertiesReader
{
    public static PageSettings ReadPageSettings(SectionProperties? sectPr)
    {
        var pageSize = sectPr?.GetFirstChild<OoxmlPageSize>();
        var pageMargin = sectPr?.GetFirstChild<PageMargin>();
        var docGrid = sectPr?.GetFirstChild<DocGrid>();

        var orientation = pageSize?.Orient?.Value == PageOrientationValues.Landscape
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;

        return new PageSettings
        {
            PageWidthTwips = pageSize?.Width?.Value is { } w ? (int)w : null,
            PageHeightTwips = pageSize?.Height?.Value is { } h ? (int)h : null,
            Orientation = orientation,
            HasPageMargin = pageMargin != null,
            TopMarginTwips = pageMargin?.Top?.Value,
            BottomMarginTwips = pageMargin?.Bottom?.Value,
            LeftMarginTwips = pageMargin?.Left?.Value is { } l ? (int)l : null,
            RightMarginTwips = pageMargin?.Right?.Value is { } r ? (int)r : null,
            HeaderDistanceTwips = pageMargin?.Header?.Value is { } hd ? (int)hd : null,
            FooterDistanceTwips = pageMargin?.Footer?.Value is { } fd ? (int)fd : null,
            Columns = ReadColumns(sectPr?.GetFirstChild<Columns>()),
            DocGridType = docGrid == null ? null : DocGridTypeName(docGrid.Type?.Value),
            DocGridLinePitchTwips = docGrid?.LinePitch?.Value,
            DocGridCharSpace = docGrid?.CharacterSpace?.Value,
        };
    }

    private static string DocGridTypeName(DocGridValues? type)
    {
        if (type == DocGridValues.Lines) return "lines";
        if (type == DocGridValues.LinesAndChars) return "linesAndChars";
        if (type == DocGridValues.SnapToChars) return "snapToChars";
        return "default";
    }

    private static ColumnLayout? ReadColumns(Columns? cols)
    {
        if (cols == null) return null;

        var equalWidth = cols.EqualWidth?.Value ?? true;

        var colChildren = cols.Elements<Column>().ToList();

        int count = cols.ColumnCount?.Value
            ?? (colChildren.Count > 0 ? colChildren.Count : 1);

        var layout = new ColumnLayout
        {
            Count = count < 1 ? 1 : count,
            EqualWidth = equalWidth,
            SpaceTwips = cols.Space?.Value is { } sp && int.TryParse(sp, out var spi) ? spi : 720,
            Separator = cols.Separator?.Value ?? false,
        };

        if (!equalWidth && colChildren.Count > 0)
        {
            layout.Columns = colChildren.Select(c => new SectionColumn
            {
                WidthTwips = c.Width?.Value is { } w && int.TryParse(w, out var wi) ? wi : 0,
                SpaceTwips = c.Space?.Value is { } s && int.TryParse(s, out var si) ? si : 0,
            }).ToList();
        }

        return layout;
    }
}
