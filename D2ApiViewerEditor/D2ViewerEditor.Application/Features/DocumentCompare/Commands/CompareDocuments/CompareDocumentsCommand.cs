using D2ViewerEditor.Application.Features.DocumentCompare.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.DocumentCompare.Commands.CompareDocuments;

/// <summary>
/// Literalne porównanie dwóch plików DOCX (pakiet, XML atrybut po atrybucie, tekst). Flagi pomijają
/// szum: identyfikatory rewizji Worda (<c>w:rsid*</c>) i właściwości dokumentu (docProps, daty zapisu).
/// </summary>
public record CompareDocumentsCommand(
    Stream LeftStream,
    string LeftFileName,
    Stream RightStream,
    string RightFileName,
    bool IgnoreRevisionIds,
    bool IgnoreDocumentProperties)
    : IRequest<Result<DocumentComparisonReportDto>>;
