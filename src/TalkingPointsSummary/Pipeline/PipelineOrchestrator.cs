using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Pipeline;

/// <summary>
/// Orchestrates the full weekly pipeline for a single parent:
/// Fetch → Dedup → Categorize → Scrape → Store News → Summarize → Email → Archive
/// </summary>
public class PipelineOrchestrator
{
    private readonly AppDbContext _db;
    private readonly ITalkingPointsApiClient _apiClient;
    private readonly IMessageDeduplicator _deduplicator;
    private readonly IMessageCategorizer _categorizer;
    private readonly INewsletterScraper _scraper;
    private readonly ISummaryGenerator _summaryGenerator;
    private readonly IMarkdownConverter _markdownConverter;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        AppDbContext db,
        ITalkingPointsApiClient apiClient,
        IMessageDeduplicator deduplicator,
        IMessageCategorizer categorizer,
        INewsletterScraper scraper,
        ISummaryGenerator summaryGenerator,
        IMarkdownConverter markdownConverter,
        IEmailSender emailSender,
        ILogger<PipelineOrchestrator> logger)
    {
        _db = db;
        _apiClient = apiClient;
        _deduplicator = deduplicator;
        _categorizer = categorizer;
        _scraper = scraper;
        _summaryGenerator = summaryGenerator;
        _markdownConverter = markdownConverter;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task RunAsync(Parent parent, CancellationToken ct = default)
    {
        _logger.LogInformation("=== Starting pipeline for parent: {ParentName} (ID: {ParentId}) ===",
            parent.Name, parent.Id);

        try
        {
            var lastSavedMessageId = await _db.Messages
                .Where(message => message.ParentId == parent.Id)
                .OrderByDescending(message => message.SentAt)
                .ThenByDescending(message => message.Id)
                .Select(message => message.ExternalMessageId)
                .FirstOrDefaultAsync(ct);

            // Step 1: Fetch messages from TalkingPoints API
            var apiMessages = await _apiClient.FetchMessagesAsync(parent, lastSavedMessageId, ct);

            // Step 2: Deduplicate and save new messages
            await _deduplicator.DeduplicateAndSaveAsync(parent, apiMessages, ct);

            // Step 3: Get all unprocessed messages
            var unprocessed = await _deduplicator.GetUnprocessedAsync(parent, ct);
            _logger.LogInformation("Found {Count} unprocessed messages for parent {ParentName}",
                unprocessed.Count, parent.Name);

            // Step 4: Categorize and route each unprocessed message
            foreach (var message in unprocessed)
            {
                await ProcessMessageAsync(parent, message, ct);
            }

            // Step 5: Generate weekly summary
            var markdown = await _summaryGenerator.GenerateAsync(parent, ct);
            if (markdown == null)
            {
                _logger.LogInformation("No summary generated for parent {ParentName} (no news items)", parent.Name);
                return;
            }

            // Step 6: Convert to HTML
            var html = _markdownConverter.ToHtml(markdown);

            // Step 7: Send email
            await _emailSender.SendAsync(
                parent.EmailRecipients,
                "Talking Points Summary V2",
                html,
                ct);

            // Step 8: Archive the summary
            var summary = new Summary
            {
                ParentId = parent.Id,
                Content = markdown,
                CreatedAt = DateTime.UtcNow
            };
            _db.Summaries.Add(summary);
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

    private async Task ProcessMessageAsync(Parent parent, Message message, CancellationToken ct)
    {
        _logger.LogInformation("Processing message {MessageId} from {FromName}",
            message.ExternalMessageId, message.FromName);

        try
        {
            // AI categorization
            var result = await _categorizer.CategorizeAsync(message, ct);
            var itemsToPersist = await BuildNewsItemsAsync(parent, message, result, ct);

            await PersistMessageProcessingAsync(parent, message, itemsToPersist, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message {MessageId}", message.ExternalMessageId);
            // Continue processing other messages
        }
    }

    private async Task<List<NewsItem>> BuildNewsItemsAsync(
        Parent parent,
        Message message,
        CategorizationResult result,
        CancellationToken ct)
    {
        var itemsByType = new Dictionary<SourceType, NewsItem>();

        // Route: newsletter URL → scrape and save
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

        // Route: direct news → save message text as news
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

    private async Task PersistMessageProcessingAsync(
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

        foreach (var newsItem in itemsToPersist.Where(newsItem => !existingTypes.Contains(newsItem.SourceType)))
        {
            _db.NewsItems.Add(newsItem);
            _logger.LogInformation("Saved {SourceType} news item for message {MessageId}", newsItem.SourceType, message.ExternalMessageId);
        }

        message.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static NewsItem CreateNewsItem(
        Parent parent,
        Message message,
        SourceType sourceType,
        string newsContent,
        string aiSummary,
        string? newsletterUrl)
    {
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
            AnalyzedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
    }
}
