using D2ViewerEditor.Application.Features.Documents.Queries.GetDeliveriesByStatus;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace D2ViewerEditor.Application.UnitTests.Features.Documents.Queries;

[TestFixture]
public class GetDeliveriesByStatusQueryHandlerTests
{
    private Mock<IDocumentDeliveryRepository> _deliveryRepo = null!;
    private GetDeliveriesByStatusQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _deliveryRepo = new Mock<IDocumentDeliveryRepository>();
        _handler = new GetDeliveriesByStatusQueryHandler(_deliveryRepo.Object);
    }

    private static DocumentDelivery BuildDelivery() =>
        DocumentDelivery.Create(
            id: Guid.NewGuid(),
            documentId: Guid.NewGuid(),
            sourceVersionId: Guid.NewGuid(),
            snapshotObjectName: "deliveries/x",
            snapshotSizeBytes: 10,
            snapshotSha256: "abc",
            recipientUrl: "https://example.com/return",
            createdBy: "User",
            correlationId: Guid.NewGuid(),
            retentionWindow: TimeSpan.FromHours(24));

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("all")]
    [TestCase("ALL")]
    public async Task Handle_EmptyOrAllStatus_FetchesAllStatuses(string? status)
    {
        var items = new[] { BuildDelivery() };
        _deliveryRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        var result = await _handler.Handle(new GetDeliveriesByStatusQuery(status), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        _deliveryRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        _deliveryRepo.Verify(
            r => r.GetByStatusAsync(It.IsAny<DeliveryStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Handle_ConcreteStatus_FiltersByThatStatus()
    {
        _deliveryRepo.Setup(r => r.GetByStatusAsync(DeliveryStatus.Sent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { BuildDelivery() });

        var result = await _handler.Handle(new GetDeliveriesByStatusQuery("Sent"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _deliveryRepo.Verify(r => r.GetByStatusAsync(DeliveryStatus.Sent, It.IsAny<CancellationToken>()), Times.Once);
        _deliveryRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_MapsRecipientUrlAndSourceVersionId()
    {
        var delivery = BuildDelivery();
        _deliveryRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { delivery });

        var result = await _handler.Handle(new GetDeliveriesByStatusQuery(), CancellationToken.None);

        var dto = result.Value!.Single();
        dto.RecipientUrl.Should().Be(delivery.RecipientUrl);
        dto.SourceVersionId.Should().Be(delivery.SourceVersionId);
        dto.DocumentId.Should().Be(delivery.DocumentId);
    }

    [Test]
    public async Task Handle_UnknownStatus_ReturnsFailureAndQueriesNothing()
    {
        var result = await _handler.Handle(new GetDeliveriesByStatusQuery("Nieistniejacy"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _deliveryRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        _deliveryRepo.Verify(
            r => r.GetByStatusAsync(It.IsAny<DeliveryStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
