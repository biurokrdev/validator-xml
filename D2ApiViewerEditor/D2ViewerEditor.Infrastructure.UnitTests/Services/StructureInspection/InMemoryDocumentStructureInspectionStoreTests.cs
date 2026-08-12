using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.StructureInspection;

[TestFixture]
public class InMemoryDocumentStructureInspectionStoreTests
{
    private static DocumentStructureInspection Inspection(
        DateTimeOffset createdAtUtc,
        TimeSpan? timeToLive = null,
        int documentSizeInBytes = 0) => new(
        Guid.NewGuid(),
        createdAtUtc,
        createdAtUtc.Add(timeToLive ?? TimeSpan.FromMinutes(30)),
        new DocumentStructureAnalysis
        {
            FileName = "test.docx",
            FileSizeInBytes = 0,
            MainDocumentPartPath = "word/document.xml",
            Elements = [],
            ElementsById = new Dictionary<string, InspectedElement>(),
            Parts = [],
            Entries = [],
            Sections = [],
            PackageIssues = [],
            SchemaIssues = [],
            SchemaIssueCount = 0,
            ElementsTruncated = false
        },
        new byte[documentSizeInBytes]);

    [Test]
    public void Save_EvictsOldestInspectionsAboveTheLimit()
    {
        var store = new InMemoryDocumentStructureInspectionStore(
            Options.Create(new StructureInspectionOptions { MaxStoredInspections = 2 }));

        var oldest = Inspection(DateTimeOffset.UtcNow.AddMinutes(-3));
        var middle = Inspection(DateTimeOffset.UtcNow.AddMinutes(-2));
        var newest = Inspection(DateTimeOffset.UtcNow.AddMinutes(-1));

        store.Save(oldest);
        store.Save(middle);
        store.Save(newest);

        store.Get(oldest.Id).Should().BeNull();
        store.Get(middle.Id).Should().NotBeNull();
        store.Get(newest.Id).Should().NotBeNull();
    }

    [Test]
    public void Get_ReturnsNullForInspectionPastItsTimeToLive()
    {
        var store = new InMemoryDocumentStructureInspectionStore(Options.Create(new StructureInspectionOptions()));

        var expired = Inspection(DateTimeOffset.UtcNow.AddMinutes(-10), TimeSpan.FromMinutes(5));
        store.Save(expired);

        store.Get(expired.Id).Should().BeNull();
    }

    [Test]
    public void Save_EvictsOldestInspectionsAboveTheByteBudget()
    {
        var store = new InMemoryDocumentStructureInspectionStore(
            Options.Create(new StructureInspectionOptions { MaxStoredInspections = 10, MaxStoredBytes = 250 }));

        var oldest = Inspection(DateTimeOffset.UtcNow.AddMinutes(-3), documentSizeInBytes: 100);
        var middle = Inspection(DateTimeOffset.UtcNow.AddMinutes(-2), documentSizeInBytes: 100);
        var newest = Inspection(DateTimeOffset.UtcNow.AddMinutes(-1), documentSizeInBytes: 100);

        store.Save(oldest);
        store.Save(middle);
        store.Save(newest);

        store.Get(oldest.Id).Should().BeNull();
        store.Get(middle.Id).Should().NotBeNull();
        store.Get(newest.Id).Should().NotBeNull();
    }

    [Test]
    public void Delete_RemovesInspectionAndReportsWhetherItExisted()
    {
        var store = new InMemoryDocumentStructureInspectionStore(Options.Create(new StructureInspectionOptions()));
        var inspection = Inspection(DateTimeOffset.UtcNow);
        store.Save(inspection);

        store.Delete(inspection.Id).Should().BeTrue();
        store.Get(inspection.Id).Should().BeNull();
        store.Delete(inspection.Id).Should().BeFalse();
    }
}
