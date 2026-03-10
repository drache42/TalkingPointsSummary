using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public interface ISummaryGenerator
{
    Task<string?> GenerateAsync(Parent parent, CancellationToken ct = default);
}
