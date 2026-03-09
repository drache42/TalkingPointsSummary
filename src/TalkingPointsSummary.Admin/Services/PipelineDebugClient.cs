using System.Net;
using System.Net.Http.Json;

namespace TalkingPointsSummary.Admin.Services;

public sealed class PipelineDebugClient(HttpClient httpClient)
{
    public bool IsConfigured => httpClient.BaseAddress is not null;

    public string? BaseUrl => httpClient.BaseAddress?.ToString();

    public async Task<PipelineDebugRunResult> RunNowAsync(int? parentId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new PipelineDebugRunResult(false, false, false, "Worker trigger URL is not configured.");
        }

        using var response = await httpClient.PostAsJsonAsync("debug/pipeline/run-now", new PipelineDebugRunRequest(parentId), cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<PipelineDebugResponse>(cancellationToken: cancellationToken);
        var message = payload?.Message;

        if (response.IsSuccessStatusCode)
        {
            return new PipelineDebugRunResult(true, false, false, message ?? "Pipeline run complete.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new PipelineDebugRunResult(false, true, false, message ?? "A pipeline run is already in progress.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PipelineDebugRunResult(false, false, true, message ?? "The requested parent was not found.");
        }

        return new PipelineDebugRunResult(false, false, false, message ?? $"Worker returned HTTP {(int)response.StatusCode}.");
    }

    private sealed record PipelineDebugRunRequest(int? ParentId);
    private sealed record PipelineDebugResponse(string? Status, string? Message);
}

public sealed record PipelineDebugRunResult(bool Success, bool AlreadyRunning, bool ParentNotFound, string Message);