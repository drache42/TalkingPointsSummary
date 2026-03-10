using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Fetches messages from the TalkingPoints parent messaging API.
/// </summary>
public class TalkingPointsApiClient : ITalkingPointsApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TalkingPointsApiClient> _logger;
    private readonly TalkingPointsApiOptions _options;
    private const string BaseUrl = "https://app.talkingpts.org/api/parents/v3/messages/feed";
    private const int PageSize = 20;

    public TalkingPointsApiClient(
        HttpClient httpClient,
        IOptions<TalkingPointsApiOptions> options,
        ILogger<TalkingPointsApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<TalkingPointsMessage>> FetchMessagesAsync(
        Parent parent,
        string? stopAtMessageId = null,
        DateTime? stopBeforeSentAtUtc = null,
        int? maxPagesOverride = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching messages for parent {ParentName} (ID: {ParentId})", parent.Name, parent.Id);

        var messages = new List<TalkingPointsMessage>();
        var page = 1;
        var pagesFetched = 0;
        var stopReached = false;
        var maxPages = maxPagesOverride ?? _options.MaxPagesPerRun;

        while (!stopReached && page <= maxPages)
        {
            var pageMessages = await FetchPageAsync(parent, page, ct);
            pagesFetched++;

            if (pageMessages.Count == 0)
            {
                break;
            }

            foreach (var message in pageMessages)
            {
                if (!string.IsNullOrWhiteSpace(stopAtMessageId)
                    && string.Equals(message.Id, stopAtMessageId, StringComparison.Ordinal))
                {
                    stopReached = true;
                    break;
                }

                var messageSentAt = GetSentAtUtc(message);
                if (stopBeforeSentAtUtc.HasValue
                    && messageSentAt.HasValue
                    && messageSentAt.Value < stopBeforeSentAtUtc.Value)
                {
                    stopReached = true;
                    break;
                }

                messages.Add(message);
            }

            if (stopReached || pageMessages.Count < PageSize)
            {
                break;
            }

            page++;
        }

        _logger.LogInformation(
            "Fetched {Count} new candidate messages for parent {ParentName} across {PageCount} page(s). StopAtReached={StopReached}. MaxPagesPerRun={MaxPagesPerRun}",
            messages.Count,
            parent.Name,
            pagesFetched,
            stopReached,
            maxPages);

        return messages;
    }

    private static DateTime? GetSentAtUtc(TalkingPointsMessage message)
        => message.DisplayDate ?? message.CreatedAt;

    private async Task<List<TalkingPointsMessage>> FetchPageAsync(Parent parent, int page, CancellationToken ct)
    {
        var url = $"{BaseUrl}?page={page}&pageSize={PageSize}&students=";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-token", parent.TalkingPointsToken);
        request.Headers.Add("x-contactid", parent.TalkingPointsContactId);
        request.Headers.Add("x-app-version", "5.0.0");
        request.Headers.Add("x-language", "en");
        request.Headers.Add("x-mobile-platform", "web");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<TalkingPointsApiResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

        return apiResponse?.Data?.Messages ?? [];
    }
}
