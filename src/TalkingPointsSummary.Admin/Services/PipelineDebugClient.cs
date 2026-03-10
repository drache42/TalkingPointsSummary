using System.Net;
using System.Net.Http.Json;

namespace TalkingPointsSummary.Admin.Services;

/// <summary>
/// Client used by the admin UI to trigger worker pipeline runs on demand.
/// </summary>
public sealed class PipelineDebugClient(HttpClient httpClient)
{
    /// <summary>
    /// Returns whether the debug client has a configured worker base address.
    /// </summary>
    public bool IsConfigured => httpClient.BaseAddress is not null;

    /// <summary>
    /// Configured worker base URL, if available.
    /// </summary>
    public string? BaseUrl => httpClient.BaseAddress?.ToString();

    /// <summary>
    /// Sends a request to trigger the worker pipeline immediately.
    /// </summary>
    /// <param name="parentId">Optional parent identifier to scope the run.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
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

/// <summary>
/// Result returned by the admin pipeline debug endpoint.
/// </summary>
public sealed record PipelineDebugRunResult
{
    /// <summary>
    /// Initializes a new pipeline debug result.
    /// </summary>
    /// <param name="success">Whether the request completed successfully.</param>
    /// <param name="alreadyRunning">Whether a run was rejected because another run is active.</param>
    /// <param name="parentNotFound">Whether the requested parent was not found.</param>
    /// <param name="message">Human-readable status message for the caller.</param>
    public PipelineDebugRunResult(bool success, bool alreadyRunning, bool parentNotFound, string message)
    {
        Success = success;
        AlreadyRunning = alreadyRunning;
        ParentNotFound = parentNotFound;
        Message = message;
    }

    /// <summary>
    /// Whether the request completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Whether the request was rejected because another pipeline run is active.
    /// </summary>
    public bool AlreadyRunning { get; init; }

    /// <summary>
    /// Whether the requested parent could not be found.
    /// </summary>
    public bool ParentNotFound { get; init; }

    /// <summary>
    /// Human-readable status message for the caller.
    /// </summary>
    public string Message { get; init; }
}