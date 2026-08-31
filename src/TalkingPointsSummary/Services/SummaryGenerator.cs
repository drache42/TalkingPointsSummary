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
    /// Upper bound on how many unreported news items one digest may carry.
    /// </summary>
    /// <remarks>
    /// Eligibility is decided by <see cref="NewsItem.IncludedInSummaryId"/> rather than by a date
    /// window, which is correct but unbounded on its own: an item is only marked reported after a
    /// digest is delivered, so every run that fails after generation leaves its items eligible
    /// again and the next prompt carries them plus a new week. Left alone that ratchets upward
    /// until the prompt exceeds the context window and the pipeline can never recover by itself.
    /// A row cap bounds the prompt without reintroducing a date floor. Because the query orders
    /// oldest first, a backlog drains across successive runs in the order it accumulated instead
    /// of starving the oldest items, and nothing is dropped: whatever does not fit stays
    /// unreported and leads the next digest.
    /// </remarks>
    internal const int MaxNewsItemsPerDigest = 60;

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
    /// Built lazily so the revision template is read off disk only when a digest actually needs
    /// correcting, which is the uncommon case.
    /// </summary>
    private static readonly Lazy<SummaryRevisionPromptBuilder> RevisionPromptBuilder =
        new(() => new SummaryRevisionPromptBuilder());

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
        //
        // The coverage filter is unbounded on its own, so the row cap is the safety valve: see
        // MaxNewsItemsPerDigest. Oldest first means an overflowing backlog drains in order rather
        // than leaving the oldest items permanently starved behind newer ones.
        var eligibleCount = await _db.NewsItems
            .CountAsync(n => n.ParentId == parent.Id && n.IncludedInSummaryId == null, ct);

        var newsItems = await _db.NewsItems
            .Where(n => n.ParentId == parent.Id && n.IncludedInSummaryId == null)
            .OrderBy(n => n.SentAt)
            .ThenBy(n => n.Id)
            .Take(MaxNewsItemsPerDigest)
            .ToListAsync(ct);

        if (eligibleCount > newsItems.Count)
        {
            // Never silent: an overflow means a previous run failed after generating, or ingestion
            // outran delivery. Both are operator-visible problems, not routine trimming.
            _logger.LogWarning(
                "Parent {ParentName} has {EligibleCount} unreported news items, over the {Cap} " +
                "per-digest cap. Including the {Included} oldest; the remaining {Deferred} stay " +
                "unreported and lead the next digest.",
                parent.Name, eligibleCount, MaxNewsItemsPerDigest, newsItems.Count,
                eligibleCount - newsItems.Count);
        }

        if (newsItems.Count == 0)
        {
            _logger.LogInformation(
                "No unreported news items for parent {ParentName}, skipping summary", parent.Name);
            return null;
        }

        // Only digests that actually reached the parent's inbox belong in the coverage index. A
        // row with content but no EmailSentAt was generated and never delivered, and its news
        // items are still unreported, so listing it here would tell the model those topics had
        // already been sent while the same items sit in the prompt above waiting to be reported.
        var previousSummaries = await _db.Summaries
            .Where(s => s.ParentId == parent.Id && s.Content != null && s.EmailSentAt != null)
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

        // The two rendered blocks are carried out with the result rather than rebuilt later. The
        // critic and the reviser have to see the exact upcoming-dates list and coverage index the
        // draft was written from; re-rendering them from a second query would let the two drift.
        var renderedUpcomingDates = SummaryPromptBuilder.BuildUpcomingDates(children, upcomingEvents);
        var renderedCoverageLedger = SummaryCoverageLedger.Render(priorDigests);

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

        return new SummaryPromptResult(
            prompt,
            newsItems,
            renderedUpcomingDates,
            renderedCoverageLedger);
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

    /// <inheritdoc/>
    public async Task<string?> ReviseAsync(SummaryRevisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DraftMarkdown) || string.IsNullOrWhiteSpace(request.Issues))
        {
            _logger.LogWarning(
                "Summary revision skipped: the draft or the defect list was empty.");
            return null;
        }

        var profile = _options.Profiles.Summarization;

        AiCompletionResult result;

        try
        {
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.GetUtcDateTime(), _scheduleTimeZone);

            var prompt = RevisionPromptBuilder.Value.Build(
                request.DraftMarkdown,
                request.Issues,
                request.UpcomingDates,
                nowLocal);

            _logger.LogInformation(
                "Revising draft digest (model: {Model}, maxTokens: {MaxTokens}, thinking: {Thinking}, effort: {Effort})",
                profile.ModelId, profile.MaxTokens, profile.Thinking, profile.Effort ?? "none");

            // The revision runs on the summarization profile and its system prompt, so the reviser
            // is held to the same digest rules that produced the draft. A revision written under a
            // different instruction set would come back correct and off-voice.
            var systemPrompt = string.IsNullOrWhiteSpace(_promptBuilder.SystemPrompt)
                ? null
                : _promptBuilder.SystemPrompt;

            result = await _aiClient.CompleteAsync(
                new AiCompletionRequest(
                    prompt,
                    profile.ModelId,
                    profile.MaxTokens,
                    profile.Thinking,
                    profile.ThinkingBudgetTokens,
                    profile.Effort,
                    systemPrompt),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller is shutting the run down. That is not a revision failure to absorb.
            throw;
        }
        catch (Exception ex)
        {
            // A revision is an improvement on a digest that is already good enough to send.
            // Taking the run down over a failed correction pass would cost the parent the whole
            // digest to fix a wrong weekday.
            _logger.LogWarning(ex,
                "Summary revision request failed; keeping the original draft digest.");
            return null;
        }

        if (AiResponseTruncatedException.IsTruncated(result.StopReason))
        {
            // A truncated revision is missing its tail and must never be emailed, but the draft it
            // was meant to correct is intact, so the caller keeps that instead of losing the week.
            _logger.LogWarning(
                "Summary revision was truncated at the max_tokens limit of {MaxTokens}; " +
                "keeping the original draft digest.",
                profile.MaxTokens);
            return null;
        }

        var revised = result.Text;

        if (string.IsNullOrWhiteSpace(revised))
        {
            // Observed in practice: a response whose first content block is a thinking block with
            // no text. There is no revision in it.
            _logger.LogWarning(
                "Summary revision returned no text (stop reason: {StopReason}); " +
                "keeping the original draft digest.",
                result.StopReason ?? "none");
            return null;
        }

        _logger.LogInformation("AI returned revised summary: {Length} chars", revised.Length);

        return revised;
    }
}
