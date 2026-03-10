using System.Net;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Abstraction for DNS host resolution.
/// </summary>
public interface IHostAddressResolver
{
    /// <summary>
    /// Resolves the supplied host name into IP addresses.
    /// </summary>
    /// <param name="host">Host name to resolve.</param>
    /// <param name="cancellationToken">Token used to cancel the lookup.</param>
    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default);
}