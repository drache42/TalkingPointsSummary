using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.IntegrationTests;

/// <summary>
/// End-to-end integration tests for the full weekly pipeline.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class PipelineEndToEndTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    /// <summary>
    /// Initializes a new end-to-end pipeline test suite.
    /// </summary>
    /// <param name="fixture">Shared integration-test fixture.</param>
    public PipelineEndToEndTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Resets shared test state before each test.
    /// </summary>
    public async Task InitializeAsync() => await _fixture.ResetAsync();

    /// <summary>
    /// Completes per-test cleanup.
    /// </summary>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Verifies that the happy path sends email and saves processed data.
    /// </summary>
    [Fact]
    public async Task FullPipeline_HappyPath_SendsEmailAndSavesSummary()
    {
        const string summaryMarkdown = "# Weekly Summary\n\nSchool play next week! Field trip to the museum on Friday.";

        // Arrange: stub TalkingPoints API with 2 messages
        var messages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("msg-1", "Mrs. Teacher", "Alice", "School play next week! All students invited."),
            CreateApiMessage("msg-2", "District Principal", "Bob", "Check the newsletter for field trip info", signature: "Principal Jordan"),
        };
        _fixture.StubTalkingPointsApi(messages);

        // Register a newsletter page on the content server
        var newsletterUrl = $"{_fixture.ContentServerUrl}/newsletter.html";
        _fixture.RegisterContentPage("newsletter.html", "<body>Field trip to the museum on Friday</body>");

        // Stub Anthropic categorization: first call for msg-1 (IsNewsItself), second for msg-2 (HasNewsletterUrl)
        // Then a third call for summary generation
        _fixture.StubAnthropicCategorizationForMessage("msg-1",
            AnthropicStubResponse.Ok($$"""{"message_id":"msg-1","has_newsletter_url":false,"newsletter_url":null,"is_news_itself":true,"summary":"School play announced"}"""));
        _fixture.StubAnthropicCategorizationForMessage("msg-2",
            AnthropicStubResponse.Ok($$"""{"message_id":"msg-2","has_newsletter_url":true,"newsletter_url":"{{newsletterUrl}}","is_news_itself":false,"summary":"Field trip info"}"""));
        _fixture.StubAnthropicSummary(summaryMarkdown);

        // Act
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbMessages = await db.Messages.Where(m => m.ParentId == _fixture.SeededParentId).ToListAsync();
        dbMessages.Should().HaveCount(2);
        dbMessages.Should().AllSatisfy(m => m.ProcessedAt.Should().NotBeNull());
        dbMessages.Should().ContainSingle(m => m.ExternalMessageId == "msg-2").Which.FromName.Should().Be("Principal Jordan");
        dbMessages.Should().ContainSingle(m => m.ExternalMessageId == "msg-2").Which.StudentName.Should().Be("Bob");

        var newsItems = await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync();
        newsItems.Should().HaveCount(2);
        var messageTextNews = newsItems.Should().ContainSingle(n => n.SourceType == SourceType.MessageText).Which;
        messageTextNews.SourceMessageId.Should().Be("msg-1");
        messageTextNews.NewsContent.Should().Be("School play next week! All students invited.");
        messageTextNews.FromName.Should().Be("Mrs. Teacher");
        messageTextNews.StudentName.Should().Be("Alice");

        var newsletterNews = newsItems.Should().ContainSingle(n => n.SourceType == SourceType.NewsletterUrl).Which;
        newsletterNews.SourceMessageId.Should().Be("msg-2");
        newsletterNews.NewsletterUrl.Should().Be(newsletterUrl);
        newsletterNews.NewsContent.Should().Be("Field trip to the museum on Friday");
        newsletterNews.FromName.Should().Be("Principal Jordan");
        newsletterNews.StudentName.Should().Be("Bob");

        var summaries = await db.Summaries.Where(s => s.ParentId == _fixture.SeededParentId).ToListAsync();
        var summary = summaries.Should().ContainSingle().Which;
        summary.ParentId.Should().Be(_fixture.SeededParentId);
        summary.Content.Should().Be(summaryMarkdown);

        // Check Mailpit
        var mailCount = await _fixture.GetMailpitMessageCountAsync();
        mailCount.Should().Be(1);

        var mailMessages = await _fixture.GetMailpitMessagesAsync();
        mailMessages[0].Subject.Should().Be("Talking Points Summary");
        mailMessages[0].To.Should().Contain("test@example.com");

        var htmlBody = await _fixture.GetMailpitMessageHtmlAsync(mailMessages[0].Id);
        htmlBody.Should().Contain("School play next week!");
        htmlBody.Should().Contain("Field trip to the museum on Friday.");
    }

    /// <summary>
    /// Covers the parts of the pipeline that only exist end to end: a news item is committed, its
    /// events are extracted against the real schema, the digest prompt is rendered from the stored
    /// events, and delivery closes the item out.
    /// </summary>
    [Fact]
    public async Task FullPipeline_ExtractsEventsRendersTheDatesAndClosesOutTheNewsItem()
    {
        const string summaryMarkdown =
            "# Weekly Summary\n\nThe book fair is coming.\n\n"
            + "## Important Upcoming Dates\n\n### Lincoln Elementary (Alice, Bob)\n"
            + "- **Wednesday, October 14, 2026** - Book Fair (9:00 AM)\n";

        _fixture.StubTalkingPointsApi([
            CreateApiMessage("events-1", "Mrs. Teacher", "Alice", "Book Fair is on October 14 at 9:00 AM.")
        ]);

        _fixture.StubAnthropicCategorizationForMessage("events-1",
            AnthropicStubResponse.Ok("""{"message_id":"events-1","has_newsletter_url":false,"is_news_itself":true,"summary":"Book Fair"}"""));

        _fixture.StubAnthropicEventExtraction("""
            {
              "events": [
                { "title": "Book Fair", "event_date": "2026-10-14", "time_text": "9:00 AM" }
              ],
              "cancelled_event_ids": [],
              "reinstated_event_ids": []
            }
            """);

        _fixture.StubAnthropicSummary(summaryMarkdown);

        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var trackedEvent = await db.TrackedEvents
            .SingleAsync(e => e.ParentId == _fixture.SeededParentId);
        trackedEvent.Title.Should().Be("Book Fair");
        trackedEvent.EventDate.Should().Be(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
        trackedEvent.School.Should().Be("Lincoln Elementary");
        trackedEvent.Status.Should().Be(TrackedEventStatus.Active);

        var newsItem = await db.NewsItems.SingleAsync(n => n.ParentId == _fixture.SeededParentId);
        trackedEvent.SourceNewsItemId.Should().Be(newsItem.Id);

        var summary = await db.Summaries.SingleAsync(s => s.ParentId == _fixture.SeededParentId);

        // The dates section is rendered in C# from the stored event and handed to the model to
        // copy, so it has to be in the prompt rather than left for the model to reconstruct.
        summary.Prompt.Should().Contain("- **Wednesday, October 14, 2026** - Book Fair (9:00 AM)");
        summary.Content.Should().Be(summaryMarkdown);
        summary.EmailSentAt.Should().NotBeNull();

        // Only a delivered digest closes out the news it reported.
        newsItem.IncludedInSummaryId.Should().Be(summary.Id);

        (await _fixture.GetMailpitMessageCountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Verifies that duplicate messages are not reprocessed on later runs.
    /// </summary>
    [Fact]
    public async Task FullPipeline_Deduplication_DoesNotReprocessExistingMessages()
    {
        const string summaryMarkdown = "# Summary\n\nPicture day is Friday!";

        // Arrange
        var messages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("dedup-msg-1", "Mrs. Smith", "Alice", "Picture day is Friday!"),
        };
        _fixture.StubTalkingPointsApi(messages);

        // Categorization + summary for first run
        _fixture.StubAnthropicCategorizationForMessage("dedup-msg-1",
            AnthropicStubResponse.Ok("""{"message_id":"dedup-msg-1","has_newsletter_url":false,"is_news_itself":true,"summary":"Picture day"}"""));
        _fixture.StubAnthropicSummary(summaryMarkdown);

        // First run
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Reset WireMock logs/stubs but NOT the database
        _fixture.WireMock.Reset();

        // Stub same message again for second run
        _fixture.StubTalkingPointsApi(messages);
        // Stub Anthropic again (categorization + summary) — dedup should prevent categorization call
        _fixture.StubAnthropicSummary(summaryMarkdown);

        // Second run
        await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert: still exactly 1 Message
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbMessages = await db.Messages.Where(m => m.ParentId == _fixture.SeededParentId).ToListAsync();
        dbMessages.Should().HaveCount(1);

        var newsItems = await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync();
        newsItems.Should().HaveCount(1);

        // Eligibility is the recorded fact that an item has not been reported yet. The first run
        // delivered this story and stamped it, so the second run has nothing left to write about
        // and produces no digest at all. Under the old date window it produced a second digest
        // repeating the same story, which is the defect that stamping exists to remove.
        newsItems[0].IncludedInSummaryId.Should().NotBeNull();

        var summaries = await db.Summaries.Where(s => s.ParentId == _fixture.SeededParentId).OrderBy(s => s.CreatedAt).ToListAsync();
        summaries.Should().ContainSingle();
        summaries[0].Content.Should().Be(summaryMarkdown);
        summaries[0].EmailSentAt.Should().NotBeNull();
        newsItems[0].IncludedInSummaryId.Should().Be(summaries[0].Id);

        var mailCount = await _fixture.GetMailpitMessageCountAsync();
        mailCount.Should().Be(1, "the second run had no unreported news to send");
    }

    /// <summary>
    /// Verifies that one message can create both direct-message and newsletter news items.
    /// </summary>
    [Fact]
    public async Task FullPipeline_MessageHasNewsAndNewsletterLink_SavesBothSourceTypes()
    {
        var newsletterUrl = $"{_fixture.ContentServerUrl}/both-news.html";
        _fixture.RegisterContentPage("both-news.html", "<body>Full schedule and logistics</body>");

        _fixture.StubTalkingPointsApi([
            CreateApiMessage("both-1", "Mrs. Teacher", "Alice", "Picture day is Friday. Full details in the newsletter.")
        ]);

        _fixture.StubAnthropicCategorizationForMessage("both-1",
            AnthropicStubResponse.Ok($$"""{"message_id":"both-1","has_newsletter_url":true,"newsletter_url":"{{newsletterUrl}}","is_news_itself":true,"summary":"Picture day and newsletter"}"""));
        _fixture.StubAnthropicSummary("# Summary\n\nPicture day is Friday.");

        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var newsItems = await db.NewsItems
            .Where(newsItem => newsItem.ParentId == _fixture.SeededParentId && newsItem.SourceMessageId == "both-1")
            .OrderBy(newsItem => newsItem.SourceType)
            .ToListAsync();

        newsItems.Should().HaveCount(2);
        newsItems.Should().ContainSingle(newsItem => newsItem.SourceType == SourceType.MessageText)
            .Which.NewsContent.Should().Be("Picture day is Friday. Full details in the newsletter.");
        newsItems.Should().ContainSingle(newsItem => newsItem.SourceType == SourceType.NewsletterUrl)
            .Which.NewsContent.Should().Be("Full schedule and logistics");
    }

    /// <summary>
    /// Verifies that rerunning an unprocessed message only adds the missing source type.
    /// </summary>
    [Fact]
    public async Task FullPipeline_UnprocessedMessageWithExistingNewsletterItem_AddsOnlyMissingSourceType()
    {
        var newsletterUrl = $"{_fixture.ContentServerUrl}/retry-news.html";
        _fixture.RegisterContentPage("retry-news.html", "<body>Newsletter content</body>");

        await using (var seedProvider = _fixture.CreateServiceProvider())
        await using (var seedScope = seedProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.Messages.Add(new Message
            {
                ParentId = _fixture.SeededParentId,
                ExternalMessageId = "retry-1",
                FromName = "Mrs. Teacher",
                StudentName = "Alice",
                MessageText = "Picture day is Friday. Full details in the newsletter.",
                SentAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                ProcessedAt = null,
            });
            seedDb.NewsItems.Add(new NewsItem
            {
                ParentId = _fixture.SeededParentId,
                SourceMessageId = "retry-1",
                SourceType = SourceType.NewsletterUrl,
                NewsletterUrl = newsletterUrl,
                NewsContent = "Newsletter content",
                AiSummary = "Existing newsletter",
                FromName = "Mrs. Teacher",
                StudentName = "Alice",
                SentAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                AnalyzedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        _fixture.StubTalkingPointsApi([]);
        _fixture.StubAnthropicCategorizationForMessage("retry-1",
            AnthropicStubResponse.Ok($$"""{"message_id":"retry-1","has_newsletter_url":true,"newsletter_url":"{{newsletterUrl}}","is_news_itself":true,"summary":"Picture day and newsletter"}"""));
        _fixture.StubAnthropicSummary("# Summary\n\nPicture day is Friday.");

        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var newsItems = await db.NewsItems
            .Where(newsItem => newsItem.ParentId == _fixture.SeededParentId && newsItem.SourceMessageId == "retry-1")
            .ToListAsync();

        newsItems.Should().HaveCount(2);
        newsItems.Count(newsItem => newsItem.SourceType == SourceType.NewsletterUrl).Should().Be(1);
        newsItems.Count(newsItem => newsItem.SourceType == SourceType.MessageText).Should().Be(1);

        var message = await db.Messages.SingleAsync(m => m.ParentId == _fixture.SeededParentId && m.ExternalMessageId == "retry-1");
        message.ProcessedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that persistence failures roll back news-item writes and processed state.
    /// </summary>
    [Fact]
    public async Task FullPipeline_SaveFailure_RollsBackNewsItemsAndProcessedState()
    {
        _fixture.StubTalkingPointsApi([
            CreateApiMessage("rollback-1", "Mrs. Teacher", "Alice", "Picture day is Friday")
        ]);

        _fixture.StubAnthropicCategorizationForMessage("rollback-1",
            AnthropicStubResponse.Ok("""{"message_id":"rollback-1","has_newsletter_url":false,"is_news_itself":true,"summary":"Picture day"}"""));
        _fixture.StubAnthropicSummary("# Summary\n\nPicture day is Friday.");

        await using var sp = _fixture.CreateServiceProvider(services =>
        {
            services.AddDbContext<FailingAppDbContext>(options =>
                options.UseNpgsql(_fixture.PostgresConnectionString,
                    npgsql => npgsql.MigrationsAssembly("TalkingPointsSummary")));
            services.AddScoped<AppDbContext>(serviceProvider => serviceProvider.GetRequiredService<FailingAppDbContext>());
        });

        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var newsItems = await db.NewsItems.Where(newsItem => newsItem.SourceMessageId == "rollback-1").ToListAsync();
        newsItems.Should().BeEmpty();

        var message = await db.Messages.SingleAsync(m => m.ExternalMessageId == "rollback-1" && m.ParentId == _fixture.SeededParentId);
        message.ProcessedAt.Should().BeNull();
    }

    /// <summary>
    /// Verifies that scraper failures fall back to the original message text.
    /// </summary>
    [Fact]
    public async Task FullPipeline_NewsletterScraperFails_FallsBackToMessageText()
    {
        // Arrange
        var messages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("scrape-fail-1", "Mrs. Johnson", "Alice", "Please read the newsletter for upcoming events"),
        };
        _fixture.StubTalkingPointsApi(messages);

        // Use a malformed URL so Browserless returns an error and the scraper falls back.
        var badUrl = "not-a-valid-url";

        _fixture.StubAnthropicCategorizationForMessage("scrape-fail-1",
            AnthropicStubResponse.Ok($$"""{"message_id":"scrape-fail-1","has_newsletter_url":true,"newsletter_url":"{{badUrl}}","is_news_itself":false,"summary":"Newsletter link"}"""));
        _fixture.StubAnthropicSummary("# Summary\n\nUpcoming events from the newsletter.");

        // Act
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert: fallback to MessageText
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var newsItems = await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync();
        newsItems.Should().HaveCount(1);
        newsItems[0].SourceType.Should().Be(SourceType.MessageText);
        newsItems[0].NewsletterUrl.Should().Be(badUrl);
        newsItems[0].NewsContent.Should().Be("Please read the newsletter for upcoming events");
    }

    /// <summary>
    /// Verifies that an empty feed completes without creating side effects.
    /// </summary>
    [Fact]
    public async Task FullPipeline_EmptyFeed_CompletesWithoutSideEffects()
    {
        // Arrange
        _fixture.StubTalkingPointsApi([]);

        // Act
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.Messages.Where(m => m.ParentId == _fixture.SeededParentId).ToListAsync()).Should().BeEmpty();
        (await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync()).Should().BeEmpty();
        (await db.Summaries.Where(s => s.ParentId == _fixture.SeededParentId).ToListAsync()).Should().BeEmpty();

        var mailCount = await _fixture.GetMailpitMessageCountAsync();
        mailCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies that non-news messages do not produce summaries or email.
    /// </summary>
    [Fact]
    public async Task FullPipeline_NoNewsMessages_NoEmailSent()
    {
        // Arrange
        var messages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("no-news-1", "Mrs. Lee", "Bob", "Have a great weekend!"),
        };
        _fixture.StubTalkingPointsApi(messages);

        _fixture.StubAnthropicCategorization(
            """{"message_id":"no-news-1","has_newsletter_url":false,"is_news_itself":false,"summary":""}""");

        // Act
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbMessages = await db.Messages.Where(m => m.ParentId == _fixture.SeededParentId).ToListAsync();
        dbMessages.Should().HaveCount(1);
        dbMessages[0].ProcessedAt.Should().NotBeNull();

        var newsItems = await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync();
        newsItems.Should().BeEmpty();

        var summaries = await db.Summaries.Where(s => s.ParentId == _fixture.SeededParentId).ToListAsync();
        summaries.Should().BeEmpty();

        var mailCount = await _fixture.GetMailpitMessageCountAsync();
        mailCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies that invalid categorization JSON falls back to treating the message as news.
    /// </summary>
    [Fact]
    public async Task FullPipeline_AnthropicReturnsInvalidJson_FallsBackToIsNewsItself()
    {
        // Arrange
        var messages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("bad-json-1", "Coach", "Alice", "Soccer practice moved to 4pm"),
        };
        _fixture.StubTalkingPointsApi(messages);

        // Anthropic returns invalid JSON → should fallback to IsNewsItself=true
        _fixture.StubAnthropicCategorizationForMessage("bad-json-1", AnthropicStubResponse.Ok("this is not json"));
        _fixture.StubAnthropicSummary("# Summary\n\nSoccer practice moved to 4pm.");

        // Act
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert: pipeline does not throw
        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Fallback creates IsNewsItself=true → MessageText news item
        var newsItems = await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync();
        newsItems.Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies that one categorization failure does not prevent other messages from succeeding.
    /// </summary>
    [Fact]
    public async Task FullPipeline_MultipleMessages_OneCategorizationFails_OthersSucceed()
    {
        // Arrange: 3 messages, msg-2 categorization returns HTTP 500
        var messages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("multi-1", "Mrs. Clark", "Alice", "Art show next Thursday"),
            CreateApiMessage("multi-2", "Mr. Davis", "Bob", "Math homework reminder"),
            CreateApiMessage("multi-3", "Nurse", "Alice", "Flu shots available"),
        };
        _fixture.StubTalkingPointsApi(messages);

        // msg-1: success, msg-2: HTTP 500, msg-3: success, then summary
        _fixture.StubAnthropicCategorizationForMessage("multi-1",
            AnthropicStubResponse.Ok("""{"message_id":"multi-1","has_newsletter_url":false,"is_news_itself":true,"summary":"Art show announced"}"""));
        _fixture.StubAnthropicCategorizationForMessage("multi-2", AnthropicStubResponse.Error(500));
        _fixture.StubAnthropicCategorizationForMessage("multi-3",
            AnthropicStubResponse.Ok("""{"message_id":"multi-3","has_newsletter_url":false,"is_news_itself":true,"summary":"Flu shots available"}"""));
        _fixture.StubAnthropicSummary("# Summary\n\nArt show and flu shots.");

        // Act
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();
        var result = await pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);

        // Assert: pipeline completed without throwing
        result.Should().Be(PipelineRunStatus.Completed);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 2 news items (msg-1 and msg-3)
        var newsItems = await db.NewsItems.Where(n => n.ParentId == _fixture.SeededParentId).ToListAsync();
        newsItems.Should().HaveCount(2);

        // msg-2 should NOT be marked processed (it errored)
        var msg2 = await db.Messages.FirstAsync(m => m.ExternalMessageId == "multi-2" && m.ParentId == _fixture.SeededParentId);
        msg2.ProcessedAt.Should().BeNull();

        // msg-1 and msg-3 should be marked processed
        var msg1 = await db.Messages.FirstAsync(m => m.ExternalMessageId == "multi-1" && m.ParentId == _fixture.SeededParentId);
        msg1.ProcessedAt.Should().NotBeNull();
        var msg3 = await db.Messages.FirstAsync(m => m.ExternalMessageId == "multi-3" && m.ParentId == _fixture.SeededParentId);
        msg3.ProcessedAt.Should().NotBeNull();

        // Email was sent (there is news from msg-1 and msg-3)
        var mailCount = await _fixture.GetMailpitMessageCountAsync();
        mailCount.Should().Be(1);
    }

    // --- Helpers ---

    private static TalkingPointsMessage CreateApiMessage(string id, string fromName, string studentName, string text, string? signature = null)
    {
        return new TalkingPointsMessage
        {
            Id = id,
            Text = text,
            FromName = fromName,
            From = new TalkingPointsFrom { User = new TalkingPointsUser { Signature = signature ?? fromName } },
            ContactInfo = new TalkingPointsContactInfo { StudentName = studentName },
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            DisplayDate = DateTime.UtcNow.AddHours(-1),
        };
    }

    private sealed class FailingAppDbContext : AppDbContext
    {
        private bool _shouldThrow = true;

        public FailingAppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var result = await base.SaveChangesAsync(cancellationToken);

            if (_shouldThrow && ChangeTracker.Entries<Message>().Any(entry => entry.Entity.ProcessedAt is not null))
            {
                _shouldThrow = false;
                throw new InvalidOperationException("Simulated save failure after flush");
            }

            return result;
        }
    }
}
