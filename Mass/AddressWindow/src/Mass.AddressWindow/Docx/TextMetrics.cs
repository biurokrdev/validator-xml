namespace Mass.AddressWindow.Docx;

internal sealed record TextMeasurement(RectangleMm? TextRect, double TotalHeight, IReadOnlyList<double> ParagraphTops);

internal sealed class TextMetrics(double averageCharWidthEm, double lineHeightFactor)
{
    public const double Unbounded = 100_000;

    public double LineHeightMm(ParagraphProps props, double fontSizePt)
    {
        var natural = Units.PtToMm(fontSizePt) * lineHeightFactor;
        return props.LineRule switch
        {
            "exact" => props.LineValue,
            "atLeast" => Math.Max(natural, props.LineValue),
            _ => natural * props.LineValue,
        };
    }

    public double TextWidthMm(LineContent line)
    {
        var text = line.Text.TrimEnd();
        if (text.Length == 0)
        {
            return 0;
        }

        var size = line.FontSizes.Count > 0 ? line.FontSizes.Average() : 10;
        return text.Length * averageCharWidthEm * Units.PtToMm(size);
    }

    public TextMeasurement Measure(IReadOnlyList<ParagraphContent> paragraphs, double widthMm)
    {
        RectangleMm? textRect = null;
        var tops = new List<double>(paragraphs.Count);
        var y = 0.0;

        foreach (var paragraph in paragraphs)
        {
            tops.Add(y);
            var props = paragraph.Props;
            y += props.SpaceBeforeMm;

            var available = Math.Max(5, widthMm - props.IndentLeftMm - props.IndentRightMm);
            foreach (var line in paragraph.Lines)
            {
                var size = line.FontSizes.Count > 0 ? line.FontSizes.Max() : paragraph.MaxFontSizePt;
                var lineHeight = LineHeightMm(props, size);
                var width = TextWidthMm(line);
                line.VisualLines = width <= available ? 1 : (int)Math.Ceiling(width / available);

                if (line.HasVisibleText)
                {
                    var used = Math.Min(width, available);
                    var offset = props.Alignment switch
                    {
                        Alignment.Center => (available - used) / 2,
                        Alignment.Right => available - used,
                        _ => 0,
                    };
                    var rect = new RectangleMm(props.IndentLeftMm + offset, y, used, lineHeight * line.VisualLines);
                    textRect = textRect is null ? rect : textRect.Value.Union(rect);
                }

                y += lineHeight * line.VisualLines;
            }

            y += props.SpaceAfterMm;
        }

        return new TextMeasurement(textRect, y, tops);
    }

    public double NaturalWidthMm(IReadOnlyList<ParagraphContent> paragraphs) => paragraphs
        .SelectMany(p => p.Lines.Select(l => p.Props.IndentLeftMm + TextWidthMm(l) + p.Props.IndentRightMm))
        .DefaultIfEmpty(0)
        .Max();
}
