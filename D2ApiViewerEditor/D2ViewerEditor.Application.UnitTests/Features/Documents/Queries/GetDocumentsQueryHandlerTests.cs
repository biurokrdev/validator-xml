using D2ViewerEditor.Application.Features.Documents.Queries.GetDocuments;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using FluentAssertions;
using NSubstitute;
using NUnit.Framework;

namespace D2ViewerEditor.Application.UnitTests.Features.Documents.Queries;

[TestFixture]
public class GetDocumentsQueryHandlerTests
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private IDocumentRepository _repo = null!;
    private GetDocumentsQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = Substitute.For<IDocumentRepository>();
        _handler = new GetDocumentsQueryHandler(_repo);
    }

    private static DocumentListEntry Entry(Guid? activeVersionId = null, int? activeVersionNumber = null, string? lastModifiedBy = null) =>
        new(Guid.NewGuid(), "raport.docx", DocxMime, DateTime.UtcNow, DocumentStatus.Editing, lastModifiedBy, activeVersionId, activeVersionNumber);

    [Test]
    public async Task Handle_MapsEntriesToDtos_WithActiveVersion()
    {
        var versionId = Guid.NewGuid();
        var entry = Entry(versionId, 2, "ACME-42");
        _repo.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<DocumentListEntry> { entry });

        var result = await _handler.Handle(new GetDocumentsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value!.Single();
        dto.MasterId.Should().Be(entry.Id);
        dto.Name.Should().Be("raport.docx");
        dto.MimeType.Should().Be(DocxMime);
        dto.CreatedAt.Should().Be(entry.CreatedAt);
        dto.ActiveVersionId.Should().Be(versionId);
        dto.VersionNumber.Should().Be(2);
        dto.Status.Should().Be("Editing");
        dto.LastModifiedBy.Should().Be("ACME-42");
    }

    [Test]
    public async Task Handle_EntryWithoutActiveVersion_MapsToEmptyGuidAndZero()
    {
        _repo.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<DocumentListEntry> { Entry() });

        var result = await _handler.Handle(new GetDocumentsQuery(), CancellationToken.None);

        var dto = result.Value!.Single();
        dto.ActiveVersionId.Should().Be(Guid.Empty);
        dto.VersionNumber.Should().Be(0);
        dto.LastModifiedBy.Should().BeNull();
    }

    [Test]
    public async Task Handle_ReturnsEveryEntry_WithoutTruncation()
    {
        // Regresja: dawny limit `take = 200` obcinał listę admina do 20 stron po 10 wierszy.
        var entries = Enumerable.Range(0, 1234).Select(_ => Entry()).ToList();
        _repo.ListAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var result = await _handler.Handle(new GetDocumentsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(1234);
        result.Value.Select(d => d.MasterId).Should().Equal(entries.Select(e => e.Id));
    }

    [Test]
    public async Task Handle_NoDocuments_ReturnsEmptyList()
    {
        _repo.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<DocumentListEntry>());

        var result = await _handler.Handle(new GetDocumentsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_RepositoryThrows_ReturnsFailure()
    {
        _repo.ListAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DocumentListEntry>>(_ => throw new Exception("db down"));

        var result = await _handler.Handle(new GetDocumentsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("db down");
    }
}
