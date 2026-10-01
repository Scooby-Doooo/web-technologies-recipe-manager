using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Controllers;

// robots.txt and sitemap.xml are generated so they always use the current host and recipe list.
public class SeoController : Controller
{
    private static readonly XNamespace SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private readonly ApplicationDbContext _context;

    public SeoController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet("/robots.txt")]
    public ContentResult Robots()
    {
        var text = new StringBuilder()
            .AppendLine("User-agent: *")
            .AppendLine("Disallow: /api/")
            .AppendLine("Disallow: /*/Create")
            .AppendLine("Disallow: /*/Edit/")
            .AppendLine("Disallow: /*/Delete/")
            .AppendLine("Disallow: /Recipes/CategoryOptions")
            .AppendLine()
            .AppendLine($"Sitemap: {SiteUrl()}/sitemap.xml");
        return Content(text.ToString(), "text/plain", Encoding.UTF8);
    }

    [HttpGet("/sitemap.xml")]
    public async Task<ContentResult> Sitemap(CancellationToken cancellationToken)
    {
        var site = SiteUrl();
        var recipeIds = await _context.Recipes.AsNoTracking()
            .OrderBy(recipe => recipe.Id).Select(recipe => recipe.Id)
            .ToListAsync(cancellationToken);

        var urls = new List<XElement>
        {
            SitemapEntry(site + "/", "weekly", "1.0"),
            SitemapEntry(site + "/Recipes", "daily", "0.8"),
            SitemapEntry(site + "/Categories", "weekly", "0.5"),
            SitemapEntry(site + "/Ingredients", "weekly", "0.5"),
            SitemapEntry(site + "/Statistics", "weekly", "0.3")
        };
        urls.AddRange(recipeIds.Select(id => SitemapEntry($"{site}/Recipes/Details/{id}", "weekly", "0.9")));

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(SitemapNamespace + "urlset", urls));
        return Content(document.Declaration + Environment.NewLine + document, "application/xml", Encoding.UTF8);
    }

    private string SiteUrl() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    private static XElement SitemapEntry(string location, string changeFrequency, string priority) =>
        new(SitemapNamespace + "url",
            new XElement(SitemapNamespace + "loc", location),
            new XElement(SitemapNamespace + "changefreq", changeFrequency),
            new XElement(SitemapNamespace + "priority", priority));
}
