using System.Net;

namespace TalkingPointsSummary.Services;

public sealed class HostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}