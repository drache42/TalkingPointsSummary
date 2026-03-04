using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Fetches messages from the TalkingPoints parent messaging API.
/// </summary>
public class TalkingPointsApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TalkingPointsApiClient> _logger;
    private const string BaseUrl = "https://app.talkingpts.org/api/parents/v3/messages/feed";

    public TalkingPointsApiClient(HttpClient httpClient, ILogger<TalkingPointsApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<TalkingPointsMessage>> FetchMessagesAsync(Parent parent, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?page=1&pageSize=20&students=";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-token", parent.TalkingPointsToken);
        request.Headers.Add("x-contactid", parent.TalkingPointsContactId);
        request.Headers.Add("x-app-version", "5.0.0");
        request.Headers.Add("x-language", "en");
        request.Headers.Add("x-mobile-platform", "web");

        _logger.LogInformation("Fetching messages for parent {ParentName} (ID: {ParentId})", parent.Name, parent.Id);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<TalkingPointsApiResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

        var messages = apiResponse?.Data?.Messages ?? [];
        _logger.LogInformation("Fetched {Count} messages for parent {ParentName}", messages.Count, parent.Name);

        return messages;
    }
}

// --- API Response DTOs ---

public class TalkingPointsApiResponse
{
    public TalkingPointsData? Data { get; set; }
}

public class TalkingPointsData
{
    public List<TalkingPointsMessage> Messages { get; set; } = [];
}

public class TalkingPointsMessage
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    public string? ContactMessageId { get; set; }
    public string? Text { get; set; }
    public string? FromName { get; set; }
    public TalkingPointsFrom? From { get; set; }
    public TalkingPointsContactInfo? ContactInfo { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? DisplayDate { get; set; }
}

public class TalkingPointsFrom
{
    public TalkingPointsUser? User { get; set; }
}

public class TalkingPointsUser
{
    public string? Signature { get; set; }
}

public class TalkingPointsContactInfo
{
    public string? StudentName { get; set; }
}
