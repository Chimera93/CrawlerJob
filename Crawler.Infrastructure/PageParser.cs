using System.Net;
using HtmlAgilityPack;

namespace Crawler.Infrastructure;

public static class PageParser
{
    public static IReadOnlyList<Uri> ExtractLinks(string html, Uri pageUri)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.SelectNodes("//a[@href]")?
            .Select(node => node.GetAttributeValue("href", "").Trim())
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => Uri.TryCreate(pageUri, WebUtility.HtmlDecode(href), out var uri) ? uri : null)
            .Where(uri => uri is not null &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(uri => uri!)
            .DistinctBy(UrlNormalizer.Normalize)
            .ToArray() ?? [];
    }
}
