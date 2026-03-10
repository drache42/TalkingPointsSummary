using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.IntegrationTests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class PipelineEndToEndTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public PipelineEndToEndTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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
        mailMessages[0].Subject.Should().Be("Talking Points Summary V2");
        mailMessages[0].To.Should().Contain("test@example.com");

        var htmlBody = await _fixture.GetMailpitMessageHtmlAsync(mailMessages[0].Id);
        htmlBody.Should().Contain("School play next week!");
        htmlBody.Should().Contain("Field trip to the museum on Friday.");
    }

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

        var summaries = await db.Summaries.Where(s => s.ParentId == _fixture.SeededParentId).OrderBy(s => s.CreatedAt).ToListAsync();
        summaries.Should().HaveCount(2);
        summaries.Should().AllSatisfy(s => s.Content.Should().Be(summaryMarkdown));

        // Mailpit: both runs send a summary because the second run still sees recent news items.
        var mailCount = await _fixture.GetMailpitMessageCountAsync();
        mailCount.Should().Be(2);
    }

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
}
