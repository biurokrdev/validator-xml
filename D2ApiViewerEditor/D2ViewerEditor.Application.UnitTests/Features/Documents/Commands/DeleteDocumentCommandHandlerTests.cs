using D2ViewerEditor.Application.Features.Documents.Commands.DeleteDocument;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace D2ViewerEditor.Application.UnitTests.Features.Documents.Commands;

[TestFixture]
public class DeleteDocumentCommandHandlerTests
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private Mock<IDocumentRepository> _repo = null!;
    private Mock<IDocumentStorageService> _storage = null!;
    private DeleteDocumentCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IDocumentRepository>();
        _storage = new Mock<IDocumentStorageService>();
        _handler = new DeleteDocumentCommandHandler(
            _repo.Object, _storage.Object, NullLogger<DeleteDocumentCommandHandler>.Instance);
    }

    private static Document BuildDocumentWithVersions(params string[] storagePaths)
    {
        var document = new Document(Guid.NewGuid(), "umowa.docx", DocxMime, "User");
        foreach (var path in storagePaths)
            document.AddVersion(Guid.NewGuid(), path, sizeInBytes: 10, createdBy: "User");
        return document;
    }

    private void SetupFound(Document document) =>
        _repo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);

    [Test]
    public async Task Handle_UnknownDocument_ReturnsNotFound()
    {
        var result = await _handler.Handle(new DeleteDocumentCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsNotFound.Should().BeTrue();
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_DeletesAllVersionBlobs_ThenDatabaseEntry()
    {
        var document = BuildDocumentWithVersions("blobs/v1", "blobs/v2");
        SetupFound(document);

        var result = await _handler.Handle(new DeleteDocumentCommand(document.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _storage.Verify(s => s.DeleteAsync("blobs/v1", It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(s => s.DeleteAsync("blobs/v2", It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.DeleteAsync(document, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Handle_DeliveryPipelineStates_AreBlocked(bool queued)
    {
        var document = BuildDocumentWithVersions("blobs/v1");
        if (queued) document.MarkQueued(); else document.MarkSending();
        SetupFound(document);

        var result = await _handler.Handle(new DeleteDocumentCommand(document.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("wysyłka");
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.DeleteAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_StorageFailure_AbortsWithDatabaseUntouched()
    {
        var document = BuildDocumentWithVersions("blobs/v1", "blobs/v2");
        SetupFound(document);
        _storage.Setup(s => s.DeleteAsync("blobs/v1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("gcs down"));

        var result = await _handler.Handle(new DeleteDocumentCommand(document.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        _repo.Verify(r => r.DeleteAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

}
