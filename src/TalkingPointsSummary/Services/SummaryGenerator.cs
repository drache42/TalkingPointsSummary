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
    /// <summary>
    /// Number of recent completed digests folded into the coverage index. Older digests fall out
    /// of the index rather than out of coverage: what they reported is already recorded on the
    /// news items themselves through <see cref="NewsItem.IncludedInSummaryId"/>.
    /// </summary>
    internal const int CoverageLedgerDigestCount = 12;

    /// <summary>
    /// Rough characters-per-token ratio, used only to log an input-size estimate. It is not
    /// accurate enough to bill against and is never used to make a decision.
    /// </summary>
    private const int EstimatedCharsPerToken = 4;

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
    /// <param name="db">Database context used to load news, summaries, events, and children.</param>
    /// <param name="aiOptions">AI configuration including the summarization profile.</param>
    /// <param name="schedule">Pipeline schedule configuration, used to determine the local timezone for prompt dates.</param>
    /// <param name="logger">Logger used for generation diagnostics.</param>
    /// <param name="gradeCalculator">Grade calculator used when building the prompt.</param>
    /// <param name="timeProvider">Optional time provider used to define the current date.</param>
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
        // TrackedEvent.EventDate holds the local calendar date of the event stamped as UTC midnight,
        // which is the convention the event extractor writes and the "timestamp with time zone"
        // column requires. The comparison value has to be built the same way: an Unspecified
        // DateTime is rejected outright by Npgsql against that column type.
        var today = DateTime.SpecifyKind(nowLocal.Date, DateTimeKind.Utc);

        // Eligibility is a recorded fact, not a date range. A news item carries three dates that
        // drift apart - SentAt, CreatedAt, and AnalyzedAt - so any window drawn over them both
        // skips items (a message that arrived late) and repeats them (a message re-analyzed
        // inside the window). IncludedInSummaryId is written exactly once, when the item is fed
        // into a digest, and answers the only question that matters here: has this been reported?
        var newsItems = await _db.NewsItems
            .Where(n => n.ParentId == parent.Id && n.IncludedInSummaryId == null)
            .OrderBy(n => n.SentAt)
            .ThenBy(n => n.Id)
            .ToListAsync(ct);

        if (newsItems.Count == 0)
        {
            _logger.LogInformation(
                "No unreported news items for parent {ParentName}, skipping summary", parent.Name);
            return null;
        }

        var previousSummaries = await _db.Summaries
            .Where(s => s.ParentId == parent.Id && s.Content != null)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(CoverageLedgerDigestCount)
            .ToListAsync(ct);

        var priorDigests = previousSummaries
            .Select(summary => new PriorDigest(
                TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(summary.CreatedAt, DateTimeKind.Utc),
                    _scheduleTimeZone).Date,
                summary.Content!))
            .ToList();

        var children = await _db.Children
            .Where(c => c.ParentId == parent.Id)
            .ToListAsync(ct);

        // Deliberately no forward cutoff and no IncludedInSummaryId filter. An event stays in the
        // Important Upcoming Dates section of every digest until the day it happens, whether or not
        // the news item that announced it was written up in prose weeks ago. Filtering on either is
        // what made a date announced in September vanish from October's digest.
        var upcomingEvents = await _db.TrackedEvents
            .Where(e => e.ParentId == parent.Id
                && e.Status == TrackedEventStatus.Active
                && e.EventDate >= today)
            .OrderBy(e => e.EventDate)
            .ThenBy(e => e.Title)
            .ToListAsync(ct);

        var prompt = _promptBuilder.Build(nowLocal, children, newsItems, priorDigests, upcomingEvents);

        var systemPromptLength = _promptBuilder.SystemPrompt.Length;
        var estimatedTokens = (prompt.Length + systemPromptLength) / EstimatedCharsPerToken;

        _logger.LogInformation(
            "Built summary prompt for parent {ParentName}: {NewsItemCount} unreported news items, "
            + "{PriorDigestCount} indexed prior digests, {UpcomingEventCount} upcoming events, "
            + "{UserPromptChars} user chars plus {SystemPromptChars} system chars "
            + "(~{EstimatedInputTokens} input tokens)",
            parent.Name,
            newsItems.Count,
            priorDigests.Count,
            upcomingEvents.Count,
            prompt.Length,
            systemPromptLength,
            estimatedTokens);

        return new SummaryPromptResult(prompt, newsItems.Count);
    }

    /// <inheritdoc/>
    public async Task<string?> ExecutePromptAsync(string prompt, CancellationToken ct = default)
    {
        var profile = _options.Profiles.Summarization;

        // The instruction set is identical on every run, so it travels as the system prompt while
        // only this week's content sits in the user message.
        var systemPrompt = string.IsNullOrWhiteSpace(_promptBuilder.SystemPrompt)
            ? null
            : _promptBuilder.SystemPrompt;

        _logger.LogInformation(
            "Executing summary prompt via AI (model: {Model}, maxTokens: {MaxTokens}, thinking: {Thinking}, effort: {Effort})",
            profile.ModelId, profile.MaxTokens, profile.Thinking, profile.Effort ?? "none");

        // The reasoning settings travel with the profile. Dropping them here would silently
        // generate every digest with thinking off while still paying for the raised MaxTokens
        // ceiling that only exists because thinking tokens count against it.
        var result = await _aiClient.CompleteAsync(
            new AiCompletionRequest(
                prompt,
                profile.ModelId,
                profile.MaxTokens,
                profile.Thinking,
                profile.ThinkingBudgetTokens,
                profile.Effort,
                systemPrompt),
            ct);

        if (result.Usage is { } usage)
        {
            _logger.LogInformation(
                "Summary token usage: input {InputTokens}, output {OutputTokens}, thinking {ThinkingTokens}, "
                + "cache write {CacheCreationInputTokens}, cache read {CacheReadInputTokens}",
                usage.InputTokens, usage.OutputTokens, usage.ThinkingTokens,
                usage.CacheCreationInputTokens, usage.CacheReadInputTokens);
        }

        // A digest that stopped at the token ceiling is missing its tail: sections, upcoming dates,
        // or the closing of a sentence. Unlike a truncated categorization, which falls back safely,
        // there is no salvaging it, and it must never be converted to HTML and emailed.
        if (AiResponseTruncatedException.IsTruncated(result.StopReason))
        {
            _logger.LogError(
                "Summary from model {Model} was truncated at the max_tokens limit of {MaxTokens}; " +
                "discarding the partial digest.",
                profile.ModelId, profile.MaxTokens);

            throw new AiResponseTruncatedException(
                $"Summary from model '{profile.ModelId}' stopped at the max_tokens limit of " +
                $"{profile.MaxTokens} and is truncated. Increase MaxTokens or shorten the prompt.");
        }

        var markdown = result.Text;

        _logger.LogInformation("AI returned summary: {Length} chars", markdown.Length);

        return string.IsNullOrEmpty(markdown) ? null : markdown;
    }
}
