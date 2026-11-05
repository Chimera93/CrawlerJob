namespace Crawler.Infrastructure;

public static class CrawlTreeBuilder
{
    public static IReadOnlyList<PageNode> Build(string? startingUrl,
        IReadOnlyDictionary<string, (double Ratio, List<string> Children)> pages)
    {
        var roots = new List<string>();
        if (startingUrl is not null && pages.ContainsKey(startingUrl))
        {
            roots.Add(startingUrl);
        }
        else
        {
            var children = pages.Values.SelectMany(page => page.Children)
                .ToHashSet(StringComparer.Ordinal);
            roots.AddRange(pages.Keys.Where(url => !children.Contains(url)));
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var tree = new List<PageNode>();
        foreach (var url in roots.Concat(pages.Keys))
        {
            if (visited.Add(url))
                tree.Add(BuildNode(url));
        }
        return tree;

        PageNode BuildNode(string url)
        {
            var page = pages[url];
            var nodes = page.Children.Where(pages.ContainsKey).Where(visited.Add)
                .Select(BuildNode).ToArray();
            return new PageNode(url, page.Ratio, nodes);
        }
    }
}
