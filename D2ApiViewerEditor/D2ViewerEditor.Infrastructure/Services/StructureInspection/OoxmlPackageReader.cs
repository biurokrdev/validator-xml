using System.IO.Compression;
using System.Text;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class RawPackagePart
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public required long UncompressedSize { get; init; }
    public required long CompressedSize { get; init; }

    public string? ContentType { get; set; }
}

public sealed record RawPackageContents(
    IReadOnlyDictionary<string, RawPackagePart> XmlParts,
    IReadOnlyDictionary<string, InspectedPackageEntry> Entries);

public sealed class OoxmlPackageReader
{
    private const string ContentTypesPath = "[Content_Types].xml";
    private const string RootRelationshipsPath = "_rels/.rels";

    private readonly StructureInspectionOptions _options;

    public OoxmlPackageReader(IOptions<StructureInspectionOptions> options)
    {
        _options = options.Value;
    }

    public RawPackageContents Read(byte[] documentBytes, CancellationToken cancellationToken)
    {
        EnsureWithinUploadLimit(documentBytes);

        var parts = new Dictionary<string, RawPackagePart>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, InspectedPackageEntry>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedBytes = 0;

        using var archive = OpenArchive(documentBytes);
        EnsureEntryCountWithinLimit(archive);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = PackagePathResolver.Normalize(entry.FullName);
            EnsureSafeEntryPath(path);

            if (entry.Name.Length == 0)
            {
                continue;
            }

            if (entries.ContainsKey(path))
            {
                throw new InvalidOoxmlPackageException($"Pakiet zawiera zduplikowany wpis: '{path}'.");
            }

            totalUncompressedBytes = AccumulateUncompressedSize(totalUncompressedBytes, entry);

            var isXml = IsXmlPart(path);
            entries[path] = new InspectedPackageEntry(path, entry.Length, entry.CompressedLength, null, isXml);

            if (isXml)
            {
                parts[path] = new RawPackagePart
                {
                    Path = path,
                    Content = ReadText(entry),
                    UncompressedSize = entry.Length,
                    CompressedSize = entry.CompressedLength
                };
            }
        }

        EnsureCorePackageParts(parts);

        return new RawPackageContents(parts, entries);
    }

    public RawPackagePart? ReadXmlPart(byte[] documentBytes, string partPath, CancellationToken cancellationToken)
    {
        EnsureWithinUploadLimit(documentBytes);

        var normalizedPath = PackagePathResolver.Normalize(partPath);
        EnsureSafeEntryPath(normalizedPath);

        if (!IsXmlPart(normalizedPath))
        {
            return null;
        }

        using var archive = OpenArchive(documentBytes);
        EnsureEntryCountWithinLimit(archive);

        var entry = archive.Entries.FirstOrDefault(candidate =>
            PackagePathResolver.Normalize(candidate.FullName)
                .Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (entry.Length > _options.MaxSingleEntryBytes)
        {
            throw new InvalidOoxmlPackageException(
                $"Część '{normalizedPath}' przekracza limit rozmiaru pojedynczego wpisu.");
        }

        return new RawPackagePart
        {
            Path = normalizedPath,
            Content = ReadText(entry),
            UncompressedSize = entry.Length,
            CompressedSize = entry.CompressedLength
        };
    }

    private ZipArchive OpenArchive(byte[] documentBytes)
    {
        var memoryStream = new MemoryStream(documentBytes, writable: false);

        try
        {
            return new ZipArchive(memoryStream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException exception)
        {
            memoryStream.Dispose();
            throw new InvalidOoxmlPackageException($"Plik nie jest poprawnym pakietem OPC (ZIP): {exception.Message}");
        }
    }

    private void EnsureWithinUploadLimit(byte[] documentBytes)
    {
        if (documentBytes.LongLength > _options.MaxUploadBytes)
        {
            throw new InvalidOoxmlPackageException($"Plik przekracza limit {_options.MaxUploadBytes} bajtów.");
        }
    }

    private void EnsureEntryCountWithinLimit(ZipArchive archive)
    {
        if (archive.Entries.Count > _options.MaxZipEntries)
        {
            throw new InvalidOoxmlPackageException($"Pakiet zawiera więcej niż {_options.MaxZipEntries} wpisów ZIP.");
        }
    }

    private long AccumulateUncompressedSize(long total, ZipArchiveEntry entry)
    {
        if (entry.Length > _options.MaxSingleEntryBytes)
        {
            throw new InvalidOoxmlPackageException(
                $"Wpis '{entry.FullName}' przekracza limit rozmiaru pojedynczego wpisu.");
        }

        if (entry.CompressedLength > 0)
        {
            var ratio = (double)entry.Length / entry.CompressedLength;

            if (ratio > _options.MaxCompressionRatio)
            {
                throw new InvalidOoxmlPackageException(
                    $"Wpis '{entry.FullName}' ma podejrzanie wysoki współczynnik kompresji.");
            }
        }

        long updated;

        try
        {
            updated = checked(total + entry.Length);
        }
        catch (OverflowException)
        {
            throw new InvalidOoxmlPackageException("Pakiet deklaruje niepoprawne rozmiary wpisów.");
        }

        if (updated > _options.MaxTotalUncompressedBytes)
        {
            throw new InvalidOoxmlPackageException("Pakiet po rozpakowaniu przekracza dopuszczalny rozmiar.");
        }

        return updated;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static void EnsureSafeEntryPath(string path)
    {
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(segment => segment == ".."))
        {
            throw new InvalidOoxmlPackageException($"Wpis pakietu '{path}' ma niedozwoloną ścieżkę.");
        }
    }

    private static bool IsXmlPart(string path) =>
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static void EnsureCorePackageParts(IReadOnlyDictionary<string, RawPackagePart> parts)
    {
        if (!parts.ContainsKey(ContentTypesPath))
        {
            throw new InvalidOoxmlPackageException($"Pakiet nie zawiera {ContentTypesPath}.");
        }

        if (!parts.ContainsKey(RootRelationshipsPath))
        {
            throw new InvalidOoxmlPackageException($"Pakiet nie zawiera korzenia relationshipów '{RootRelationshipsPath}'.");
        }
    }
}
