using D2ViewerEditor.Application.Features.DocumentHealth.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.DocumentHealth.Commands.AnalyzeDocumentHealth;

/// <summary>
/// Diagnostyka „Kondycja dokumentu": czy plik jest uszkodzony i co blokuje konwersję do PDF.
/// Plik NIE przechodzi bramki uploadu przed analizą — jej wynik jest jedną z prób w raporcie,
/// bo odrzucony plik to dokładnie ten, który administrator chce zdiagnozować.
/// </summary>
public record AnalyzeDocumentHealthCommand(Stream FileStream, string FileName, bool IncludeConversionProbes)
    : IRequest<Result<DocumentHealthReportDto>>;
