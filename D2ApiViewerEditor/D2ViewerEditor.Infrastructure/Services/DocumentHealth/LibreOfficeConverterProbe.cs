using System.Diagnostics;
using System.Text;
using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed class LibreOfficeConverterProbe
{
    public const string ProbeId = "libreoffice";

    private const int MaxOutputChars = 4000;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly DocumentHealthOptions _options;
    private readonly ILogger<LibreOfficeConverterProbe> _logger;

    public LibreOfficeConverterProbe(IOptions<DocumentHealthOptions> options, ILogger<LibreOfficeConverterProbe> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string? ResolveBinary()
    {
        var path = string.IsNullOrWhiteSpace(_options.LibreOfficePath)
            ? Environment.GetEnvironmentVariable("SOFFICE_BIN")
            : _options.LibreOfficePath;

        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    public async Task<ConversionProbeResult> RunAsync(byte[] documentBytes, CancellationToken cancellationToken)
    {
        const string name = "LibreOffice headless → PDF";
        const string description = "Niezależny silnik konwersji (soffice --headless --convert-to pdf); stderr wskazuje konstrukcję, na której pada import.";

        if (!_options.EnableLibreOfficeProbe)
        {
            return new ConversionProbeResult(ProbeId, name, description, ProbeStatus.Skipped, 0,
                "Wyłączone w konfiguracji (DocumentHealth:EnableLibreOfficeProbe).", null);
        }

        var binary = ResolveBinary();

        if (binary is null)
        {
            return new ConversionProbeResult(ProbeId, name, description, ProbeStatus.Skipped, 0,
                "Brak LibreOffice na tej maszynie — ustaw DocumentHealth:LibreOfficePath albo zmienną SOFFICE_BIN (obraz Dockera API ma /usr/bin/soffice).", null);
        }

        var workDir = Path.Combine(Path.GetTempPath(), "d2-document-health", Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(workDir, "profile");
        var inputPath = Path.Combine(workDir, "input.docx");
        var outputPath = Path.Combine(workDir, "input.pdf");
        var stopwatch = Stopwatch.StartNew();

        await Gate.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(profileDir);
            await File.WriteAllBytesAsync(inputPath, documentBytes, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            foreach (var argument in new[]
                     {
                         "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
                         $"-env:UserInstallation=file:///{profileDir.Replace('\\', '/').TrimStart('/')}",
                         "--convert-to", "pdf", "--outdir", workDir, inputPath
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) stdout.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) stderr.AppendLine(args.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.LibreOfficeTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                stopwatch.Stop();

                return new ConversionProbeResult(ProbeId, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                    $"LibreOffice nie zakończył konwersji w {_options.LibreOfficeTimeout.TotalSeconds:0} s — proces zabity. Zawieszenie importu to typowy objaw niedomkniętych pól, korespondencji seryjnej albo bardzo dużych obrazów.",
                    Trim(stderr, stdout));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            stopwatch.Stop();
            var output = Trim(stderr, stdout);

            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                var size = new FileInfo(outputPath).Length;
                var warning = stderr.Length > 0;

                return new ConversionProbeResult(ProbeId, name, description,
                    warning ? ProbeStatus.Warning : ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                    warning
                        ? $"PDF powstał ({size} B, kod wyjścia {process.ExitCode}), ale LibreOffice zgłosił ostrzeżenia na stderr."
                        : $"PDF powstał: {size} B, kod wyjścia {process.ExitCode}.",
                    output);
            }

            return new ConversionProbeResult(ProbeId, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"LibreOffice nie wygenerował pliku PDF (kod wyjścia {process.ExitCode}). Treść stderr poniżej wskazuje przyczynę.",
                output.Length > 0 ? output : "(brak wyjścia procesu)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "Próba LibreOffice nie mogła zostać uruchomiona.");

            return new ConversionProbeResult(ProbeId, name, description, ProbeStatus.Skipped, stopwatch.ElapsedMilliseconds,
                $"Nie udało się uruchomić LibreOffice: {exception.Message}", exception.ToString());
        }
        finally
        {
            Gate.Release();
            TryDelete(workDir);
        }
    }

    private static string Trim(StringBuilder stderr, StringBuilder stdout)
    {
        var combined = new StringBuilder();

        if (stderr.Length > 0)
        {
            combined.Append("[stderr]\n").Append(stderr);
        }

        if (stdout.Length > 0)
        {
            combined.Append("[stdout]\n").Append(stdout);
        }

        var text = combined.ToString().Trim();

        return text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n… (ucięto)";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
