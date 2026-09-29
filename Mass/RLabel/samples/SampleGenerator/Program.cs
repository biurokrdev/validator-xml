using Mass.RLabel;

// Generuje przykładowe nalepki do oceny wzrokowej i do testu skanerem.
// Użycie: dotnet run [katalog_wyjściowy]   (domyślnie katalog nadrzędny, czyli samples/)
var outputDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
Directory.CreateDirectory(outputDir);

var renderer = new RegisteredLabelRenderer();

(string Name, RegisteredLabelRequest Request)[] samples =
[
    ("01_krajowa_65x25_png", new() { Number = "00759007731512000621", Type = MailType.Domestic }),
    ("02_zagraniczna_65x25_png", new() { Number = "RR473124829PL", Type = MailType.International }),
    ("03_krajowa_65x25_jpg", new() { Number = "(00) 7 5900773 1 51200062 1", Type = MailType.Domestic, Format = LabelImageFormat.Jpeg }),
    ("04_krajowa_100x40_600dpi", new() { Number = "00759007731512000638", Type = MailType.Domestic, WidthMm = 100, HeightMm = 40, Dpi = 600 }),
    ("05_zagraniczna_50x20", new() { Number = "rr473124850pl", Type = MailType.International, WidthMm = 50, HeightMm = 20 }),
    ("06_krajowa_65x25_203dpi_termiczna", new() { Number = "00759007731512000645", Type = MailType.Domestic, Dpi = 203 }),
];

foreach (var (name, request) in samples)
{
    var image = renderer.Render(request);
    var path = Path.Combine(outputDir, $"{name}.{image.FileExtension}");
    File.WriteAllBytes(path, image.Bytes);
    Console.WriteLine($"{Path.GetFileName(path),-40} {image.WidthPx}×{image.HeightPx} px @ {image.Dpi} DPI, moduł {image.BarcodeModuleMm:0.000} mm, {image.Bytes.Length / 1024} KB, {image.HumanReadableNumber}");
}

Console.WriteLine();
Console.WriteLine("Base64 (pierwsze 60 znaków): " + renderer.RenderBase64("RR473124829PL", MailType.International)[..60] + "…");
Console.WriteLine($"Zapisano w: {outputDir}");
