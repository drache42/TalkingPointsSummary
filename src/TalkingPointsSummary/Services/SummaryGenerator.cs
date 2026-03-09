using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Uses Claude Sonnet to generate a weekly parent briefing summary.
/// </summary>
public class SummaryGenerator
{
    private static readonly SummaryPromptBuilder PromptBuilder = new();

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly ILogger<SummaryGenerator> _logger;

    public SummaryGenerator(
        HttpClient httpClient,
        AppDbContext db,
        IOptions<AppSettings> settings,
        ILogger<SummaryGenerator> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generates a weekly summary for a parent, returning the Markdown content.
    /// </summary>
    public async Task<string?> GenerateAsync(Parent parent, CancellationToken ct = default)
    {
        var sixWeeksAgo = DateTime.UtcNow.AddDays(-42);

        var newsItems = await _db.NewsItems
            .Where(n => n.ParentId == parent.Id && n.CreatedAt > sixWeeksAgo)
            .OrderByDescending(n => n.SentAt)
            .ToListAsync(ct);

        if (newsItems.Count == 0)
        {
            _logger.LogInformation("No news items for parent {ParentName}, skipping summary", parent.Name);
            return null;
        }

        var previousSummaries = await _db.Summaries
            .Where(s => s.ParentId == parent.Id && s.CreatedAt > sixWeeksAgo)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var children = await _db.Children
            .Where(c => c.ParentId == parent.Id)
            .ToListAsync(ct);

        var prompt = PromptBuilder.Build(DateTime.UtcNow, children, newsItems, previousSummaries);

        _logger.LogInformation("Generating summary for parent {ParentName} with {NewsCount} news items",
            parent.Name, newsItems.Count);

        var requestBody = new
        {
            model = "claude-sonnet-4-5-20250929",
            max_tokens = 8192,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(requestBody);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<AnthropicResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

        var markdown = apiResponse?.Content?.FirstOrDefault()?.Text;

        _logger.LogInformation("Generated summary for parent {ParentName}: {Length} chars",
            parent.Name, markdown?.Length ?? 0);

        return markdown;
    }
}
