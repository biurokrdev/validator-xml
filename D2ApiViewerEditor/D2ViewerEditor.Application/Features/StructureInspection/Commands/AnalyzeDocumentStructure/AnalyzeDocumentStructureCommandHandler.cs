using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using MediatR;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Application.Features.StructureInspection.Commands.AnalyzeDocumentStructure;

public class AnalyzeDocumentStructureCommandHandler
    : IRequestHandler<AnalyzeDocumentStructureCommand, Result<StructureInspectionSummaryDto>>
{
    private readonly IDocumentStructureInspector _inspector;
    private readonly IDocumentStructureInspectionStore _store;
    private readonly IFileUploadSecurityService _uploadSecurity;
    private readonly StructureInspectionRetentionOptions _retention;

    public AnalyzeDocumentStructureCommandHandler(
        IDocumentStructureInspector inspector,
        IDocumentStructureInspectionStore store,
        IFileUploadSecurityService uploadSecurity,
        IOptions<StructureInspectionRetentionOptions> retention)
    {
        _inspector = inspector;
        _store = store;
        _uploadSecurity = uploadSecurity;
        _retention = retention.Value;
    }

    public async Task<Result<StructureInspectionSummaryDto>> Handle(
        AnalyzeDocumentStructureCommand request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.FileStream.CopyToAsync(buffer, cancellationToken);
        var documentBytes = buffer.ToArray();

        var uploadValidation = _uploadSecurity.ValidateDocxStructure(documentBytes);

        if (!uploadValidation.IsValid)
        {
            return Result<StructureInspectionSummaryDto>.Failure(
                $"Plik odrzucony ({uploadValidation.Code}): {uploadValidation.Error}");
        }

        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var analysis = _inspector.Analyze(documentBytes, request.FileName, cancellationToken);
            var inspection = new DocumentStructureInspection(
                Guid.NewGuid(),
                createdAt,
                createdAt.Add(_retention.InspectionTimeToLive),
                analysis,
                documentBytes);

            _store.Save(inspection);

            return Result<StructureInspectionSummaryDto>.Success(BuildSummary(inspection));
        }
        catch (InvalidOoxmlPackageException exception)
        {
            return Result<StructureInspectionSummaryDto>.Failure(exception.Message);
        }
    }

    private static StructureInspectionSummaryDto BuildSummary(DocumentStructureInspection inspection)
    {
        var analysis = inspection.Analysis;

        var issues = analysis.Elements.SelectMany(element => element.Issues).ToArray();

        return new StructureInspectionSummaryDto(
            inspection.Id,
            analysis.FileName,
            analysis.FileSizeInBytes,
            analysis.MainDocumentPartPath,
            inspection.ExpiresAtUtc,
            analysis.Elements.Count,
            issues.Count(issue => issue.Severity == StructureIssueSeverity.Error),
            issues.Count(issue => issue.Severity == StructureIssueSeverity.Warning),
            issues.Count(issue => issue.Severity == StructureIssueSeverity.Info),
            analysis.SchemaIssueCount,
            analysis.PackageIssues.Count,
            analysis.Sections.Count,
            analysis.ElementsTruncated,
            analysis.Parts.Select(StructureInspectionMapper.ToDto).ToArray(),
            analysis.Elements
                .Select(element => element.Category)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }
}
