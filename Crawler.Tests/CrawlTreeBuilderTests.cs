using Crawler.Infrastructure;
using Xunit;

namespace Crawler.Tests;

public sealed class CrawlTreeBuilderTests
{
    [Fact]
    public void Build_keeps_the_seed_root_and_includes_each_page_once_for_cyclic_graphs()
    {
        var pages = new Dictionary<string, (double Ratio, List<string> Children)>(StringComparer.Ordinal)
        {
            ["https://example.com/"] = (1, ["https://example.com/a", "https://example.com/b"]),
            ["https://example.com/a"] = (1, ["https://example.com/", "https://example.com/b", "https://example.com/c"]),
            ["https://example.com/b"] = (1, ["https://example.com/", "https://example.com/a", "https://example.com/c"]),
            ["https://example.com/c"] = (1, ["https://example.com/a", "https://example.com/b"])
        };

        var tree = CrawlTreeBuilder.Build("https://example.com/", pages);

        var root = Assert.Single(tree);
        Assert.Equal("https://example.com/", root.Url);
        var urls = Flatten(root).ToArray();
        Assert.Equal(pages.Count, urls.Length);
        Assert.Equal(pages.Count, urls.Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<string> Flatten(PageNode node)
    {
        yield return node.Url;
        foreach (var child in node.Children)
        foreach (var url in Flatten(child))
            yield return url;
    }
}
