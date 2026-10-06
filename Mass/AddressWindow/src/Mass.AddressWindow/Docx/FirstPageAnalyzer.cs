using System.Xml.Linq;
using Mass.AddressWindow.Rules;

namespace Mass.AddressWindow.Docx;

internal sealed class FirstPageAnalyzer
{
    private const double DefaultCellMarginLeftRight = 108 / Units.TwipsPerMm;

    private readonly DocxPackage _package;
    private readonly TextExtractor _extractor;
    private readonly TextMetrics _metrics;

    public FirstPageAnalyzer(DocxPackage package, StyleResolver styles, TextMetrics metrics)
    {
        _package = package;
        _extractor = new TextExtractor(styles);
        _metrics = metrics;
        Page = PageSetup.FromBody(package.Body);
    }

    public PageSetup Page { get; }

    /// <summary>Grafiki z pierwszej strony; wypełniane przez <see cref="Analyze"/>.</summary>
    public IReadOnlyList<ImageCandidate> Images => _images;

    private readonly List<ImageCandidate> _images = [];

    public IReadOnlyList<TextBlockCandidate> Analyze()
    {
        var result = new List<TextBlockCandidate>();
        _images.Clear();

        if (Page.FirstPageHeaderRelId is { } headerId && _package.GetHeader(headerId)?.Root is { } header)
        {
            new PartScan(this, header, DocumentPartKind.Header, Page.HeaderDistance, result).Run();
        }

        new PartScan(this, _package.Body, DocumentPartKind.Body, Page.MarginTop, result).Run();
        return result;
    }

    private TextBlockCandidate? Build(
        AddressSourceKind kind,
        DocumentPartKind part,
        RectangleMm bounds,
        RectangleMm contentArea,
        IReadOnlyList<ParagraphContent> paragraphs,
        VerticalAnchor anchor = VerticalAnchor.Top,
        bool fixedHeight = false,
        bool estimated = false,
        bool noWrap = false,
        double rotation = 0,
        bool vertical = false,
        string? name = null,
        params string?[] extraIdentifiers)
    {
        if (!paragraphs.Any(p => p.HasVisibleText))
        {
            return null;
        }

        var measurement = _metrics.Measure(paragraphs, noWrap ? TextMetrics.Unbounded : contentArea.Width);
        var overflow = 0.0;
        var dy = 0.0;
        if (measurement.TotalHeight > contentArea.Height && !fixedHeight)
        {
            var grow = measurement.TotalHeight - contentArea.Height;
            contentArea = contentArea with { Height = measurement.TotalHeight };
            bounds = bounds with { Height = bounds.Height + grow };
        }
        else
        {
            overflow = Math.Max(0, measurement.TotalHeight - contentArea.Height);
            var free = contentArea.Height - measurement.TotalHeight;
            dy = anchor switch
            {
                VerticalAnchor.Center => free / 2,
                VerticalAnchor.Bottom => free,
                _ => 0,
            };
        }

        var text = measurement.TextRect!.Value.Offset(contentArea.Left, contentArea.Top + dy);

        return new TextBlockCandidate
        {
            Kind = kind,
            Part = part,
            Name = name,
            Identifiers = CollectIdentifiers(paragraphs, [name, .. extraIdentifiers]),
            Bounds = bounds,
            TextBounds = text,
            OverflowMm = overflow,
            Estimated = estimated,
            RotationDeg = rotation,
            VerticalText = vertical,
            Paragraphs = paragraphs,
        };
    }

