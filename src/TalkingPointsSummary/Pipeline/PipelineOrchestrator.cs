using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Pipeline;

/// <summary>
/// Orchestrates the full weekly pipeline for a single parent:
/// Fetch, Dedup, Categorize, Scrape, Store News, Extract Events, Summarize, Review, Email, Archive.
/// </summary>
public class PipelineOrchestrator
{
    /// <summary>
    /// Subject line every digest email goes out under.
    /// </summary>
    private const string EmailSubject = "Talking Points Summary";

    private static readonly JsonSerializerOptions CritiqueLogSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly AppDbContext _db;
    private readonly ITalkingPointsApiClient _apiClient;
    private readonly IMessageDeduplicator _deduplicator;
    private readonly IMessageCategorizer _categorizer;
    private readonly INewsletterScraper _scraper;
    private readonly IEventExtractor _eventExtractor;
    private readonly ISummaryGenerator _summaryGenerator;
    private readonly SummaryOutputValidator _outputValidator;
    private readonly ISummaryCritic _critic;
    private readonly IMarkdownConverter _markdownConverter;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _scheduleTimeZone;

    /// <summary>
    /// Initializes a pipeline orchestrator for end-to-end parent processing.
    /// </summary>
    /// <param name="db">Database context used for persistence.</param>
    /// <param name="apiClient">TalkingPoints API client.</param>
    /// <param name="deduplicator">Message deduplication service.</param>
    /// <param name="categorizer">Message categorization service.</param>
    /// <param name="scraper">Newsletter scraping service.</param>
    /// <param name="eventExtractor">Event extraction service applied to each persisted news item.</param>
    /// <param name="summaryGenerator">Summary generation service.</param>
    /// <param name="outputValidator">Deterministic date and formatting validator for the draft digest.</param>
    /// <param name="critic">AI reviewer that checks the draft digest against its sources.</param>
    /// <param name="markdownConverter">Markdown-to-HTML converter.</param>
    /// <param name="emailSender">Email delivery service.</param>
    /// <param name="schedule">
    /// Pipeline schedule configuration, used for the local timezone the digest's dates are read
    /// in. Validating "is this date upcoming?" against UTC would flag a same-day event as past
    /// for any timezone behind UTC.
    /// </param>
    /// <param name="logger">Logger used for pipeline diagnostics.</param>
    /// <param name="timeProvider">Optional time provider for timestamps.</param>
    public PipelineOrchestrator(
        AppDbContext db,
        ITalkingPointsApiClient apiClient,
        IMessageDeduplicator deduplicator,
        IMessageCategorizer categorizer,
        INewsletterScraper scraper,
        IEventExtractor eventExtractor,
        ISummaryGenerator summaryGenerator,
        SummaryOutputValidator outputValidator,
        ISummaryCritic critic,
        IMarkdownConverter markdownConverter,
        IEmailSender emailSender,
        IOptions<PipelineScheduleOptions> schedule,
        ILogger<PipelineOrchestrator> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _apiClient = apiClient;
        _deduplicator = deduplicator;
        _categorizer = categorizer;
        _scraper = scraper;
        _eventExtractor = eventExtractor;
        _summaryGenerator = summaryGenerator;
        _outputValidator = outputValidator;
        _critic = critic;
        _markdownConverter = markdownConverter;
        _emailSender = emailSender;
        _scheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Value.TimeZone);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the full weekly pipeline for a single parent.
    /// </summary>
    /// <param name="parent">Parent to process.</param>
    /// <param name="ct">Token used to cancel the run.</param>
    public async Task RunAsync(Parent parent, CancellationToken ct = default)
    {
        _logger.LogInformation("=== Starting pipeline for parent: {ParentName} (ID: {ParentId}) ===",
            parent.Name, parent.Id);

        try
        {
            var lastSavedMessage = await _db.Messages
                .Where(message => message.ParentId == parent.Id)
                .OrderByDescending(message => message.SentAt)
                .ThenByDescending(message => message.Id)
                .Select(message => new
                {
                    message.ExternalMessageId,
                    message.SentAt
                })
                .FirstOrDefaultAsync(ct);

            // Step 1: Fetch messages from TalkingPoints API
            var apiMessages = await _apiClient.FetchMessagesAsync(
                parent,
                lastSavedMessage?.ExternalMessageId,
                lastSavedMessage?.SentAt,
                null,
                ct);

            // Step 2: Deduplicate and save new messages
            await _deduplicator.DeduplicateAndSaveAsync(parent, apiMessages, ct);

            // Step 3: Get all unprocessed messages
            var unprocessed = await _deduplicator.GetUnprocessedAsync(parent, ct);
            _logger.LogInformation("Found {Count} unprocessed messages for parent {ParentName}",
                unprocessed.Count, parent.Name);

            // Step 4: Categorize, route, and extract events from each unprocessed message
            foreach (var message in unprocessed)
            {
                await ProcessMessageAsync(parent, message, ct);
            }

            // Step 5: Build summary prompt
            var promptResult = await _summaryGenerator.BuildPromptAsync(parent, ct);
            if (promptResult == null)
            {
                _logger.LogInformation("No summary generated for parent {ParentName} (no news items)", parent.Name);
                return;
            }

            // Step 6: Persist prompt row before AI call so it is always saved
            var summary = new Summary
            {
                ParentId = parent.Id,
                Prompt = promptResult.Prompt,
                Content = null,
                CreatedAt = _timeProvider.GetUtcDateTime()
            };
            _db.Summaries.Add(summary);
            await _db.SaveChangesAsync(ct);

            // Step 7: Execute AI to produce Markdown
            var markdown = await _summaryGenerator.ExecutePromptAsync(promptResult.Prompt, ct);
            if (string.IsNullOrEmpty(markdown))
            {
                _logger.LogWarning("AI returned empty summary for parent {ParentName}; prompt row saved for debugging", parent.Name);
                return;
            }

            // Step 8: Validate and critique the draft, correcting it when either found defects
            var review = await ReviewAsync(promptResult, markdown, ct);

            // Step 9: Persist the finished digest BEFORE it is handed to SMTP. Generating a digest
            // costs a full model call; a mail server that is down must not throw that away, and a
            // row left with a null Content would drop out of the coverage index for good.
            summary.Content = review.Markdown;
            summary.CritiqueLog = review.CritiqueLog;
            summary.RevisionCount = review.RevisionCount;
            await _db.SaveChangesAsync(ct);

            // Step 10: Convert to HTML and send email
            var html = _markdownConverter.ToHtml(review.Markdown);
            await _emailSender.SendAsync(
                parent.EmailRecipients,
                EmailSubject,
                html,
                ct);

            // Step 11: Only a delivered digest closes out the news it reported. Stamping
            // IncludedInSummaryId before the send would bury this week's items behind a digest
            // that never arrived, and they would never be reported again.
            summary.EmailSentAt = _timeProvider.GetUtcDateTime();
            await MarkNewsItemsReportedAsync(promptResult, summary.Id, ct);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("=== Pipeline completed for parent: {ParentName} ===", parent.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for parent {ParentName} (ID: {ParentId})",
                parent.Name, parent.Id);
            throw;
        }
    }

