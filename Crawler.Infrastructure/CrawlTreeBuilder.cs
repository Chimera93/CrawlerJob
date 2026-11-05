namespace Crawler.Infrastructure;

public static class CrawlTreeBuilder
{
    public static IReadOnlyList<PageNode> Build(string? startingUrl,
        IReadOnlyDictionary<string, (double Ratio, int Depth, List<string> Children)> pages)
    {
        var roots = new List<string>();
        if (startingUrl is not null && pages.ContainsKey(startingUrl))
        {
            roots.Add(startingUrl);
        }
        else
        {
            roots.AddRange(pages.Where(entry => !pages.Any(parent =>
                    parent.Value.Depth + 1 == entry.Value.Depth &&
                    parent.Value.Children.Contains(entry.Key, StringComparer.Ordinal)))
                .Select(entry => entry.Key));
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var tree = new List<PageNode>();
        foreach (var url in roots.Concat(pages.Keys.OrderBy(url => pages[url].Depth)))
        {
            if (visited.Add(url))
                tree.Add(BuildNode(url));
        }
        return tree;

        PageNode BuildNode(string url)
        {
            var page = pages[url];
            var nodes = page.Children.Where(pages.ContainsKey)
                .Where(child => pages[child].Depth == page.Depth + 1)
                .Where(visited.Add)
                .Select(BuildNode).ToArray();
            return new PageNode(url, page.Ratio, nodes);
        }
    }
}
