using Crawler.Infrastructure;
using Xunit;

namespace Crawler.Tests;

public sealed class CrawlRulesTests
{
    [Fact]
    public void Normalize_resolves_fragments_and_removes_fragment()
    {
        var uri = new Uri(new Uri("https://example.com/section/page"), "../next#top");

        Assert.Equal("https://example.com/next", UrlNormalizer.Normalize(uri));
    }

    [Fact]
    public void Domain_ratio_counts_only_http_links()
    {
        var links = new[]
        {
            new Uri("https://example.com/a"),
            new Uri("http://example.com/b"),
            new Uri("https://other.test/"),
            new Uri("mailto:test@example.com")
        };

        Assert.Equal(2d / 3d, UrlNormalizer.DomainLinkRatio(links, "example.com"), 5);
    }

    [Fact]
    public void Parser_resolves_fixture_links_and_deduplicates_fragments()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "links.html");
        var html = File.ReadAllText(fixture);

        var links = PageParser.ExtractLinks(html, new Uri("https://example.com/section/page"));

        Assert.Equal(4, links.Count);
        Assert.Contains(links, link => UrlNormalizer.Normalize(link) == "https://example.com/news");
        Assert.Contains(links, link => UrlNormalizer.Normalize(link) == "https://example.com/section/page");
        Assert.Contains(links, link => UrlNormalizer.Normalize(link) == "https://outside.test/page");
    }
}
