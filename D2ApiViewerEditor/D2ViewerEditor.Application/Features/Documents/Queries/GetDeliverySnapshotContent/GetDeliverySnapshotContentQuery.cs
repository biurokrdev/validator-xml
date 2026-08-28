using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Queries.GetDeliverySnapshotContent;

public record GetDeliverySnapshotContentQuery(Guid DeliveryId) : IRequest<Result<DeliverySnapshotContentDto>>;

public record DeliverySnapshotContentDto(
    string FileName,
    string MimeType,
    byte[] Content,
    string Sha256
);