    /// <summary>
    /// Runs the deterministic validator and the AI critic over the draft, and applies a single
    /// correction pass when either reports something worth correcting.
    /// </summary>
    /// <remarks>
    /// Review never blocks a send. A revision is kept only when it comes back non-empty and no
    /// worse than the draft by the validator's own count, so a reviser that mangles the digest
    /// cannot make the emailed result worse than what generation produced.
    /// </remarks>
    private async Task<DigestReview> ReviewAsync(
        SummaryPromptResult promptResult,
        string markdown,
        CancellationToken ct)
    {
        var todayLocal = TimeZoneInfo
            .ConvertTimeFromUtc(_timeProvider.GetUtcDateTime(), _scheduleTimeZone)
            .Date;

        var validationFindings = _outputValidator.Validate(markdown, todayLocal);

        var critiqueFindings = await _critic.CritiqueAsync(
            new SummaryCritiqueRequest(
                promptResult.NewsItems,
                markdown,
                promptResult.UpcomingDates,
                promptResult.CoverageLedger),
            ct);

        if (validationFindings.Count == 0 && critiqueFindings.Count == 0)
        {
            _logger.LogInformation("Draft digest passed validation and critique with no findings.");
            return new DigestReview(markdown, null, 0);
        }

        _logger.LogInformation(
            "Draft digest review found {ValidationFindingCount} validation finding(s) and "
            + "{CritiqueFindingCount} critique finding(s).",
            validationFindings.Count, critiqueFindings.Count);

        // A low-severity critique finding is redundant or trivially wrong content, not something a
        // parent would act on. Spending a second full model call on it, and risking a reviser that
        // rewrites more than it was asked to, costs more than the defect does.
        var worthRevising = validationFindings.Count > 0
            || critiqueFindings.Any(finding => finding.Severity != CritiqueSeverity.Low);

        if (!worthRevising)
        {
            _logger.LogInformation(
                "All critique findings were low severity; sending the draft digest unrevised.");
            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevision: null),
                0);
        }

        var revised = await _summaryGenerator.ReviseAsync(
            new SummaryRevisionRequest(
                markdown,
                FormatIssues(validationFindings, critiqueFindings),
                promptResult.UpcomingDates),
            ct);

        if (string.IsNullOrWhiteSpace(revised))
        {
            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevision: null),
                0);
        }

        var postRevisionFindings = _outputValidator.Validate(revised, todayLocal);

        if (postRevisionFindings.Count > validationFindings.Count)
        {
            _logger.LogWarning(
                "Discarding the revised digest: it carries {RevisedFindingCount} validation "
                + "finding(s) against the draft's {DraftFindingCount}.",
                postRevisionFindings.Count, validationFindings.Count);

            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevisionFindings),
                0);
        }

        _logger.LogInformation(
            "Revised digest accepted: validation findings went from {DraftFindingCount} to "
            + "{RevisedFindingCount}.",
            validationFindings.Count, postRevisionFindings.Count);

        return new DigestReview(
            revised,
            BuildCritiqueLog(validationFindings, critiqueFindings, revised: true, postRevisionFindings),
            1);
    }

    /// <summary>
    /// Renders both finding sets into the single prompt-ready block the reviser is given.
    /// </summary>
    /// <param name="validationFindings">Findings from the deterministic output validator.</param>
    /// <param name="critiqueFindings">Findings from the AI critic.</param>
    /// <returns>Prompt-ready text, empty when there are no findings at all.</returns>
    internal static string FormatIssues(
        IReadOnlyList<SummaryValidationFinding> validationFindings,
        IReadOnlyList<CritiqueFinding> critiqueFindings)
    {
        var builder = new StringBuilder();

        if (validationFindings.Count > 0)
        {
            builder.Append("Date and formatting defects found by the validator:\n");
            builder.Append(SummaryOutputValidator.FormatForRevisionPrompt(validationFindings));
            builder.Append('\n');
        }

        if (critiqueFindings.Count > 0)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            builder.Append("Factual defects found by the reviewer:\n");

            var position = 0;
            foreach (var finding in critiqueFindings)
            {
                position++;
                builder.Append(position).Append(". [").Append(finding.Severity).Append("] ")
                    .Append(finding.Kind).Append(": ").Append(finding.Problem);

                if (!string.IsNullOrWhiteSpace(finding.Quote))
                    builder.Append(" Text: \"").Append(finding.Quote).Append('"');

                if (!string.IsNullOrWhiteSpace(finding.SuggestedFix))
                    builder.Append(" Fix: ").Append(finding.SuggestedFix);

                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCritiqueLog(
        IReadOnlyList<SummaryValidationFinding> validationFindings,
        IReadOnlyList<CritiqueFinding> critiqueFindings,
        bool revised,
        IReadOnlyList<SummaryValidationFinding>? postRevision)
    {
        var entry = new CritiqueLogEntry(
            validationFindings.Select(ToLog).ToList(),
            critiqueFindings
                .Select(finding => new CritiqueFindingLog(
                    finding.Severity.ToString(),
                    finding.Kind,
                    finding.Quote,
                    finding.Problem,
                    finding.SuggestedFix))
                .ToList(),
            revised,
            postRevision?.Select(ToLog).ToList());

        return JsonSerializer.Serialize(entry, CritiqueLogSerializerOptions);

        static ValidationFindingLog ToLog(SummaryValidationFinding finding)
            => new(finding.Kind.ToString(), finding.LineNumber, finding.Excerpt, finding.Message);
    }

    /// <summary>
    /// Stamps every news item fed into this digest with the summary that reported it, so it is
    /// never handed to a later digest again.
    /// </summary>
    /// <remarks>
    /// The rows are re-read by id rather than mutated through the objects the generator returned.
    /// In production both services share one scoped context and the two are the same instances,
    /// but writing through a detached graph would silently persist nothing, and this is the write
    /// that stops a digest repeating last week's news.
    /// </remarks>
    private async Task MarkNewsItemsReportedAsync(
        SummaryPromptResult promptResult,
        int summaryId,
        CancellationToken ct)
    {
        var newsItemIds = promptResult.NewsItems.Select(newsItem => newsItem.Id).ToList();
        if (newsItemIds.Count == 0)
            return;

        var tracked = await _db.NewsItems
            .Where(newsItem => newsItemIds.Contains(newsItem.Id))
            .ToListAsync(ct);

        foreach (var newsItem in tracked)
        {
            newsItem.IncludedInSummaryId = summaryId;
        }

        _logger.LogInformation(
            "Marked {NewsItemCount} news item(s) as reported in summary {SummaryId}",
            tracked.Count, summaryId);
    }

    private async Task ProcessMessageAsync(Parent parent, Message message, CancellationToken ct)
    {
        _logger.LogInformation("Processing message {MessageId} from {FromName}",
            message.ExternalMessageId, message.FromName);

        List<NewsItem> persisted;

        try
        {
            // AI categorization
            var result = await _categorizer.CategorizeAsync(message, ct);
            var itemsToPersist = await BuildNewsItemsAsync(parent, message, result, ct);

            persisted = await PersistMessageProcessingAsync(parent, message, itemsToPersist, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message {MessageId}", message.ExternalMessageId);
            // Continue processing other messages
            return;
        }

        // Event extraction runs after the news item is committed, because a tracked event points
        // at the news item that announced it and needs its assigned id. It is deliberately outside
        // the transaction above: a failed extraction must not undo a stored news item, and the
        // dates it missed are recovered the next time that event is announced.
        foreach (var newsItem in persisted)
        {
            await ExtractEventsAsync(newsItem, ct);
        }
    }

    private async Task ExtractEventsAsync(NewsItem newsItem, CancellationToken ct)
    {
        try
        {
            var events = await _eventExtractor.ExtractAsync(newsItem, ct);

            if (events.Count > 0)
            {
                _logger.LogInformation(
                    "Extracted {EventCount} event(s) from news item {NewsItemId}",
                    events.Count, newsItem.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to extract events from news item {NewsItemId}; the news item is kept",
                newsItem.Id);
        }
    }

    private async Task<List<NewsItem>> BuildNewsItemsAsync(
        Parent parent,
        Message message,
        CategorizationResult result,
        CancellationToken ct)
    {
        var itemsByType = new Dictionary<SourceType, NewsItem>();

        // Route: newsletter URL, scrape and save
        if (result.HasNewsletterUrl && !string.IsNullOrWhiteSpace(result.NewsletterUrl))
        {
            var scrapedText = await _scraper.ScrapeAsync(result.NewsletterUrl, ct);

            if (!string.IsNullOrWhiteSpace(scrapedText))
            {
                itemsByType[SourceType.NewsletterUrl] = CreateNewsItem(
                    parent,
                    message,
                    SourceType.NewsletterUrl,
                    scrapedText,
                    result.Summary,
                    result.NewsletterUrl);
            }
            else
            {
                _logger.LogWarning(
                    "Scraper returned empty for URL {NewsletterUrl} (message {MessageId}); saving message text as fallback",
                    result.NewsletterUrl, message.ExternalMessageId);

                itemsByType[SourceType.MessageText] = CreateNewsItem(
                    parent,
                    message,
                    SourceType.MessageText,
                    message.MessageText,
                    result.Summary,
                    result.NewsletterUrl);
            }
        }

        // Route: direct news, save message text as news
        if (result.IsNewsItself && !itemsByType.ContainsKey(SourceType.MessageText))
        {
            itemsByType[SourceType.MessageText] = CreateNewsItem(
                parent,
                message,
                SourceType.MessageText,
                message.MessageText,
                result.Summary,
                newsletterUrl: null);
        }

        return itemsByType.Values.ToList();
    }

    /// <summary>
    /// Commits the news items a message produced and marks the message processed.
    /// </summary>
    /// <returns>
    /// The news items actually inserted by this call, with their database ids assigned. Items
    /// already stored for this message under the same source type are not returned, so a rerun
    /// does not re-extract events from content that was handled the first time.
    /// </returns>
    private async Task<List<NewsItem>> PersistMessageProcessingAsync(
        Parent parent,
        Message message,
        IReadOnlyCollection<NewsItem> itemsToPersist,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var existingTypes = await _db.NewsItems
            .Where(newsItem => newsItem.ParentId == parent.Id && newsItem.SourceMessageId == message.ExternalMessageId)
            .Select(newsItem => newsItem.SourceType)
            .ToListAsync(ct);

        var inserted = new List<NewsItem>();

        foreach (var newsItem in itemsToPersist.Where(newsItem => !existingTypes.Contains(newsItem.SourceType)))
        {
            _db.NewsItems.Add(newsItem);
            inserted.Add(newsItem);
            _logger.LogInformation("Saved {SourceType} news item for message {MessageId}", newsItem.SourceType, message.ExternalMessageId);
        }

        message.ProcessedAt = _timeProvider.GetUtcDateTime();
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return inserted;
    }

    private NewsItem CreateNewsItem(
        Parent parent,
        Message message,
        SourceType sourceType,
        string newsContent,
        string aiSummary,
        string? newsletterUrl)
    {
        var now = _timeProvider.GetUtcDateTime();

        return new NewsItem
        {
            ParentId = parent.Id,
            SourceMessageId = message.ExternalMessageId,
            SourceType = sourceType,
            NewsletterUrl = newsletterUrl,
            NewsContent = newsContent,
            AiSummary = aiSummary,
            FromName = message.FromName,
            StudentName = message.StudentName,
            SentAt = message.SentAt,
            AnalyzedAt = now,
            CreatedAt = now
        };
    }

    /// <summary>
    /// The digest that came out of the review stage and the record of what the review did.
    /// </summary>
    private sealed record DigestReview(string Markdown, string? CritiqueLog, int RevisionCount);

    private sealed record ValidationFindingLog(string Kind, int LineNumber, string Excerpt, string Message);

    private sealed record CritiqueFindingLog(
        string Severity,
        string Kind,
        string Quote,
        string Problem,
        string SuggestedFix);

    private sealed record CritiqueLogEntry(
        IReadOnlyList<ValidationFindingLog> ValidationFindings,
        IReadOnlyList<CritiqueFindingLog> CritiqueFindings,
        bool Revised,
        IReadOnlyList<ValidationFindingLog>? PostRevisionValidationFindings);
}
