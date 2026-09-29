using Mass.AddressWindow;
using Mass.AddressWindow.Pdf;

// Waliduje pliki PDF z samples/pdf w obu trybach i zapisuje podglądy do samples/pdf/preview.
// Użycie: dotnet run [katalog_z_pdf]
var pdfDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "pdf"));
var previewDir = Path.Combine(pdfDir, "preview");
Directory.CreateDirectory(previewDir);

var validator = new PdfAddressWindowValidator();
foreach (var file in Directory.EnumerateFiles(pdfDir, "*.pdf").OrderBy(f => f))
{
    Console.WriteLine($"=== {Path.GetFileName(file)}");
    foreach (var mode in new[] { WindowMode.Single, WindowMode.Double })
    {
        var result = validator.ValidateFile(file, mode, options: new PdfValidationOptions { PreviewDpi = 72 });
        Console.WriteLine($"  [{mode}] IsValid = {result.IsValid}; strona {result.PageWidthMm:0}×{result.PageHeightMm:0} mm; {result.Summary}");
        foreach (var window in result.Windows.Where(w => w.Block is not null))
        {
            var b = window.Block!;
            Console.WriteLine($"    {window.Role}: tekst {b.TextBounds}, {b.MinFontSizePt}–{b.MaxFontSizePt} pt, wiersze: {string.Join(" | ", b.Lines)}");
        }

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"    - {issue.Severity,-7} {issue.Code} ({issue.Window}): {issue.Message}");
        }

        if (result.Preview is { } preview)
        {
            var path = Path.Combine(previewDir, $"{Path.GetFileNameWithoutExtension(file)}_{mode}.png");
            File.WriteAllBytes(path, preview.Png);
            Console.WriteLine($"    podgląd: {Path.GetFileName(path)} ({preview.WidthPx}×{preview.HeightPx}, {(preview.RenderedFromPdf ? "render PDFium" : "schemat")})");
        }
    }

    Console.WriteLine();
}
