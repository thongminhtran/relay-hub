using System.Net;
using RelayHub.Core.Channels;

namespace RelayHub.UnitTests.Channels;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // Azure / AWS instance metadata
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]           // IPv6 link-local
    [InlineData("fec0::1")]           // IPv6 site-local
    [InlineData("fd12:3456::1")]      // IPv6 unique local
    [InlineData("ff02::1")]           // IPv6 multicast
    [InlineData("::ffff:10.0.0.1")]   // IPv4-mapped private
    [InlineData("::ffff:169.254.169.254")]
    public void Private_local_and_special_addresses_are_not_public(string ip)
    {
        Assert.False(SsrfGuard.IsPublic(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_public(string ip)
    {
        Assert.True(SsrfGuard.IsPublic(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("http://example.com/hook", "https")]
    [InlineData("ftp://example.com/hook", "https")]
    [InlineData("https://localhost/hook", "local")]
    [InlineData("https://LOCALHOST:8443/hook", "local")]
    [InlineData("https://api.localhost/hook", "local")]
    [InlineData("https://intranet/hook", "local")]
    [InlineData("https://printer.local/hook", "local")]
    [InlineData("https://127.0.0.1/hook", "private")]
    [InlineData("https://10.1.2.3/hook", "private")]
    [InlineData("https://169.254.169.254/metadata", "private")]
    [InlineData("https://[::1]/hook", "private")]
    [InlineData("https://[fe80::1]/hook", "private")]
    [InlineData("https://user:pw@example.com/hook", "credentials")]
    [InlineData("not a url", "https")]
    [InlineData("", "https")]
    public void Url_shape_rules_refuse_non_https_local_and_private_targets(string url, string expectedWord)
    {
        var error = SsrfGuard.CheckUrl(url, out var uri);

        Assert.NotNull(error);
        Assert.Null(uri);
        Assert.Contains(expectedWord, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://hooks.example.com/relay")]
    [InlineData("https://93.184.216.34/relay")]
    [InlineData("https://[2001:4860:4860::8888]/relay")]
    public void Public_https_targets_pass_the_shape_rules(string url)
    {
        Assert.Null(SsrfGuard.CheckUrl(url, out var uri));
        Assert.NotNull(uri);
    }

    [Fact]
    public async Task Resolve_allows_a_public_literal_and_refuses_private_ones_without_dns()
    {
        var allowed = await SsrfGuard.ResolveAsync("https://93.184.216.34/relay", CancellationToken.None);
        Assert.True(allowed.Allowed);
        Assert.Equal([IPAddress.Parse("93.184.216.34")], allowed.Addresses);

        var refused = await SsrfGuard.ResolveAsync("https://169.254.169.254/latest/meta-data", CancellationToken.None);
        Assert.False(refused.Allowed);
        Assert.False(refused.Transient);

        var http = await SsrfGuard.ResolveAsync("http://93.184.216.34/relay", CancellationToken.None);
        Assert.False(http.Allowed);
    }

    [Fact]
    public async Task Resolve_refuses_a_host_name_that_resolves_to_loopback()
    {
        // "localhost" is caught by the name rule; "localhost." with a trailing dot and mixed case must be too.
        var verdict = await SsrfGuard.ResolveHostAsync("LocalHost.", CancellationToken.None);
        Assert.False(verdict.Allowed);
    }
}
