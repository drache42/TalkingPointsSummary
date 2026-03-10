using System.Net;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Resolves DNS host names into IP addresses.
/// </summary>
public sealed class HostAddressResolver : IHostAddressResolver
{
    /// <summary>
    /// Resolves the supplied host name into IP addresses.
    /// </summary>
    /// <param name="host">Host name to resolve.</param>
    /// <param name="cancellationToken">Token used to cancel the lookup.</param>
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}