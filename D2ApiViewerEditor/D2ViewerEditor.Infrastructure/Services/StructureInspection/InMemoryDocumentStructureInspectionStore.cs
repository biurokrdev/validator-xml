using System.Collections.Concurrent;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class InMemoryDocumentStructureInspectionStore : IDocumentStructureInspectionStore
{
    private readonly ConcurrentDictionary<Guid, DocumentStructureInspection> _inspections = new();
    private readonly StructureInspectionOptions _options;

    public InMemoryDocumentStructureInspectionStore(IOptions<StructureInspectionOptions> options)
    {
        _options = options.Value;
    }

    public void Save(DocumentStructureInspection inspection)
    {
        _inspections[inspection.Id] = inspection;
        Evict();
    }

    public DocumentStructureInspection? Get(Guid inspectionId)
    {
        if (!_inspections.TryGetValue(inspectionId, out var inspection))
        {
            return null;
        }

        if (!IsExpired(inspection))
        {
            return inspection;
        }

        _inspections.TryRemove(inspectionId, out _);

        return null;
    }

    public bool Delete(Guid inspectionId) => _inspections.TryRemove(inspectionId, out _);

    private void Evict()
    {
        foreach (var expired in _inspections.Values.Where(IsExpired))
        {
            _inspections.TryRemove(expired.Id, out _);
        }

        var byAge = _inspections.Values.OrderBy(item => item.CreatedAtUtc).ToArray();
        var totalBytes = byAge.Sum(item => item.DocumentBytes.LongLength);
        var count = byAge.Length;

        foreach (var oldest in byAge)
        {
            if (count <= _options.MaxStoredInspections && totalBytes <= _options.MaxStoredBytes)
            {
                break;
            }

            if (_inspections.TryRemove(oldest.Id, out _))
            {
                totalBytes -= oldest.DocumentBytes.LongLength;
                count--;
            }
        }
    }

    private static bool IsExpired(DocumentStructureInspection inspection) =>
        DateTimeOffset.UtcNow > inspection.ExpiresAtUtc;
}