    private static IReadOnlyCollection<string> CollectIdentifiers(IEnumerable<ParagraphContent> paragraphs, IEnumerable<string?> extra)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in extra)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        foreach (var paragraph in paragraphs)
        {
            var sdts = paragraph.Source.Ancestors(Ns.W + "sdt").Concat(paragraph.Source.Descendants(Ns.W + "sdt"));
            foreach (var sdtPr in sdts.Select(s => s.Element(Ns.W + "sdtPr")).OfType<XElement>())
            {
                foreach (var property in new[] { "tag", "alias" })
                {
                    if ((string?)sdtPr.Element(Ns.W + property)?.Attribute(Ns.W + "val") is { Length: > 0 } value)
                    {
                        ids.Add(value);
                    }
                }
            }

            foreach (var bookmark in paragraph.Source.Descendants(Ns.W + "bookmarkStart"))
            {
                if ((string?)bookmark.Attribute(Ns.W + "name") is { Length: > 0 } value && !value.StartsWith('_'))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private sealed class PartScan(
        FirstPageAnalyzer owner,
        XElement root,
        DocumentPartKind part,
        double startY,
        List<TextBlockCandidate> output)
    {
        private readonly Dictionary<XElement, double> _paragraphTops = [];
        private readonly List<ParagraphContent> _flow = [];
        private readonly List<ParagraphContent> _frame = [];
        private readonly List<Obstacle> _obstacles = [];
        private double _flowTop;
        private double _flowLeft;
        private double _flowWidth;
        private double _frameTop;
        private readonly double _startY = startY;
        private double _y = startY;
        private bool _stopped;

        private PageSetup Page => owner.Page;

        private bool IsBody => part == DocumentPartKind.Body;

        public void Run()
        {
            CollectPageAnchoredObstacles();
            ProcessBlocks(root.Elements());
            FlushFlow();
            FlushFrame();
            ProcessDrawings();
            ProcessVmlShapes();
        }

        private void Add(TextBlockCandidate? candidate)
        {
            if (candidate is not null)
            {
                output.Add(candidate);
            }
        }

        private void Stop()
        {
            FlushFlow();
            FlushFrame();
            _stopped = true;
        }


        private void ProcessBlocks(IEnumerable<XElement> blocks)
        {
            foreach (var block in blocks)
            {
                if (_stopped)
                {
                    return;
                }

                if (block.Name == Ns.W + "p")
                {
                    ProcessParagraph(block);
                }
                else if (block.Name == Ns.W + "tbl")
                {
                    FlushFlow();
                    FlushFrame();
                    ProcessTable(block);
                }
                else if (block.Name == Ns.W + "sdt")
                {
                    ProcessBlocks(block.Element(Ns.W + "sdtContent")?.Elements() ?? []);
                }
                else if (block.Name == Ns.W + "customXml")
                {
                    ProcessBlocks(block.Elements());
                }
            }
        }

        private void ProcessParagraph(XElement paragraph)
        {
            var content = owner._extractor.Read(paragraph);
            var props = content.Props;
            var pageBreak = FindPageBreak(paragraph);

            if (IsBody && (pageBreak == PageBreak.BeforeText || (props.PageBreakBefore && _y > _startY + 0.1)))
            {
                Stop();
                return;
            }

            if (props.FramePr is { } framePr)
            {
                FlushFlow();
                if (_frame.Count > 0 && FrameSignature(_frame[0].Props.FramePr!) != FrameSignature(framePr))
                {
                    FlushFrame();
                }

                if (_frame.Count == 0)
                {
                    _frameTop = _y;
                }

                _paragraphTops[paragraph] = _y;
                _frame.Add(content);
                return;
            }

            FlushFrame();
            var (top, left, width, height) = Place(content, _y);
            _paragraphTops[paragraph] = top;

            if (content.HasVisibleText)
            {
                var moved = top > _y + 0.01 || Math.Abs(left - _flowLeft) > 0.01 || Math.Abs(width - _flowWidth) > 0.01;
                if (_flow.Count > 0 && (moved || StartsNewBlock(_flow[^1], content)))
                {
                    FlushFlow();
                }

                if (_flow.Count == 0)
                {
                    (_flowTop, _flowLeft, _flowWidth) = (top, left, width);
                }

                _flow.Add(content);
            }
            else
            {
                FlushFlow();
            }

            _y = top + height;

            var sectionEnds = paragraph.Element(Ns.W + "pPr")?.Element(Ns.W + "sectPr") is not null;
            if (IsBody && (pageBreak == PageBreak.AfterText || sectionEnds || _y > Page.ContentBottom))
            {
                Stop();
            }
        }

        private bool StartsNewBlock(ParagraphContent previous, ParagraphContent next)
        {
            var gap = previous.Props.SpaceAfterMm + next.Props.SpaceBeforeMm;
            if (gap > owner._metrics.LineHeightMm(next.Props, next.MaxFontSizePt))
            {
                return true;
            }

            static bool RightSide(ParagraphContent p) => p.Props.Alignment is Alignment.Right or Alignment.Center;
            return RightSide(previous) != RightSide(next);
        }

        private void FlushFlow()
        {
            if (_flow.Count == 0)
            {
                return;
            }

            var paragraphs = _flow.ToList();
            _flow.Clear();
            var area = new RectangleMm(_flowLeft, _flowTop, _flowWidth, 0);
            Add(owner.Build(AddressSourceKind.Paragraphs, part, area, area, paragraphs, estimated: true));
        }

        private readonly record struct Obstacle(RectangleMm Area, bool SideBySide);

        private (double Top, double Left, double Width, double Height) Place(ParagraphContent content, double y)
        {
            const double minimumWidth = 20;
            var left = Page.ContentLeft;
            var right = Page.ContentLeft + Page.ContentWidth;
            var height = owner._metrics.Measure([content], right - left).TotalHeight;

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var changed = false;
                foreach (var obstacle in _obstacles)
                {
                    var area = obstacle.Area;
                    var textLeft = left + content.Props.IndentLeftMm;
                    var textRight = right - content.Props.IndentRightMm;
                    var overlaps = area.Top < y + height && area.Bottom > y && area.Left < textRight && area.Right > textLeft;
                    if (!overlaps)
                    {
                        continue;
                    }

                    if (obstacle.SideBySide)
                    {
                        if (area.Left + area.Width / 2 < (left + right) / 2)
                        {
                            left = Math.Max(left, area.Right);
                        }
                        else
                        {
                            right = Math.Min(right, area.Left);
                        }
                    }

                    if (!obstacle.SideBySide || right - left < minimumWidth)
                    {
                        y = area.Bottom;
                        left = Page.ContentLeft;
                        right = Page.ContentLeft + Page.ContentWidth;
                    }

                    height = owner._metrics.Measure([content], right - left).TotalHeight;
                    changed = true;
                    break;
                }

                if (!changed)
                {
                    break;
                }
            }

            return (y, left, right - left, height);
        }

        private void CollectPageAnchoredObstacles()
        {
            if (!IsBody)
            {
                return;
            }

            foreach (var anchor in root.Descendants(Ns.WP + "anchor").Where(a => !a.Ancestors(Ns.MC + "Fallback").Any()))
            {
                var wrap = anchor.Elements()
                    .FirstOrDefault(e => e.Name.Namespace == Ns.WP && e.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal))
                    ?.Name.LocalName;
                if (wrap is null or "wrapNone")
                {
                    continue;
                }

                var extent = anchor.Element(Ns.WP + "extent");
                var width = Units.EmuAttrToMm(extent, "cx") ?? 0;
                var height = Units.EmuAttrToMm(extent, "cy") ?? 0;
                var (x, estimatedX) = ResolveAnchorAxis(anchor.Element(Ns.WP + "positionH"), width, horizontal: true, _startY);
                var (y, estimatedY) = ResolveAnchorAxis(anchor.Element(Ns.WP + "positionV"), height, horizontal: false, _startY);
                if (estimatedX || estimatedY)
                {
                    continue;
                }

                var area = RectangleMm.FromEdges(
                    x - (Units.EmuAttrToMm(anchor, "distL") ?? 0),
                    y - (Units.EmuAttrToMm(anchor, "distT") ?? 0),
                    x + width + (Units.EmuAttrToMm(anchor, "distR") ?? 0),
                    y + height + (Units.EmuAttrToMm(anchor, "distB") ?? 0));
                _obstacles.Add(new Obstacle(area, SideBySide: wrap != "wrapTopAndBottom"));
            }
        }

        private void FlushFrame()
        {
            if (_frame.Count == 0)
            {
                return;
            }

            var paragraphs = _frame.ToList();
            _frame.Clear();
            var framePr = paragraphs[0].Props.FramePr!;

            var width = Units.TwipsAttrToMm(framePr, Ns.W + "w");
            var height = Units.TwipsAttrToMm(framePr, Ns.W + "h");
            var hRule = (string?)framePr.Attribute(Ns.W + "hRule") ?? (height is null ? "auto" : "atLeast");
            var frameWidth = width ?? Math.Min(owner._metrics.NaturalWidthMm(paragraphs) + 0.5, Page.ContentWidth);

            var (hStart, hEnd, _) = Page.Horizontal((string?)framePr.Attribute(Ns.W + "hAnchor") switch
            {
                "page" => "page",
                _ => "margin",
            });
            var x = Position(
                hStart, hEnd, frameWidth,
                Units.TwipsAttrToMm(framePr, Ns.W + "x"),
                (string?)framePr.Attribute(Ns.W + "xAlign"));

            var vAnchor = (string?)framePr.Attribute(Ns.W + "vAnchor") ?? "margin";
            var yAlign = (string?)framePr.Attribute(Ns.W + "yAlign");
            var estimated = vAnchor == "text" || yAlign == "inline" || width is null;
            var (vStart, vEnd) = yAlign == "inline" ? (_frameTop, _frameTop) : Page.Vertical(vAnchor) ?? (_frameTop, _frameTop);

            var measured = owner._metrics.Measure(paragraphs, frameWidth).TotalHeight;
            var frameHeight = hRule == "exact" ? height ?? measured : Math.Max(height ?? 0, measured);
            var y = Position(vStart, vEnd, frameHeight, Units.TwipsAttrToMm(framePr, Ns.W + "y"), yAlign == "inline" ? null : yAlign);

            var bounds = new RectangleMm(x, y, frameWidth, frameHeight);
            var wrap = (string?)framePr.Attribute(Ns.W + "wrap") ?? "auto";
            if (wrap != "none")
            {
                var hSpace = Units.TwipsAttrToMm(framePr, Ns.W + "hSpace") ?? 0;
                var vSpace = Units.TwipsAttrToMm(framePr, Ns.W + "vSpace") ?? 0;
                _obstacles.Add(new Obstacle(
                    RectangleMm.FromEdges(x - hSpace, y - vSpace, x + frameWidth + hSpace, y + frameHeight + vSpace),
                    SideBySide: wrap != "notBeside"));
            }

            Add(owner.Build(
                AddressSourceKind.Frame, part, bounds, bounds, paragraphs,
                fixedHeight: hRule == "exact", estimated: estimated));
        }

        private static string FrameSignature(XElement framePr) =>
            string.Join('|', framePr.Attributes().OrderBy(a => a.Name.ToString()).Select(a => $"{a.Name.LocalName}={a.Value}"));

        private void ProcessTable(XElement table)
        {
            var tblPr = table.Element(Ns.W + "tblPr");
            var tblpPr = tblPr?.Element(Ns.W + "tblpPr");
            var grid = table.Element(Ns.W + "tblGrid")?.Elements(Ns.W + "gridCol")
                .Select(g => Units.TwipsAttrToMm(g, Ns.W + "w") ?? 0)
                .ToList() ?? [];
            var tableWidth = grid.Sum();

            double x, y;
            bool estimated;
            if (tblpPr is null)
            {
                x = Page.ContentLeft + (Units.TwipsAttrToMm(tblPr?.Element(Ns.W + "tblInd"), Ns.W + "w") ?? 0);
                y = _y;
                estimated = true;
            }
            else
            {
                var (hStart, hEnd, _) = Page.Horizontal((string?)tblpPr.Attribute(Ns.W + "horzAnchor") == "page" ? "page" : "margin");
                x = Position(hStart, hEnd, tableWidth,
                    Units.TwipsAttrToMm(tblpPr, Ns.W + "tblpX"), (string?)tblpPr.Attribute(Ns.W + "tblpXSpec"));

                var vertAnchor = (string?)tblpPr.Attribute(Ns.W + "vertAnchor") ?? "margin";
                var (vStart, vEnd) = Page.Vertical(vertAnchor) ?? (_y, _y);
                estimated = vertAnchor == "text";
                y = Position(vStart, vEnd, 0,
                    Units.TwipsAttrToMm(tblpPr, Ns.W + "tblpY"), (string?)tblpPr.Attribute(Ns.W + "tblpYSpec"));
            }

            var height = LayoutTable(table, grid, x, y, estimated);
            if (tblpPr is null)
            {
                _y += height;
                if (IsBody && _y > Page.ContentBottom)
                {
                    Stop();
                }
            }
        }

        private double LayoutTable(XElement table, IReadOnlyList<double> grid, double x0, double y0, bool estimated)
        {
            var cellMar = table.Element(Ns.W + "tblPr")?.Element(Ns.W + "tblCellMar");
            var defaultMargins = new CellMargins(
                Margin(cellMar, DefaultCellMarginLeftRight, "left", "start"),
                Margin(cellMar, 0, "top"),
                Margin(cellMar, DefaultCellMarginLeftRight, "right", "end"),
                Margin(cellMar, 0, "bottom"));

            var y = y0;
            var rowsAboveEstimated = estimated;
            foreach (var row in Unwrap(table.Elements(), Ns.W + "tr"))
            {
                var trPr = row.Element(Ns.W + "trPr");
                var column = (int)(Units.ParseDouble((string?)trPr?.Element(Ns.W + "gridBefore")?.Attribute(Ns.W + "val")) ?? 0);
                var cells = new List<(RectangleMm Column, CellMargins Margins, List<ParagraphContent> Paragraphs, TextMeasurement Size, VerticalAnchor Anchor)>();
                var needed = 0.0;

                foreach (var cell in Unwrap(row.Elements(), Ns.W + "tc"))
                {
                    var tcPr = cell.Element(Ns.W + "tcPr");
                    var span = Math.Max(1, (int)(Units.ParseDouble((string?)tcPr?.Element(Ns.W + "gridSpan")?.Attribute(Ns.W + "val")) ?? 1));
                    var cellX = x0 + grid.Take(column).Sum();
                    var cellWidth = grid.Count >= column + span
                        ? grid.Skip(column).Take(span).Sum()
                        : Units.TwipsAttrToMm(tcPr?.Element(Ns.W + "tcW"), Ns.W + "w") ?? 30;
                    column += span;

                    var vMerge = tcPr?.Element(Ns.W + "vMerge");
                    var continuation = vMerge is not null && (string?)vMerge.Attribute(Ns.W + "val") != "restart";

                    var tcMar = tcPr?.Element(Ns.W + "tcMar");
                    var margins = new CellMargins(
                        Margin(tcMar, defaultMargins.Left, "left", "start"),
                        Margin(tcMar, defaultMargins.Top, "top"),
                        Margin(tcMar, defaultMargins.Right, "right", "end"),
                        Margin(tcMar, defaultMargins.Bottom, "bottom"));

                    var paragraphs = continuation ? [] : owner._extractor.Read(CellParagraphs(cell)).ToList();
                    var size = owner._metrics.Measure(paragraphs, Math.Max(5, cellWidth - margins.Left - margins.Right));
                    needed = Math.Max(needed, size.TotalHeight + margins.Top + margins.Bottom);

                    var anchor = (string?)tcPr?.Element(Ns.W + "vAlign")?.Attribute(Ns.W + "val") switch
                    {
                        "center" => VerticalAnchor.Center,
                        "bottom" => VerticalAnchor.Bottom,
                        _ => VerticalAnchor.Top,
                    };
                    cells.Add((new RectangleMm(cellX, 0, cellWidth, 0), margins, paragraphs, size, anchor));
                }

                var trHeight = trPr?.Element(Ns.W + "trHeight");
                var declared = Units.TwipsAttrToMm(trHeight, Ns.W + "val") ?? 0;
                var exact = (string?)trHeight?.Attribute(Ns.W + "hRule") == "exact";
                var rowHeight = exact ? declared : Math.Max(declared, needed);

                foreach (var (columnRect, margins, paragraphs, size, anchor) in cells)
                {
                    for (var i = 0; i < paragraphs.Count; i++)
                    {
                        _paragraphTops[paragraphs[i].Source] = y + margins.Top + size.ParagraphTops[i];
                    }

                    var bounds = columnRect with { Top = y, Height = rowHeight };
                    var content = RectangleMm.FromEdges(
                        bounds.Left + margins.Left, bounds.Top + margins.Top,
                        bounds.Right - margins.Right, bounds.Bottom - margins.Bottom);
                    Add(owner.Build(
                        AddressSourceKind.TableCell, part, bounds, content, paragraphs,
                        anchor, fixedHeight: exact, estimated: rowsAboveEstimated || !exact && anchor != VerticalAnchor.Top));
                }

                rowsAboveEstimated |= !exact;
                y += rowHeight;
            }

            return y - y0;
        }

        private readonly record struct CellMargins(double Left, double Top, double Right, double Bottom);

        private static double Margin(XElement? container, double fallback, params string[] names)
        {
            foreach (var name in names)
            {
                var element = container?.Element(Ns.W + name);
                if (element is not null && ((string?)element.Attribute(Ns.W + "type") ?? "dxa") == "dxa")
                {
                    return Units.TwipsAttrToMm(element, Ns.W + "w") ?? fallback;
                }
            }

            return fallback;
        }

        private static IEnumerable<XElement> Unwrap(IEnumerable<XElement> elements, XName wanted)
        {
            foreach (var element in elements)
            {
                if (element.Name == wanted)
                {
                    yield return element;
                }
                else if (element.Name == Ns.W + "sdt")
                {
                    foreach (var inner in Unwrap(element.Element(Ns.W + "sdtContent")?.Elements() ?? [], wanted))
                    {
                        yield return inner;
                    }
                }
                else if (element.Name == Ns.W + "customXml")
                {
                    foreach (var inner in Unwrap(element.Elements(), wanted))
                    {
                        yield return inner;
                    }
                }
            }
        }

        private static IEnumerable<XElement> CellParagraphs(XElement cell) => cell.Descendants(Ns.W + "p")
            .Where(p => !p.Ancestors().TakeWhile(a => a != cell).Any(a => a.Name == Ns.W + "txbxContent"));


        private void ProcessDrawings()
        {
            var drawings = root.Descendants()
                .Where(e => e.Name == Ns.WP + "anchor" || e.Name == Ns.WP + "inline")
                .Where(e => !e.Ancestors(Ns.MC + "Fallback").Any())
                .ToList();

            foreach (var drawing in drawings)
            {
                if (!TryGetAnchorParagraph(drawing, out var paragraph, out var paragraphTop))
                {
                    continue;
                }

                var extent = drawing.Element(Ns.WP + "extent");
                var width = Units.EmuAttrToMm(extent, "cx") ?? 0;
                var height = Units.EmuAttrToMm(extent, "cy") ?? 0;
                var docPr = drawing.Element(Ns.WP + "docPr");
                var names = new[] { (string?)docPr?.Attribute("name"), (string?)docPr?.Attribute("descr"), (string?)docPr?.Attribute("title") };

                double x, y;
                bool estimated;
                if (drawing.Name == Ns.WP + "inline")
                {
                    var props = paragraph is null ? null : owner._extractor.Read(paragraph).Props;
                    x = Page.ContentLeft + (props?.IndentLeftMm ?? 0);
                    y = paragraphTop + (props?.SpaceBeforeMm ?? 0);
                    estimated = true;
                }
                else
                {
                    (x, var estimatedX) = ResolveAnchorAxis(drawing.Element(Ns.WP + "positionH"), width, horizontal: true, paragraphTop);
                    (y, var estimatedY) = ResolveAnchorAxis(drawing.Element(Ns.WP + "positionV"), height, horizontal: false, paragraphTop);
                    estimated = estimatedX || estimatedY;
                }

                var rect = new RectangleMm(x, y, width, height);
                var graphicData = drawing.Element(Ns.A + "graphic")?.Element(Ns.A + "graphicData");
                foreach (var shape in graphicData?.Elements() ?? [])
                {
                    if (shape.Name == Ns.WPS + "wsp")
                    {
                        ProcessWordShape(shape, rect, estimated, names);
                    }
                    else if (shape.Name == Ns.WPG + "wgp")
                    {
                        ProcessGroup(shape, rect, estimated, names);
                    }
                    else if (shape.Name == Ns.PIC + "pic")
                    {
                        AddImage(rect, estimated, names);
                    }
                }
            }
        }

        private void AddImage(RectangleMm rect, bool estimated, string?[] names)
        {
            if (rect.Width > 0 && rect.Height > 0)
            {
                owner._images.Add(new ImageCandidate(rect, part, names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)), estimated));
            }
        }

        private bool TryGetAnchorParagraph(XElement drawing, out XElement? paragraph, out double top)
        {
            paragraph = drawing.Ancestors(Ns.W + "p").FirstOrDefault(p => _paragraphTops.ContainsKey(p));
            if (paragraph is not null)
            {
                top = _paragraphTops[paragraph];
                return true;
            }

            top = _startY;
            return !IsBody;
        }

        private (double Value, bool Estimated) ResolveAnchorAxis(XElement? position, double size, bool horizontal, double paragraphTop)
        {
            if (position is null)
            {
                return (horizontal ? Page.ContentLeft : paragraphTop, true);
            }

            var relativeFrom = (string?)position.Attribute("relativeFrom");
            double start, end;
            var estimated = false;
            if (horizontal)
            {
                (start, end, estimated) = Page.Horizontal(relativeFrom);
            }
            else if (Page.Vertical(relativeFrom) is { } range)
            {
                (start, end) = range;
            }
            else
            {
                (start, end, estimated) = (paragraphTop, paragraphTop, true);
            }

            var offsetEmu = Units.ParseDouble(position.Element(Ns.WP + "posOffset")?.Value);
            if (offsetEmu is not null)
            {
                return (start + Units.EmuToMm(offsetEmu.Value), estimated);
            }

            var percent = Units.ParseDouble(position.Element(Ns.WP14 + (horizontal ? "pctPosHOffset" : "pctPosVOffset"))?.Value);
            if (percent is not null)
            {
                return (start + (end - start) * percent.Value / 100_000.0, estimated);
            }

            return (Position(start, end, size, null, position.Element(Ns.WP + "align")?.Value), estimated);
        }

        private void ProcessGroup(XElement group, RectangleMm rect, bool estimated, string?[] names)
        {
            var xfrm = group.Element(Ns.WPG + "grpSpPr")?.Element(Ns.A + "xfrm");
            var childOffset = Point(xfrm?.Element(Ns.A + "chOff"), "x", "y") ?? Point(xfrm?.Element(Ns.A + "off"), "x", "y") ?? (0, 0);
            var childExtent = Point(xfrm?.Element(Ns.A + "chExt"), "cx", "cy") ?? Point(xfrm?.Element(Ns.A + "ext"), "cx", "cy");
            if (childExtent is not { } ext || ext.X <= 0 || ext.Y <= 0)
            {
                return;
            }

            var scaleX = rect.Width / ext.X;
            var scaleY = rect.Height / ext.Y;

            foreach (var child in group.Elements())
            {
                var isShape = child.Name == Ns.WPS + "wsp";
                var isGroup = child.Name == Ns.WPG + "grpSp";
                var isPicture = child.Name == Ns.PIC + "pic";
                if (!isShape && !isGroup && !isPicture)
                {
                    continue;
                }

                var properties = isShape ? child.Element(Ns.WPS + "spPr")
                    : isGroup ? child.Element(Ns.WPG + "grpSpPr")
                    : child.Element(Ns.PIC + "spPr");
                var childXfrm = properties?.Element(Ns.A + "xfrm");
                var offset = Point(childXfrm?.Element(Ns.A + "off"), "x", "y");
                var size = Point(childXfrm?.Element(Ns.A + "ext"), "cx", "cy");
                if (offset is null || size is null)
                {
                    continue;
                }

                var childRect = new RectangleMm(
                    rect.Left + (offset.Value.X - childOffset.X) * scaleX,
                    rect.Top + (offset.Value.Y - childOffset.Y) * scaleY,
                    size.Value.X * scaleX,
                    size.Value.Y * scaleY);

                if (isShape)
                {
                    ProcessWordShape(child, childRect, estimated, names);
                }
                else if (isPicture)
                {
                    AddImage(childRect, estimated, names);
                }
                else
                {
                    ProcessGroup(child, childRect, estimated, names);
                }
            }
        }

        private static (double X, double Y)? Point(XElement? element, string xName, string yName)
        {
            var x = Units.EmuAttrToMm(element, xName);
            var y = Units.EmuAttrToMm(element, yName);
            return x is null || y is null ? null : (x.Value, y.Value);
        }

        private void ProcessWordShape(XElement shape, RectangleMm rect, bool estimated, string?[] names)
        {
            var textbox = shape.Element(Ns.WPS + "txbx")?.Element(Ns.W + "txbxContent");
            if (textbox is null)
            {
                return;
            }

            var bodyPr = shape.Element(Ns.WPS + "bodyPr");
            var content = RectangleMm.FromEdges(
                rect.Left + (Units.EmuAttrToMm(bodyPr, "lIns") ?? 2.54),
                rect.Top + (Units.EmuAttrToMm(bodyPr, "tIns") ?? 1.27),
                rect.Right - (Units.EmuAttrToMm(bodyPr, "rIns") ?? 2.54),
                rect.Bottom - (Units.EmuAttrToMm(bodyPr, "bIns") ?? 1.27));

            var rotation = (Units.ParseDouble((string?)shape.Element(Ns.WPS + "spPr")?.Element(Ns.A + "xfrm")?.Attribute("rot")) ?? 0) / 60000
                + (Units.ParseDouble((string?)bodyPr?.Attribute("rot")) ?? 0) / 60000;
            var vert = (string?)bodyPr?.Attribute("vert");
            var anchor = (string?)bodyPr?.Attribute("anchor") switch
            {
                "ctr" => VerticalAnchor.Center,
                "b" => VerticalAnchor.Bottom,
                _ => VerticalAnchor.Top,
            };

            var shapeName = (string?)shape.Element(Ns.WPS + "cNvPr")?.Attribute("name");
            Add(owner.Build(
                AddressSourceKind.TextBox, part, rect, content, ReadTextbox(textbox),
                anchor,
                fixedHeight: bodyPr?.Element(Ns.A + "spAutoFit") is null,
                estimated: estimated,
                noWrap: (string?)bodyPr?.Attribute("wrap") == "none",
                rotation: rotation,
                vertical: vert is not null && vert != "horz",
                name: names[0],
                extraIdentifiers: [.. names, shapeName]));
        }

        private IReadOnlyList<ParagraphContent> ReadTextbox(XElement textbox) => owner._extractor.Read(
            textbox.Descendants(Ns.W + "p").Where(p => p.Ancestors(Ns.W + "txbxContent").First() == textbox));


        private void ProcessVmlShapes()
        {
            var shapes = root.Descendants()
                .Where(e => e.Name.Namespace == Ns.V && e.Element(Ns.V + "textbox") is not null)
                .Where(e => e.Parent?.Name != Ns.V + "group" && !e.Ancestors(Ns.MC + "Fallback").Any())
                .ToList();

            foreach (var shape in shapes)
            {
                var textbox = shape.Element(Ns.V + "textbox")!;
                var content = textbox.Element(Ns.W + "txbxContent");
                if (content is null || !TryGetAnchorParagraph(shape, out _, out var paragraphTop))
                {
                    continue;
                }

                var style = ParseStyle((string?)shape.Attribute("style"));
                var pxMm = 0.75 * Units.MmPerPt;
                var width = Length(style, "width", pxMm);
                var height = Length(style, "height", pxMm);

                var hRelative = style.GetValueOrDefault("mso-position-horizontal-relative", "text");
                var (hStart, hEnd, estimatedX) = Page.Horizontal(hRelative == "text" ? "margin" : hRelative);
                var left = Length(style, "margin-left", pxMm, Length(style, "left", pxMm));
                var x = Position(hStart, hEnd, width, left, AbsoluteToNull(style.GetValueOrDefault("mso-position-horizontal")));

                var vRelative = style.GetValueOrDefault("mso-position-vertical-relative", "text");
                var vRange = Page.Vertical(vRelative);
                var estimatedY = vRange is null;
                var (vStart, vEnd) = vRange ?? (paragraphTop, paragraphTop);
                var top = Length(style, "margin-top", pxMm, Length(style, "top", pxMm));
                var y = Position(vStart, vEnd, height, top, AbsoluteToNull(style.GetValueOrDefault("mso-position-vertical")));

                var insets = ((string?)textbox.Attribute("inset") ?? "").Split(',');
                double Inset(int index, double fallback) =>
                    index < insets.Length && Units.TryParseLength(insets[index], pxMm, out var mm) ? mm : fallback;

                var rect = new RectangleMm(x, y, width, height);
                var inner = RectangleMm.FromEdges(
                    rect.Left + Inset(0, 2.54), rect.Top + Inset(1, 1.27),
                    rect.Right - Inset(2, 2.54), rect.Bottom - Inset(3, 1.27));

                var textboxStyle = ParseStyle((string?)textbox.Attribute("style"));
                var autofit = textboxStyle.GetValueOrDefault("mso-fit-shape-to-text") == "t"
                              || style.GetValueOrDefault("mso-fit-shape-to-text") == "t";
                var vertical = textboxStyle.GetValueOrDefault("layout-flow", "horizontal").StartsWith("vertical", StringComparison.Ordinal);
                var name = (string?)shape.Attribute("id");

                Add(owner.Build(
                    AddressSourceKind.VmlTextBox, part, rect, inner, ReadTextbox(content),
                    fixedHeight: !autofit,
                    estimated: estimatedX || estimatedY,
                    rotation: Units.ParseDouble(style.GetValueOrDefault("rotation")?.TrimEnd('f', 'd')) ?? 0,
                    vertical: vertical,
                    name: name,
                    extraIdentifiers: [(string?)shape.Attribute(Ns.O + "title"), (string?)shape.Attribute("alt")]));
            }
        }

        private static string? AbsoluteToNull(string? align) => align is null or "absolute" ? null : align;

        private static Dictionary<string, string> ParseStyle(string? style)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in (style ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':');
                if (colon > 0)
                {
                    result[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
                }
            }

            return result;
        }

        private static double Length(Dictionary<string, string> style, string key, double defaultUnitMm, double fallback = 0) =>
            style.TryGetValue(key, out var raw) && Units.TryParseLength(raw, defaultUnitMm, out var mm) ? mm : fallback;


        private static double Position(double start, double end, double size, double? offset, string? align)
        {
            if (align is null)
            {
                return start + (offset ?? 0);
            }

            return align switch
            {
                "right" or "bottom" or "outside" => end - size,
                "center" => start + (end - start - size) / 2,
                _ => start,
            };
        }

        private enum PageBreak
        {
            None,
            BeforeText,
            AfterText,
        }

        private static PageBreak FindPageBreak(XElement paragraph)
        {
            var textSeen = false;
            foreach (var element in OwnDescendants(paragraph))
            {
                if (element.Name == Ns.W + "t" && !string.IsNullOrWhiteSpace(element.Value))
                {
                    textSeen = true;
                }
                else if (element.Name == Ns.W + "lastRenderedPageBreak"
                         || element.Name == Ns.W + "br" && (string?)element.Attribute(Ns.W + "type") == "page")
                {
                    return textSeen ? PageBreak.AfterText : PageBreak.BeforeText;
                }
            }

            return PageBreak.None;
        }

        private static IEnumerable<XElement> OwnDescendants(XElement element)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name == Ns.W + "drawing" || child.Name == Ns.W + "pict" || child.Name == Ns.W + "object"
                    || child.Name == Ns.MC + "AlternateContent" || child.Name == Ns.W + "del")
                {
                    continue;
                }

                yield return child;
                foreach (var descendant in OwnDescendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }

}
