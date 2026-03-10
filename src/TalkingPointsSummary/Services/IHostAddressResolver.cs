using System.Net;

namespace TalkingPointsSummary.Services;

public interface IHostAddressResolver
{
    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default);
}