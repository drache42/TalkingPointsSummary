using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Validates newsletter URLs against scheme, host, and resolved-address rules.
/// </summary>
public sealed class NewsletterUrlValidator : INewsletterUrlValidator
{
    private readonly NewsletterScrapingSecurityOptions _options;
    private readonly IHostAddressResolver _hostAddressResolver;

    /// <summary>
    /// Initializes a newsletter URL validator.
    /// </summary>
    /// <param name="options">Security rules that control which URLs are allowed.</param>
    /// <param name="hostAddressResolver">Resolver used to inspect host DNS results.</param>
    public NewsletterUrlValidator(
        IOptions<NewsletterScrapingSecurityOptions> options,
        IHostAddressResolver hostAddressResolver)
    {
        _options = options.Value;
        _hostAddressResolver = hostAddressResolver;
    }

    /// <summary>
    /// Validates whether a URL may be scraped under the configured security rules.
    /// </summary>
    /// <param name="url">URL to validate.</param>
    /// <param name="cancellationToken">Token used to cancel validation.</param>
    public async Task<NewsletterUrlValidationResult> ValidateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return NewsletterUrlValidationResult.Allow();
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return NewsletterUrlValidationResult.Block("URL is not a valid absolute URI.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return NewsletterUrlValidationResult.Block("Only HTTP and HTTPS URLs are allowed.");
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return NewsletterUrlValidationResult.Block("URLs containing user info are not allowed.");
        }

        var normalizedHost = NormalizeHost(uri.Host);
        var explicitlyAllowedHost = ContainsHost(_options.AllowedHosts, normalizedHost);

        if (_options.RequireHttps
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !ContainsHost(_options.AllowHttpHosts, normalizedHost))
        {
            return NewsletterUrlValidationResult.Block("HTTP URLs are not allowed for this host.");
        }

        if (IsExplicitlyBlockedHost(normalizedHost) && !explicitlyAllowedHost)
        {
            return NewsletterUrlValidationResult.Block("Loopback and localhost hosts are not allowed.");
        }

        if (IPAddress.TryParse(uri.Host, out var parsedAddress))
        {
            if (explicitlyAllowedHost)
            {
                return NewsletterUrlValidationResult.Allow();
            }

            return IsBlockedAddress(parsedAddress)
                ? NewsletterUrlValidationResult.Block("Private, loopback, or link-local IP addresses are not allowed.")
                : NewsletterUrlValidationResult.Allow();
        }

        if (explicitlyAllowedHost)
        {
            return NewsletterUrlValidationResult.Allow();
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _hostAddressResolver.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return NewsletterUrlValidationResult.Block("Host name could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return NewsletterUrlValidationResult.Block("Host name did not resolve to any addresses.");
        }

        return addresses.Any(IsBlockedAddress)
            ? NewsletterUrlValidationResult.Block("Host resolves to a private, loopback, or link-local IP address.")
            : NewsletterUrlValidationResult.Allow();
    }

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool ContainsHost(IEnumerable<string> hosts, string host)
        => hosts.Any(candidate => string.Equals(NormalizeHost(candidate), host, StringComparison.Ordinal));

    private static bool IsExplicitlyBlockedHost(string host)
        => string.Equals(host, "localhost", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal);

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 127
                || bytes[0] == 0;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.Equals(IPAddress.IPv6Loopback)
                || IsUniqueLocalIPv6(address);
        }

        return false;
    }

    private static bool IsUniqueLocalIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length > 0 && (bytes[0] & 0xFE) == 0xFC;
    }
}