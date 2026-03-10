using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class NewsletterUrlValidatorTests
{
    [Fact]
    public async Task ValidateAsync_PublicHttpsUrl_Allows()
    {
        var validator = CreateValidator(addresses: [IPAddress.Parse("93.184.216.34")]);

        var result = await validator.ValidateAsync("https://example.com/news");

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_HttpUrlWithoutException_Blocks()
    {
        var validator = CreateValidator(addresses: [IPAddress.Parse("93.184.216.34")]);

        var result = await validator.ValidateAsync("http://example.com/news");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("HTTP URLs");
    }

    [Fact]
    public async Task ValidateAsync_LoopbackIp_Blocks()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync("http://127.0.0.1:8080/private");

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_PrivateResolvedAddress_Blocks()
    {
        var validator = CreateValidator(addresses: [IPAddress.Parse("10.0.0.5")]);

        var result = await validator.ValidateAsync("https://newsletter.internal/path");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("resolves");
    }

    [Fact]
    public async Task ValidateAsync_AllowedHostBypassesPrivateResolution()
    {
        var validator = CreateValidator(
            options: new NewsletterScrapingSecurityOptions
            {
                Enabled = true,
                RequireHttps = true,
                AllowedHosts = ["host.docker.internal"],
                AllowHttpHosts = ["host.docker.internal"]
            },
            addresses: [IPAddress.Parse("192.168.1.20")]);

        var result = await validator.ValidateAsync("http://host.docker.internal:5001/newsletter.html");

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_MalformedUrl_Blocks()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync("not-a-url");

        result.Allowed.Should().BeFalse();
    }

    private static NewsletterUrlValidator CreateValidator(
        NewsletterScrapingSecurityOptions? options = null,
        IPAddress[]? addresses = null)
    {
        var securityOptions = Options.Create(options ?? new NewsletterScrapingSecurityOptions());
        return new NewsletterUrlValidator(securityOptions, new StubHostAddressResolver(addresses ?? []));
    }

    private sealed class StubHostAddressResolver : IHostAddressResolver
    {
        private readonly IPAddress[] _addresses;

        public StubHostAddressResolver(IPAddress[] addresses)
        {
            _addresses = addresses;
        }

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.FromResult(_addresses);
    }
}