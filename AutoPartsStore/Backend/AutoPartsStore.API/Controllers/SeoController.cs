using System.Xml.Linq;
using AutoPartsStore.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Controllers;

public sealed class PublicSiteOptions
{
    public string? BaseUrl { get; init; }
    public int SitemapPageSize { get; init; } = 50_000;

    public bool TryGetBaseUri(out Uri baseUri)
    {
        var isValid = Uri.TryCreate(BaseUrl, UriKind.Absolute, out var candidate) &&
                      string.IsNullOrEmpty(candidate.UserInfo) &&
                      string.IsNullOrEmpty(candidate.Query) &&
                      string.IsNullOrEmpty(candidate.Fragment) &&
                      (candidate.Scheme == Uri.UriSchemeHttps ||
                       (candidate.Scheme == Uri.UriSchemeHttp && candidate.IsLoopback)) &&
                      SitemapPageSize is >= 1 and <= 50_000;
        if (isValid)
        {
            baseUri = new Uri(candidate!.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
            return true;
        }

        baseUri = null!;
        return false;
    }
}

[ApiController]
[AllowAnonymous]
public sealed class SeoController : ControllerBase
{
    private static readonly XNamespace SitemapNamespace =
        "http://www.sitemaps.org/schemas/sitemap/0.9";
    private readonly AutoPartsDbContext _context;
    private readonly PublicSiteOptions _options;

    public SeoController(AutoPartsDbContext context, PublicSiteOptions options)
    {
        _context = context;
        _options = options;
    }

    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> GetIndex(CancellationToken cancellationToken)
    {
        if (!_options.TryGetBaseUri(out var baseUri)) return NotConfigured();
        var productCount = await _context.Products.AsNoTracking().CountAsync(cancellationToken);
        var productPages = Math.Max(
            1,
            (int)Math.Ceiling(productCount / (double)_options.SitemapPageSize));
        var maps = new List<string> { "sitemaps/static.xml" };
        maps.AddRange(Enumerable.Range(1, productPages)
            .Select(page => $"sitemaps/products-{page}.xml"));
        var root = new XElement(
            SitemapNamespace + "sitemapindex",
            maps.Select(path => new XElement(
                SitemapNamespace + "sitemap",
                new XElement(SitemapNamespace + "loc", new Uri(baseUri, path).AbsoluteUri))));
        return Xml(root);
    }

    [HttpGet("/sitemaps/static.xml")]
    public async Task<IActionResult> GetStatic(CancellationToken cancellationToken)
    {
        if (!_options.TryGetBaseUri(out var baseUri)) return NotConfigured();
        var categorySlugs = await _context.Categories.AsNoTracking()
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => candidate.Slug)
            .ToListAsync(cancellationToken);
        var brandSlugs = await _context.Brands.AsNoTracking()
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => candidate.Slug)
            .ToListAsync(cancellationToken);
        var paths = new List<string> { string.Empty, "hakkimizda", "iletisim", "sss" };
        paths.AddRange(categorySlugs.Select(slug => $"category/{Uri.EscapeDataString(slug)}"));
        paths.AddRange(brandSlugs.Select(slug => $"brand/{Uri.EscapeDataString(slug)}"));
        return Xml(UrlSet(baseUri, paths));
    }

    [HttpGet("/sitemaps/products-{page:int}.xml")]
    public async Task<IActionResult> GetProducts(
        int page,
        CancellationToken cancellationToken)
    {
        if (!_options.TryGetBaseUri(out var baseUri)) return NotConfigured();
        if (page <= 0) return NotFound();
        var total = await _context.Products.AsNoTracking().CountAsync(cancellationToken);
        var pageCount = Math.Max(
            1,
            (int)Math.Ceiling(total / (double)_options.SitemapPageSize));
        if (page > pageCount) return NotFound();
        var productIds = await _context.Products.AsNoTracking()
            .OrderBy(candidate => candidate.Id)
            .Skip((page - 1) * _options.SitemapPageSize)
            .Take(_options.SitemapPageSize)
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        return Xml(UrlSet(baseUri, productIds.Select(id => $"product/{id}")));
    }

    [HttpGet("/robots.txt")]
    public IActionResult GetRobots()
    {
        if (!_options.TryGetBaseUri(out var baseUri))
        {
            return Content("User-agent: *\nDisallow: /\n", "text/plain; charset=utf-8");
        }

        var sitemapUri = new Uri(baseUri, "sitemap.xml").AbsoluteUri;
        return Content(
            "User-agent: *\n" +
            "Disallow: /admin\n" +
            "Disallow: /checkout\n" +
            "Disallow: /profile\n" +
            "Disallow: /orders\n" +
            "Disallow: /garajim\n" +
            $"Sitemap: {sitemapUri}\n",
            "text/plain; charset=utf-8");
    }

    private static XElement UrlSet(Uri baseUri, IEnumerable<string> paths) =>
        new(
            SitemapNamespace + "urlset",
            paths.Select(path => new XElement(
                SitemapNamespace + "url",
                new XElement(SitemapNamespace + "loc", new Uri(baseUri, path).AbsoluteUri))));

    private ContentResult Xml(XElement root) => Content(
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(),
        "application/xml; charset=utf-8");

    private ObjectResult NotConfigured() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new { message = "Public site origin is not configured." });
}
