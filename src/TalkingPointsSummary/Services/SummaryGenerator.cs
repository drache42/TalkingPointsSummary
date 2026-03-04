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

        var prompt = BuildPrompt(parent, children, newsItems, previousSummaries);

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

    private string BuildPrompt(
        Parent parent,
        List<Child> children,
        List<NewsItem> newsItems,
        List<Summary> previousSummaries)
    {
        var now = DateTime.UtcNow;
        var todayStr = now.ToString("dddd, MMMM d, yyyy");

        // Build context section with children info
        var contextBuilder = new StringBuilder();
        foreach (var child in children)
        {
            var gradeLabel = GradeCalculator.GetCurrentGradeLabel(child, now);
            contextBuilder.AppendLine($"- {child.Name} ({child.School}) — {gradeLabel}");
        }

        // Build recent news section
        var newsBuilder = new StringBuilder();
        for (int i = 0; i < newsItems.Count; i++)
        {
            var item = newsItems[i];
            newsBuilder.AppendLine($"""

                ### News Item {i + 1}
                Student: {item.StudentName}
                From: {item.FromName}
                Type: {(item.SourceType == SourceType.NewsletterUrl ? "Newsletter" : "Direct Message")}
                Date Sent: {item.SentAt:O}
                Content: {item.NewsContent}
                ---
                """);
        }

        // Build previous summaries section
        var summaryBuilder = new StringBuilder();
        foreach (var s in previousSummaries)
        {
            summaryBuilder.AppendLine(s.Content);
            summaryBuilder.AppendLine("---");
        }
        if (previousSummaries.Count == 0)
        {
            summaryBuilder.AppendLine("None");
        }

        // Build child output format sections
        var childSections = new StringBuilder();
        foreach (var child in children)
        {
            var gradeLabel = GradeCalculator.GetCurrentGradeLabel(child, now);
            childSections.AppendLine($"""

                # {child.Emoji} {child.Name} ({gradeLabel})
                ### [Subheading Topic]
                [Friendly summary...]

                """);
        }

        return $"""
            You are an elite School Communications Assistant. Your goal is to provide a "Monday Morning Briefing" for parents by synthesizing recent school messages into a warm, scannable summary.

            <instructions>
            1. **Date Awareness:** - Today is {todayStr}.
               - Analyze the "Date Sent" for every item in <recent_news>.
               - Define "This Week" as the 7 days leading up to today.

            2. **Deduplication Logic:** - Compare <recent_news> against <previous_summaries>.
               - If a news item has already been summarized in the past, DISCARD it unless there is a significant new update (e.g., a time change or a new call to action).
               - If the same news appears in multiple recent messages, merge them into one concise point.

            3. **Grade Level Info:**
            {contextBuilder}

            4. **Tone & Style:**
               - Tone: Warm, conversational, "parent-to-parent."
               - Structure: Use the exact Markdown headers provided in the output format.
               - Scannability: Use bolding for key nouns and ### for sub-topics.
            </instructions>

            <thinking>
            Before writing the email, perform these steps:
            1. Create a mental timeline of the messages in <recent_news>. Identify which ones fell in the "Current Week."
            2. Cross-reference each new item with <previous_summaries>. List which items are truly "new" here.
            3. Identify any "Whole School" news that affects multiple children or general family logistics.
            </thinking>

            <context>
            {contextBuilder}
            </context>

            <recent_news>
            {newsBuilder}
            </recent_news>

            <previous_summaries>
            {summaryBuilder}
            </previous_summaries>

            <output_format>
            # 🏫 Whole School News
            (Include only if applicable to multiple children or general family scheduling)

            ### [Subheading Topic]
            [Friendly summary of the news]

            {childSections}

            ## Important Upcoming Dates
            - List ONLY future dates.
            - Format: **[Date]** – [Event] ([Time if applicable])
            - Sort chronologically (earliest first).
            - If empty, write: "No upcoming dates at this time."
            </output_format>
            """;
    }
}
