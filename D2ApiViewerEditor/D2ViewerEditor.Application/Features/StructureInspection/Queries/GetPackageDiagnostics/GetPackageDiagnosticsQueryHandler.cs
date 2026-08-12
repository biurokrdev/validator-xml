using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetPackageDiagnostics;

public class GetPackageDiagnosticsQueryHandler
    : IRequestHandler<GetPackageDiagnosticsQuery, Result<PackageDiagnosticsDto>>
{
    private readonly IDocumentStructureInspectionStore _store;
    private readonly IDocumentStructureInspector _inspector;

    public GetPackageDiagnosticsQueryHandler(
        IDocumentStructureInspectionStore store,
        IDocumentStructureInspector inspector)
    {
        _store = store;
        _inspector = inspector;
    }

    public Task<Result<PackageDiagnosticsDto>> Handle(
        GetPackageDiagnosticsQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(Result<PackageDiagnosticsDto>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        var analysis = inspection.Analysis;
        var dto = new PackageDiagnosticsDto(
            analysis.MainDocumentPartPath,
            analysis.PackageIssues,
            analysis.Entries.Select(StructureInspectionMapper.ToDto).ToArray(),
            _inspector.GetSupportedSchemaTargets());

        return Task.FromResult(Result<PackageDiagnosticsDto>.Success(dto));
    }
}
