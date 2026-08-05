using System.Xml.Linq;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class SeoControllerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://store.example.test")]
    [InlineData("https://user:password@store.example.test")]
    [InlineData("javascript:alert(1)")]
    public void PublicSiteOptions_RejectUnsafeOrigins(string? origin)
    {
        var options = new PublicSiteOptions { BaseUrl = origin };

        Assert.False(options.TryGetBaseUri(out _));
    }

    [Fact]
    public async Task SitemapIndexAndPages_AreBoundedAndUseConfiguredHttpsOrigin()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = new SeoController(
            database.Context,
            new PublicSiteOptions
            {
                BaseUrl = "https://parts.example.test/ignored-path",
                SitemapPageSize = 50
            });

        var indexResult = Assert.IsType<ContentResult>(
            await controller.GetIndex(CancellationToken.None));
        var index = XDocument.Parse(indexResult.Content!);
        var locations = index.Descendants()
            .Where(element => element.Name.LocalName == "loc")
            .Select(element => element.Value)
            .ToArray();
        var productResult = Assert.IsType<ContentResult>(
            await controller.GetProducts(1, CancellationToken.None));
        var productMap = XDocument.Parse(productResult.Content!);

        Assert.Equal("application/xml; charset=utf-8", indexResult.ContentType);
        Assert.Equal(
            [
                "https://parts.example.test/sitemaps/static.xml",
                "https://parts.example.test/sitemaps/products-1.xml",
                "https://parts.example.test/sitemaps/products-2.xml"
            ],
            locations);
        Assert.Equal(
            50,
            productMap.Descendants().Count(element => element.Name.LocalName == "url"));
        Assert.IsType<NotFoundResult>(await controller.GetProducts(3, CancellationToken.None));
    }

    [Fact]
    public async Task MissingProductionOrigin_FailsSitemapClosedAndDisallowsRobots()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = new SeoController(database.Context, new PublicSiteOptions());

        var sitemap = Assert.IsType<ObjectResult>(
            await controller.GetIndex(CancellationToken.None));
        var robots = Assert.IsType<ContentResult>(controller.GetRobots());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, sitemap.StatusCode);
        Assert.Equal("User-agent: *\nDisallow: /\n", robots.Content);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(AutoPartsDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public AutoPartsDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new(context, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
