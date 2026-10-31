using System.Net;
using Crawler.Infrastructure;
using Xunit;

namespace Crawler.Tests;

public sealed class PublicHttpUrlPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.51.100.10", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void IsPublicAddress_classifies_global_and_non_routable_addresses(string address, bool expected) =>
        Assert.Equal(expected, PublicHttpUrlPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task EnsurePublicHttpUrl_rejects_loopback_without_dns_lookup()
    {
        var policy = new PublicHttpUrlPolicy();

        await Assert.ThrowsAsync<UnsafeCrawlUrlException>(() =>
            policy.EnsurePublicHttpUrlAsync(new Uri("http://127.0.0.1/"), CancellationToken.None));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.com/")]
    public async Task EnsurePublicHttpUrl_rejects_unsupported_or_credential_urls(string url)
    {
        var policy = new PublicHttpUrlPolicy();

        await Assert.ThrowsAsync<UnsafeCrawlUrlException>(() =>
            policy.EnsurePublicHttpUrlAsync(new Uri(url), CancellationToken.None));
    }
}
