using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class StartupValidatorTests : IDisposable
{
    private readonly AppDbContext _db;

    public StartupValidatorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(options);
    }

    [Fact]
    public async Task RunAllChecksAsync_TalkingPointsParentCheck_UsesOnePageProbe()
    {
        _db.Parents.Add(new Parent
        {
            Name = "Test Parent",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "test@example.com",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        var talkingPointsClient = new Mock<ITalkingPointsApiClient>();
        talkingPointsClient
            .Setup(client => client.FetchMessagesAsync(
                It.IsAny<Parent>(),
                null,
                null,
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        var validator = new StartupValidator(
            Options.Create(new AnthropicOptions { ApiKey = "test-key" }),
            Options.Create(new BrowserlessOptions { BaseUrl = "http://localhost:3000" }),
            Options.Create(new SmtpOptions { Host = "localhost", Port = 1025, FromEmail = "dev@example.com" }),
            _db,
            talkingPointsClient.Object,
            httpClientFactory.Object,
            NullLogger<StartupValidator>.Instance);

        await validator.RunAllChecksAsync();

        talkingPointsClient.Verify(client => client.FetchMessagesAsync(
            It.IsAny<Parent>(),
            null,
            null,
            1,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}