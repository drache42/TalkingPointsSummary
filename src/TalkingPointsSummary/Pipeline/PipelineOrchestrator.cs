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

    /// <summary>
    /// Smallest batch a truncation retry will shrink to. Below this the prompt is no longer the
    /// problem, so shrinking further only produces an ever more threadbare digest.
    /// </summary>
    internal const int MinNewsItemsPerDigest = 4;

    /// <summary>
    /// Shortest a revision may be, as a fraction of the draft it corrects, before it is rejected
    /// as content loss rather than accepted as a correction.
    /// </summary>
    /// <remarks>
    /// Fixing a weekday, deleting an unsupported sentence, or dropping a past date changes a digest
    /// by a line or two. A response that comes back at less than half the length is a preamble, a
    /// refusal, or a rewrite that threw the week's news away, and the validator cannot see that:
    /// text that is not there states no wrong dates, so it scores zero findings and would win the
    /// straight count comparison against a draft that had two.
    /// </remarks>
    internal const double MinRevisionLengthRatio = 0.5;

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

            // Steps 5 to 7: build the prompt, persist it, and generate, shrinking the batch and
            // retrying if the model stops at its token ceiling.
            var generated = await GenerateDigestAsync(parent, ct);
            if (generated is null)
                return;

            var (summary, promptResult, markdown) = generated.Value;

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
            await MarkNewsItemsReportedAsync(promptResult, review.OmittedNewsItemIds, summary.Id, ct);
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
    /// Builds the prompt, saves the prompt row, and generates the draft digest, halving the number
    /// of news items and trying again when the model stops at its token ceiling.
    /// </summary>
    /// <remarks>
    /// Eligibility is driven by <see cref="NewsItem.IncludedInSummaryId"/>, so an item stays
    /// eligible until a digest carrying it is delivered. A backlog sitting at the generator's row
    /// cap therefore selects the same rows on every run: if that set truncates once it truncates
    /// forever, and the parent never receives another digest without manual intervention. Halving
    /// the batch trades a shorter digest this week for a delivery that actually drains the
    /// backlog. The retry costs a second model call, which is worth it once a week to break a
    /// stall that is otherwise permanent.
    /// </remarks>
    /// <returns>
    /// The saved summary row, the prompt it was built from, and the draft markdown, or
    /// <see langword="null"/> when there is nothing to summarize or the model returned nothing.
    /// </returns>
    private async Task<(Summary Summary, SummaryPromptResult PromptResult, string Markdown)?> GenerateDigestAsync(
        Parent parent,
        CancellationToken ct)
    {
        Summary? summary = null;
        int? itemLimit = null;

        while (true)
        {
            // Step 5: Build summary prompt
            var promptResult = await _summaryGenerator.BuildPromptAsync(parent, ct, itemLimit);
            if (promptResult == null)
            {
                _logger.LogInformation("No summary generated for parent {ParentName} (no news items)", parent.Name);
                return null;
            }

            // Step 6: Persist prompt row before the AI call so it is always saved. A retry reuses
            // the same row rather than leaving a trail of abandoned prompts behind it.
            if (summary is null)
            {
                summary = new Summary
                {
                    ParentId = parent.Id,
                    Prompt = promptResult.Prompt,
                    Content = null,
                    CreatedAt = _timeProvider.GetUtcDateTime()
                };
                _db.Summaries.Add(summary);
            }
            else
            {
                summary.Prompt = promptResult.Prompt;
            }

            await _db.SaveChangesAsync(ct);

            // Step 7: Execute AI to produce Markdown
            string? markdown;

            try
            {
                markdown = await _summaryGenerator.ExecutePromptAsync(promptResult.Prompt, ct);
            }
            catch (AiResponseTruncatedException ex) when (promptResult.NewsItemCount > MinNewsItemsPerDigest)
            {
                itemLimit = Math.Max(promptResult.NewsItemCount / 2, MinNewsItemsPerDigest);

                _logger.LogWarning(ex,
                    "Digest for parent {ParentName} was truncated with {NewsItemCount} news items; "
                    + "rebuilding it with the oldest {ReducedCount} so the backlog can still drain.",
                    parent.Name, promptResult.NewsItemCount, itemLimit);

                continue;
            }

            if (string.IsNullOrEmpty(markdown))
            {
                _logger.LogWarning("AI returned empty summary for parent {ParentName}; prompt row saved for debugging", parent.Name);
                return null;
            }

            return (summary, promptResult, markdown);
        }
    }

    /// <summary>
    /// Runs the deterministic validator and the AI critic over the draft, and applies a single
    /// correction pass when either reports something worth correcting.
    /// </summary>
    /// <remarks>
    /// Review never blocks a send. A revision replaces the draft only when it survives
    /// <see cref="IsUsableRevision"/>, which asks whether the response is still a digest at all,
    /// and then scores no worse than the draft by the validator's own count. Neither test alone is
    /// enough: a count comparison cannot see content that is simply absent, and a shape check
    /// cannot see a wrong weekday.
    /// </remarks>
    private async Task<DigestReview> ReviewAsync(
        SummaryPromptResult promptResult,
        string markdown,
        CancellationToken ct)
    {
        var todayLocal = TimeZoneInfo
            .ConvertTimeFromUtc(_timeProvider.GetUtcDateTime(), _scheduleTimeZone)
            .Date;

        // The rendered upcoming-dates block travels into validation, so a digest that dropped or
        // rewrote the section the pipeline rendered for it is reported rather than passed as clean.
        var validationFindings = _outputValidator.Validate(
            markdown, todayLocal, promptResult.UpcomingDates);

        var critiqueFindings = await _critic.CritiqueAsync(
            new SummaryCritiqueRequest(
                promptResult.NewsItems,
                markdown,
                promptResult.UpcomingDates,
                promptResult.CoverageLedger),
            ct);

        // The critic runs once, against this draft. A revision may only correct wording, dates,
        // and formatting the reviser is explicitly told about below; it is never asked to add or
        // remove whole items, so which news items actually reached the reader does not change
        // between the draft and whatever markdown this method ultimately returns.
        var omittedNewsItemIds = ResolveOmittedNewsItemIds(critiqueFindings, promptResult.NewsItems);

        // Bookkeeping, not a defect: omitting the weakest items to respect the digest's length and
        // bullet caps is expected of the writer. Feeding it to the reviser would just invite it to
        // cram dropped items back in, so it never reaches the revision decision or its prompt.
        var revisableCritiqueFindings = critiqueFindings
            .Where(finding => finding.Kind != CritiqueFindingKinds.OmittedItem)
            .ToList();

        if (validationFindings.Count == 0 && revisableCritiqueFindings.Count == 0)
        {
            _logger.LogInformation("Draft digest passed validation and critique with no findings.");
            return new DigestReview(markdown, null, 0, omittedNewsItemIds);
        }

        _logger.LogInformation(
            "Draft digest review found {ValidationFindingCount} validation finding(s) and "
            + "{CritiqueFindingCount} critique finding(s).",
            validationFindings.Count, revisableCritiqueFindings.Count);

        // A low-severity critique finding is redundant or trivially wrong content, not something a
        // parent would act on. Spending a second full model call on it, and risking a reviser that
        // rewrites more than it was asked to, costs more than the defect does.
        var worthRevising = validationFindings.Count > 0
            || revisableCritiqueFindings.Any(finding => finding.Severity != CritiqueSeverity.Low);

        if (!worthRevising)
        {
            _logger.LogInformation(
                "All critique findings were low severity; sending the draft digest unrevised.");
            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevision: null),
                0,
                omittedNewsItemIds);
        }

        var revised = await _summaryGenerator.ReviseAsync(
            new SummaryRevisionRequest(
                markdown,
                FormatIssues(validationFindings, revisableCritiqueFindings),
                promptResult.UpcomingDates),
            ct);

        if (string.IsNullOrWhiteSpace(revised))
        {
            // ReviseAsync reports every failure this way: a truncated response, a refusal, an empty
            // completion, or a provider outage. The draft is intact, so it is what goes out.
            _logger.LogInformation(
                "No usable revision came back; sending the draft digest as generated.");

            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevision: null),
                0,
                omittedNewsItemIds);
        }

        if (!IsUsableRevision(markdown, revised, out var rejection))
        {
            _logger.LogWarning(
                "Discarding the revised digest: {Reason} Sending the draft digest as generated.",
                rejection);

            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevision: null),
                0,
                omittedNewsItemIds);
        }

        var postRevisionFindings = _outputValidator.Validate(
            revised, todayLocal, promptResult.UpcomingDates);

        if (postRevisionFindings.Count > validationFindings.Count)
        {
            _logger.LogWarning(
                "Discarding the revised digest: it carries {RevisedFindingCount} validation "
                + "finding(s) against the draft's {DraftFindingCount}.",
                postRevisionFindings.Count, validationFindings.Count);

            return new DigestReview(
                markdown,
                BuildCritiqueLog(validationFindings, critiqueFindings, revised: false, postRevisionFindings),
                0,
                omittedNewsItemIds);
        }

        _logger.LogInformation(
            "Revised digest accepted: validation findings went from {DraftFindingCount} to "
            + "{RevisedFindingCount}.",
            validationFindings.Count, postRevisionFindings.Count);

        return new DigestReview(
            revised,
            BuildCritiqueLog(validationFindings, critiqueFindings, revised: true, postRevisionFindings),
            1,
            omittedNewsItemIds);
    }

    /// <summary>
    /// Maps the critic's omitted-item findings onto the actual news item ids they refer to.
    /// </summary>
    /// <remarks>
    /// The critic names items by their 1-based "SOURCE ITEM N" position, the same order and
    /// numbering <see cref="SummaryPromptResult.NewsItems"/> was rendered in for both the digest
    /// prompt and the critique prompt. <see cref="SummaryCritic"/> has already range-checked the
    /// number against the source item count it was given, but that count is validated against the
    /// request the critic itself received; this defends the same way against a number that is
    /// in-range but does not line up with this particular <paramref name="newsItems"/> list.
    /// </remarks>
    private static IReadOnlyList<int> ResolveOmittedNewsItemIds(
        IReadOnlyList<CritiqueFinding> critiqueFindings,
        IReadOnlyList<NewsItem> newsItems)
    {
        List<int>? omitted = null;

        foreach (var finding in critiqueFindings)
        {
            if (finding.Kind != CritiqueFindingKinds.OmittedItem || finding.SourceItemNumber is not int number)
                continue;

            var index = number - 1;
            if (index < 0 || index >= newsItems.Count)
                continue;

            omitted ??= [];
            omitted.Add(newsItems[index].Id);
        }

        return omitted ?? [];
    }

    /// <summary>
    /// Reports whether a revision is still recognizably the digest it was asked to correct.
    /// </summary>
    /// <remarks>
    /// The validator reads a digest on its own terms: it counts wrong weekdays and past dates, and
    /// it has nothing to say about a response that is not a digest at all. "Here is the corrected
    /// digest:" stops with an ordinary end_turn, is not empty, and scores zero findings, so on a
    /// straight count comparison it beats a draft with two and gets emailed as the week's news,
    /// stamping every news item the critic did not separately flag as omitted from the draft it
    /// replaced, even though almost none of that draft's content survived into what was actually
    /// sent. This is the check that stops that: a correction pass is allowed to fix lines, not to
    /// throw the digest away.
    /// </remarks>
    /// <param name="draft">The draft digest the revision was asked to correct.</param>
    /// <param name="revised">The revision that came back.</param>
    /// <param name="rejection">Why the revision was rejected, when it was.</param>
    /// <returns><c>true</c> when the revision may replace the draft.</returns>
    internal static bool IsUsableRevision(string draft, string revised, out string rejection)
    {
        var minimumLength = (int)(draft.Length * MinRevisionLengthRatio);

        if (revised.Length < minimumLength)
        {
            rejection =
                $"it is {revised.Length} characters against the draft's {draft.Length}, "
                + $"below the {minimumLength} a correction pass should leave standing.";
            return false;
        }

        // A digest is a set of headed sections. A response carrying none of them is prose about the
        // digest rather than the digest itself, however long it is.
        if (CountHeadings(draft) > 0 && CountHeadings(revised) == 0)
        {
            rejection = "it carries no markdown headings at all while the draft does.";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    private static int CountHeadings(string markdown)
    {
        var count = 0;

        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith('#'))
                count++;
        }

        return count;
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
    /// Stamps every news item that actually reached the reader in this digest with the summary
    /// that reported it, so it is never handed to a later digest again.
    /// </summary>
    /// <remarks>
    /// Being fed into the prompt is not enough: <paramref name="omittedNewsItemIds"/> names the
    /// items the critic found no trace of anywhere in the sent markdown, and those are left with
    /// <see cref="NewsItem.IncludedInSummaryId"/> unset so <c>BuildPromptAsync</c>'s eligibility
    /// filter picks them up again next run. Marking a fed-in item as reported regardless of whether
    /// its content survived into the prose would drop it from every future digest the moment the
    /// model dropped it once, permanently, with no parent ever having read it.
    /// <para>
    /// The rows are re-read by id rather than mutated through the objects the generator returned.
    /// In production both services share one scoped context and the two are the same instances,
    /// but writing through a detached graph would silently persist nothing, and this is the write
    /// that stops a digest repeating last week's news.
    /// </para>
    /// </remarks>
    private async Task MarkNewsItemsReportedAsync(
        SummaryPromptResult promptResult,
        IReadOnlyList<int> omittedNewsItemIds,
        int summaryId,
        CancellationToken ct)
    {
        var omitted = omittedNewsItemIds.Count == 0
            ? null
            : new HashSet<int>(omittedNewsItemIds);

        var newsItemIds = promptResult.NewsItems
            .Select(newsItem => newsItem.Id)
            .Where(id => omitted is null || !omitted.Contains(id))
            .ToList();

        if (omitted is { Count: > 0 })
        {
            _logger.LogInformation(
                "Leaving {OmittedCount} news item(s) unreported for summary {SummaryId}: the critic "
                + "found no trace of them in the sent digest, so they carry into next week's.",
                omitted.Count, summaryId);
        }

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller is shutting the run down. That is not an extraction failure to absorb.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to extract events from news item {NewsItemId} (parent {ParentId}); "
                + "the news item is kept and its dates are recovered when the event is announced again",
                newsItem.Id, newsItem.ParentId);

            await DiscardPendingTrackedEventChangesAsync(ct);
        }
    }

    /// <summary>
    /// Rolls back whatever the failed extraction left pending in the change tracker.
    /// </summary>
    /// <remarks>
    /// A failed SaveChanges leaves its entities Added or Modified, and the very next SaveChanges on
    /// the same context retries them. This context is shared by every parent in the run, so without
    /// this the failure escapes the news item it belongs to: the next save is the one that persists
    /// this parent's digest, and it, and every later parent's, would throw on the same orphaned
    /// row. Swallowing the extraction error is only safe if nothing survives it.
    /// </remarks>
    private async Task DiscardPendingTrackedEventChangesAsync(CancellationToken ct)
    {
        foreach (var entry in _db.ChangeTracker.Entries<TrackedEvent>().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
                continue;
            }

            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
                continue;

            try
            {
                // Reload rather than just marking it unchanged: the in-memory row carries the
                // half-applied status change, and a later query in this same run would be handed
                // that tracked instance instead of what the database actually holds.
                await entry.ReloadAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not reload tracked event {EventId} after a failed extraction; detaching it",
                    entry.Entity.Id);

                entry.State = EntityState.Detached;
            }
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
    /// <param name="Markdown">The digest markdown to send, either the draft or an accepted revision.</param>
    /// <param name="CritiqueLog">
    /// The rendered validation and critique findings, ready to persist on the summary row, or
    /// <see langword="null"/> when the draft passed with nothing to report.
    /// </param>
    /// <param name="RevisionCount">
    /// <c>1</c> when a revision was generated and accepted in place of the draft, otherwise <c>0</c>.
    /// </param>
    /// <param name="OmittedNewsItemIds">
    /// Ids of news items the critic found no trace of anywhere in the digest, fed into the prompt
    /// but never actually reported. These are excluded when the pipeline stamps
    /// <see cref="NewsItem.IncludedInSummaryId"/>, so they roll into next week's digest instead of
    /// being marked delivered for content the parent never received.
    /// </param>
    private sealed record DigestReview(
        string Markdown,
        string? CritiqueLog,
        int RevisionCount,
        IReadOnlyList<int> OmittedNewsItemIds);

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
