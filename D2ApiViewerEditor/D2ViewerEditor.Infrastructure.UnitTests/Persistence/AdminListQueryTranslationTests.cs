using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Infrastructure.Persistence;
using D2ViewerEditor.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Persistence;

[TestFixture]
public class AdminListQueryTranslationTests
{
    private DocumentDbContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        _context = new DocumentDbContext(options);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void DocumentList_IsSingleQuery_WithoutLimit_AndWithoutMetadataColumn()
    {
        var sql = DocumentRepository.BuildListQuery(_context).ToQueryString();
        TestContext.Out.WriteLine(sql);

        var outer = sql.Substring(sql.LastIndexOf("FROM documents", StringComparison.Ordinal));
        outer.Should().NotContainEquivalentOf("LIMIT").And.NotContainEquivalentOf("OFFSET");
        outer.Should().Contain("is_deleted").And.Contain("ORDER BY").And.Contain("created_at");

        sql.Should().NotContain("metadata", "grid nie pokazuje metadanych — kolumna JSON nie może jechać z każdym wierszem");
        sql.Should().Contain("is_active", "aktywna wersja wyznaczana w SQL, nie przez materializację kolekcji wersji");
        sql.Should().Contain("version_number");
        sql.Should().NotContainEquivalentOf("JOIN");
        sql.Split("LIMIT 1").Should().HaveCount(3, "dokładnie dwa podzapytania o aktywną wersję (id, version_number)");
    }

    [Test]
    public void DeliveryList_AllStatuses_HasNoLimit_AndSortsNewestFirst()
    {
        var sql = DocumentDeliveryRepository.BuildListQuery(_context, status: null).ToQueryString();
        TestContext.Out.WriteLine(sql);

        sql.Should().NotContainEquivalentOf("LIMIT").And.NotContainEquivalentOf("OFFSET");
        sql.Should().NotContain("WHERE");
        sql.Should().Contain("ORDER BY").And.Contain("created_at").And.Contain("DESC");
    }

    [Test]
    public void DeliveryList_ByStatus_FiltersInSql_WithoutLimit()
    {
        var sql = DocumentDeliveryRepository.BuildListQuery(_context, DeliveryStatus.DeadLettered).ToQueryString();
        TestContext.Out.WriteLine(sql);

        sql.Should().NotContainEquivalentOf("LIMIT").And.NotContainEquivalentOf("OFFSET");
        sql.Should().Contain("WHERE").And.Contain("status");
        sql.Should().Contain("ORDER BY").And.Contain("created_at").And.Contain("DESC");
    }
}
