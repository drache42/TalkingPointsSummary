using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Uses the configured AI provider to generate a weekly parent briefing summary.
/// </summary>
public class SummaryGenerator : ISummaryGenerator
{
    private readonly IAiClient _aiClient;
    private readonly AppDbContext _db;
    private readonly AiOptions _options;
    private readonly ILogger<SummaryGenerator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _scheduleTimeZone;
    private readonly SummaryPromptBuilder _promptBuilder;

    /// <summary>
    /// Initializes a summary generator.
    /// </summary>
    /// <param name="aiClient">AI client used to execute prompts.</param>
    /// <param name="db">Database context used to load news, summaries, and children.</param>
    /// <param name="aiOptions">AI configuration including the summarization profile.</param>
    /// <param name="schedule">Pipeline schedule configuration, used to determine the local timezone for prompt dates.</param>
    /// <param name="logger">Logger used for generation diagnostics.</param>
    /// <param name="gradeCalculator">Grade calculator used when building the prompt.</param>
    /// <param name="timeProvider">Optional time provider used to define the summary window.</param>
    public SummaryGenerator(
        IAiClient aiClient,
        AppDbContext db,
        IOptions<AiOptions> aiOptions,
        IOptions<PipelineScheduleOptions> schedule,
        ILogger<SummaryGenerator> logger,
        IGradeCalculator gradeCalculator,
        TimeProvider? timeProvider = null)
    {
        _aiClient = aiClient;
        _db = db;
        _options = aiOptions.Value;
        _scheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Value.TimeZone);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _promptBuilder = new SummaryPromptBuilder(gradeCalculator);
    }

    /// <inheritdoc/>
    public async Task<SummaryPromptResult?> BuildPromptAsync(Parent parent, CancellationToken ct = default)
    {
        var nowUtc = _timeProvider.GetUtcDateTime();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _scheduleTimeZone);
        var sixWeeksAgo = nowUtc.AddDays(-42);

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
            .Where(s => s.ParentId == parent.Id && s.CreatedAt > sixWeeksAgo && s.Content != null)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var children = await _db.Children
            .Where(c => c.ParentId == parent.Id)
            .ToListAsync(ct);

        var prompt = _promptBuilder.Build(nowLocal, children, newsItems, previousSummaries);
        return new SummaryPromptResult(prompt, newsItems.Count);
    }

    /// <inheritdoc/>
    public async Task<string?> ExecutePromptAsync(string prompt, CancellationToken ct = default)
    {
        var profile = _options.Profiles.Summarization;

        _logger.LogInformation("Executing summary prompt via AI (model: {Model}, maxTokens: {MaxTokens})",
            profile.ModelId, profile.MaxTokens);

        var result = await _aiClient.CompleteAsync(
            new AiCompletionRequest(prompt, profile.ModelId, profile.MaxTokens), ct);

        var markdown = result.Text;

        _logger.LogInformation("AI returned summary: {Length} chars", markdown.Length);

        return string.IsNullOrEmpty(markdown) ? null : markdown;
    }
}
