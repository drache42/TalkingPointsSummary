using Microsoft.EntityFrameworkCore;
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
    private readonly TalkingPointsApiClient _apiClient;
    private readonly MessageDeduplicator _deduplicator;
    private readonly MessageCategorizer _categorizer;
    private readonly NewsletterScraper _scraper;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly MarkdownConverter _markdownConverter;
    private readonly EmailSender _emailSender;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        AppDbContext db,
        TalkingPointsApiClient apiClient,
        MessageDeduplicator deduplicator,
        MessageCategorizer categorizer,
        NewsletterScraper scraper,
        SummaryGenerator summaryGenerator,
        MarkdownConverter markdownConverter,
        EmailSender emailSender,
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
            // Step 1: Fetch messages from TalkingPoints API
            var apiMessages = await _apiClient.FetchMessagesAsync(parent, ct);

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
                "Talking Points Summary",
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

            // Route: newsletter URL → scrape and save
            if (result.HasNewsletterUrl && !string.IsNullOrWhiteSpace(result.NewsletterUrl))
            {
                var scrapedText = await _scraper.ScrapeAsync(result.NewsletterUrl, ct);

                if (!string.IsNullOrWhiteSpace(scrapedText))
                {
                    var newsItem = new NewsItem
                    {
                        ParentId = parent.Id,
                        SourceMessageId = message.ExternalMessageId,
                        SourceType = SourceType.NewsletterUrl,
                        NewsletterUrl = result.NewsletterUrl,
                        NewsContent = scrapedText,
                        AiSummary = result.Summary,
                        FromName = message.FromName,
                        StudentName = message.StudentName,
                        SentAt = message.SentAt,
                        AnalyzedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.NewsItems.Add(newsItem);
                    await _db.SaveChangesAsync(ct);
                    _logger.LogInformation("Saved newsletter news item for message {MessageId}", message.ExternalMessageId);
                }
            }

            // Route: direct news → save message text as news
            if (result.IsNewsItself)
            {
                var newsItem = new NewsItem
                {
                    ParentId = parent.Id,
                    SourceMessageId = message.ExternalMessageId,
                    SourceType = SourceType.MessageText,
                    NewsContent = message.MessageText,
                    AiSummary = result.Summary,
                    FromName = message.FromName,
                    StudentName = message.StudentName,
                    SentAt = message.SentAt,
                    AnalyzedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                _db.NewsItems.Add(newsItem);
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Saved direct news item for message {MessageId}", message.ExternalMessageId);
            }

            // Mark message as processed
            await _deduplicator.MarkProcessedAsync(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message {MessageId}", message.ExternalMessageId);
            // Continue processing other messages
        }
    }
}
