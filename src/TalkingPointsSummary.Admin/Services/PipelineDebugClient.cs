using System.Net;
using System.Net.Http.Json;

namespace TalkingPointsSummary.Admin.Services;

public sealed class PipelineDebugClient(HttpClient httpClient)
{
    public bool IsConfigured => httpClient.BaseAddress is not null;

    public string? BaseUrl => httpClient.BaseAddress?.ToString();

    public async Task<PipelineDebugRunResult> RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new PipelineDebugRunResult(false, false, "Worker trigger URL is not configured.");
        }

        using var response = await httpClient.PostAsync("debug/pipeline/run-now", content: null, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<PipelineDebugResponse>(cancellationToken: cancellationToken);
        var message = payload?.Message;

        if (response.IsSuccessStatusCode)
        {
            return new PipelineDebugRunResult(true, false, message ?? "Pipeline run complete.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new PipelineDebugRunResult(false, true, message ?? "A pipeline run is already in progress.");
        }

        return new PipelineDebugRunResult(false, false, message ?? $"Worker returned HTTP {(int)response.StatusCode}.");
    }

    private sealed record PipelineDebugResponse(string? Status, string? Message);
}

public sealed record PipelineDebugRunResult(bool Success, bool AlreadyRunning, string Message);