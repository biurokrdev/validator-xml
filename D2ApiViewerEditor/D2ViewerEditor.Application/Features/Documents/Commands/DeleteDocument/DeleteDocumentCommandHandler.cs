using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace D2ViewerEditor.Application.Features.Documents.Commands.DeleteDocument;

public class DeleteDocumentCommandHandler : IRequestHandler<DeleteDocumentCommand, Result<bool>>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentStorageService _storageService;
    private readonly ILogger<DeleteDocumentCommandHandler> _logger;

    public DeleteDocumentCommandHandler(
        IDocumentRepository documentRepository,
        IDocumentStorageService storageService,
        ILogger<DeleteDocumentCommandHandler> logger)
    {
        _documentRepository = documentRepository;
        _storageService = storageService;
        _logger = logger;
    }

    public async Task<Result<bool>> Handle(DeleteDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetByIdWithVersionsAsync(request.MasterId, cancellationToken);
        if (document == null)
            return Result<bool>.NotFound();

        if (document.Status is DocumentStatus.Queued or DocumentStatus.Sending)
            return Result<bool>.Failure(
                $"Nie można usunąć dokumentu w stanie {document.Status}: wysyłka jest w toku. " +
                "Najpierw przerwij/anuluj wysyłkę.");

        foreach (var version in document.Versions)
        {
            if (string.IsNullOrWhiteSpace(version.StoragePath)) continue;
            try
            {
                await _storageService.DeleteAsync(version.StoragePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Usuwanie dokumentu {MasterId}: błąd kasowania blobu {StoragePath} — przerwano, baza nietknięta.",
                    document.Id, version.StoragePath);
                return Result<bool>.Failure(
                    "Nie udało się usunąć pliku z magazynu. Dokument pozostaje na liście — spróbuj ponownie.");
            }
        }

        await _documentRepository.DeleteAsync(document, cancellationToken);
        await _documentRepository.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Dokument {MasterId} ({Name}) usunięty TRWALE przez administratora: {VersionCount} wersji z GCS + wpis z bazy.",
            document.Id, document.Name, document.Versions.Count);

        return Result<bool>.Success(true);
    }
}
