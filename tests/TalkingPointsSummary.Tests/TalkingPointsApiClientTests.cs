using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class TalkingPointsApiClientTests
{
    private readonly Parent _testParent = new()
    {
        Id = 1,
        Name = "Test Parent",
        TalkingPointsToken = "test-token-123",
        TalkingPointsContactId = "contact-456",
        EmailRecipients = "test@example.com"
    };

    private sealed record LogEntry(LogLevel LogLevel, string Message);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private static TalkingPointsApiClient CreateClient(HttpMessageHandler handler, TalkingPointsApiOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        return new TalkingPointsApiClient(
            httpClient,
            Options.Create(options ?? new TalkingPointsApiOptions()),
            NullLogger<TalkingPointsApiClient>.Instance);
    }

    private static TalkingPointsApiClient CreateClient(HttpMessageHandler handler, ILogger<TalkingPointsApiClient> logger, TalkingPointsApiOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        return new TalkingPointsApiClient(
            httpClient,
            Options.Create(options ?? new TalkingPointsApiOptions()),
            logger);
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, object? responseBody = null)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        var responseContent = responseBody != null
            ? new StringContent(JsonSerializer.Serialize(responseBody), System.Text.Encoding.UTF8, "application/json")
            : new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = responseContent
            });

        return mockHandler;
    }

    [Fact]
    public async Task FetchMessagesAsync_SendsCorrectUrlAndHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("""{"data":{"messages":[]}}""", System.Text.Encoding.UTF8, "application/json")
            });

        var client = CreateClient(mockHandler.Object);
        await client.FetchMessagesAsync(_testParent);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString().Should().Contain("/api/parents/v3/messages/feed");
        capturedRequest.Headers.GetValues("x-token").Should().ContainSingle().Which.Should().Be("test-token-123");
        capturedRequest.Headers.GetValues("x-contactid").Should().ContainSingle().Which.Should().Be("contact-456");
        capturedRequest.Headers.GetValues("x-app-version").Should().ContainSingle().Which.Should().Be("5.0.0");
        capturedRequest.Headers.GetValues("x-language").Should().ContainSingle().Which.Should().Be("en");
        capturedRequest.Headers.GetValues("x-mobile-platform").Should().ContainSingle().Which.Should().Be("web");
    }

    [Fact]
    public async Task FetchMessagesAsync_SuccessfulResponse_DeserializesCorrectly()
    {
        var responseBody = new
        {
            data = new
            {
                messages = new[]
                {
                    new
                    {
                        _id = "msg-001",
                        contactMessageId = "contact-msg-001",
                        text = "Hello parents!",
                        fromName = "Ms. Smith",
                        from = new { user = new { signature = "Ms. Jane Smith" } },
                        contactInfo = new { studentName = "StudentOne" },
                        createdAt = "2026-03-01T10:00:00Z",
                        displayDate = "2026-03-01T10:30:00Z"
                    }
                }
            }
        };

        var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseBody);
        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(_testParent);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("msg-001");
        result[0].Text.Should().Be("Hello parents!");
        result[0].ContactInfo!.StudentName.Should().Be("StudentOne");
        result[0].From!.User!.Signature.Should().Be("Ms. Jane Smith");
    }

    [Fact]
    public async Task FetchMessagesAsync_EmptyMessages_ReturnsEmptyList()
    {
        var responseBody = new { data = new { messages = Array.Empty<object>() } };
        var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseBody);
        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(_testParent);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchMessagesAsync_NonSuccessStatus_Throws()
    {
        var mockHandler = CreateMockHandler(HttpStatusCode.Unauthorized);
        var client = CreateClient(mockHandler.Object);

        var act = () => client.FetchMessagesAsync(_testParent);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task FetchMessagesAsync_WhenFirstPageFull_FetchesAdditionalPages()
    {
        var pageOneMessages = Enumerable.Range(1, 20)
            .Select(index => CreateApiMessage($"page1-{index}"))
            .ToList();
        var pageTwoMessages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("page2-1"),
            CreateApiMessage("page2-2")
        };

        var requestedPages = new List<int>();
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                var page = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"];
                requestedPages.Add(int.Parse(page!));
            })
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
                var body = page switch
                {
                    1 => CreateApiResponse(pageOneMessages),
                    2 => CreateApiResponse(pageTwoMessages),
                    _ => CreateApiResponse([])
                };

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(_testParent);

        result.Should().HaveCount(22);
        requestedPages.Should().Equal(1, 2);
    }

    [Fact]
    public async Task FetchMessagesAsync_StopsWhenStopMessageIsEncountered()
    {
        var pageOneMessages = Enumerable.Range(1, 20)
            .Select(index => CreateApiMessage($"page1-{index}"))
            .ToList();
        var pageTwoMessages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("page2-1"),
            CreateApiMessage("page2-2"),
            CreateApiMessage("already-saved"),
            CreateApiMessage("older-than-saved")
        };

        var requestedPages = new List<int>();
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestedPages.Add(int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!));
            })
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
                var body = page switch
                {
                    1 => CreateApiResponse(pageOneMessages),
                    2 => CreateApiResponse(pageTwoMessages),
                    _ => CreateApiResponse([])
                };

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(_testParent, "already-saved");

        result.Select(message => message.Id).Should().ContainInOrder(
            pageOneMessages.Select(message => message.Id)
                .Concat(["page2-1", "page2-2"]));
        result.Should().NotContain(message => message.Id == "already-saved" || message.Id == "older-than-saved");
        requestedPages.Should().Equal(1, 2);
    }

    [Fact]
    public async Task FetchMessagesAsync_StopsWhenMessagesBecomeOlderThanNewestSavedTimestamp()
    {
        var pageOneMessages = Enumerable.Range(1, 20)
            .Select(index => CreateApiMessage($"page1-{index}", new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-index)))
            .ToList();
        var pageTwoMessages = new List<TalkingPointsMessage>
        {
            CreateApiMessage("page2-newer", new DateTime(2026, 3, 10, 10, 1, 0, DateTimeKind.Utc)),
            CreateApiMessage("page2-older", new DateTime(2026, 3, 10, 9, 59, 0, DateTimeKind.Utc)),
            CreateApiMessage("page2-oldest", new DateTime(2026, 3, 10, 9, 58, 0, DateTimeKind.Utc)),
        };

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
                var body = page switch
                {
                    1 => CreateApiResponse(pageOneMessages),
                    2 => CreateApiResponse(pageTwoMessages),
                    _ => CreateApiResponse([])
                };

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(
            _testParent,
            stopBeforeSentAtUtc: new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc));

        result.Select(message => message.Id).Should().Contain("page2-newer");
        result.Should().NotContain(message => message.Id == "page2-older" || message.Id == "page2-oldest");
    }

    [Fact]
    public async Task FetchMessagesAsync_RespectsMaxPagesPerRun()
    {
        var requestedPages = new List<int>();
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestedPages.Add(int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!));
            })
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
                var body = CreateApiResponse(Enumerable.Range(1, 20).Select(index => CreateApiMessage($"page{page}-{index}")).ToList());

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var client = CreateClient(mockHandler.Object, new TalkingPointsApiOptions { MaxPagesPerRun = 3 });

        var result = await client.FetchMessagesAsync(_testParent);

        requestedPages.Should().Equal(1, 2, 3);
        result.Should().HaveCount(60);
    }

    [Fact]
    public async Task FetchMessagesAsync_LogsActualPagesFetched_WhenMaxPagesStopsPagination()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
                var body = CreateApiResponse(Enumerable.Range(1, 20).Select(index => CreateApiMessage($"page{page}-{index}")).ToList());

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var logger = new ListLogger<TalkingPointsApiClient>();
        var client = CreateClient(mockHandler.Object, logger, new TalkingPointsApiOptions { MaxPagesPerRun = 3 });

        await client.FetchMessagesAsync(_testParent);

        logger.Entries.Should().Contain(entry =>
            entry.LogLevel == LogLevel.Information
            && entry.Message.Contains("across 3 page(s)", StringComparison.Ordinal)
            && entry.Message.Contains("MaxPagesPerRun=3", StringComparison.Ordinal));
    }

    private static string CreateApiResponse(List<TalkingPointsMessage> messages)
        => JsonSerializer.Serialize(new TalkingPointsApiResponse
        {
            Data = new TalkingPointsData { Messages = messages }
        });

    private static TalkingPointsMessage CreateApiMessage(string id, DateTime? sentAt = null)
        => new()
        {
            Id = id,
            Text = $"Message {id}",
            CreatedAt = sentAt ?? DateTime.UtcNow,
            DisplayDate = sentAt ?? DateTime.UtcNow
        };
}
