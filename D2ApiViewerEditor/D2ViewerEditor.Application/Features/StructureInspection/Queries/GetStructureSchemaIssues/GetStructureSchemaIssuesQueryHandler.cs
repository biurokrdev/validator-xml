using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureSchemaIssues;

public class GetStructureSchemaIssuesQueryHandler
    : IRequestHandler<GetStructureSchemaIssuesQuery, Result<SchemaIssuesDto>>
{
    private readonly IDocumentStructureInspectionStore _store;
    private readonly IDocumentStructureInspector _inspector;

    public GetStructureSchemaIssuesQueryHandler(
        IDocumentStructureInspectionStore store,
        IDocumentStructureInspector inspector)
    {
        _store = store;
        _inspector = inspector;
    }

    public Task<Result<SchemaIssuesDto>> Handle(
        GetStructureSchemaIssuesQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(Result<SchemaIssuesDto>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        var analysis = inspection.Analysis;
        var analyzedTarget = analysis.SchemaIssues.FirstOrDefault()?.TargetVersion;

        if (string.IsNullOrWhiteSpace(request.TargetVersion) ||
            request.TargetVersion.Equals(analyzedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Result<SchemaIssuesDto>.Success(new SchemaIssuesDto(
                analyzedTarget ?? request.TargetVersion ?? string.Empty,
                analysis.SchemaIssueCount,
                analysis.SchemaIssues.Select(StructureInspectionMapper.ToDto).ToArray())));
        }

        try
        {
            var issues = _inspector.ValidateSchema(
                inspection.DocumentBytes,
                request.TargetVersion,
                analysis.Elements,
                cancellationToken);

            return Task.FromResult(Result<SchemaIssuesDto>.Success(new SchemaIssuesDto(
                request.TargetVersion,
                issues.Count,
                issues.Select(StructureInspectionMapper.ToDto).ToArray())));
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(Result<SchemaIssuesDto>.Failure(exception.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = new SchemaValidationIssue(
                "OPENXML_SDK_VALIDATION_FAILED",
                "Error",
                $"Open XML SDK nie zwalidował pakietu: {exception.Message}",
                null,
                null,
                null,
                null,
                request.TargetVersion);

            return Task.FromResult(Result<SchemaIssuesDto>.Success(new SchemaIssuesDto(
                request.TargetVersion,
                1,
                [StructureInspectionMapper.ToDto(failure)])));
        }
    }
}
