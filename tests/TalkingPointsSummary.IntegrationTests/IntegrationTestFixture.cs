using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TalkingPointsSummary.IntegrationTests;

/// <summary>
/// Shared test fixture that manages Docker containers (Postgres, Mailpit, Browserless),
/// a WireMock server for stubbing external APIs, and a simple content server for static HTML.
///
/// Requirements:
/// - Docker must be running on the host machine
/// - No environment variables or secrets are needed — all external dependencies are replaced by containers/stubs
/// - Run with: dotnet test tests/TalkingPointsSummary.IntegrationTests/
/// </summary>
public class IntegrationTestFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private IContainer _mailpit = null!;
    private IContainer _browserless = null!;
    private WireMockServer _wireMock = null!;
    private WebApplication _contentServer = null!;

    // Exposed endpoints
    public string PostgresConnectionString { get; private set; } = null!;
    public string MailpitSmtpHost { get; private set; } = null!;
    public int MailpitSmtpPort { get; private set; }
    public string MailpitApiUrl { get; private set; } = null!;
    public string BrowserlessUrl { get; private set; } = null!;
    public string WireMockUrl => _wireMock.Url!;
    public string ContentServerUrl { get; private set; } = null!;
    public WireMockServer WireMock => _wireMock;

    // Seeded data IDs
    public int SeededParentId { get; private set; }
    public int SeededChild1Id { get; private set; }
    public int SeededChild2Id { get; private set; }

    // Content server pages — add entries before starting a test
    private readonly Dictionary<string, ContentPage> _contentPages = new();

    public record ContentPage(string Html, int StatusCode = 200);

    public async Task InitializeAsync()
    {
        // Start containers in parallel
        _postgres = new PostgreSqlBuilder("postgres:15-alpine")
            .WithDatabase("talkingpoints")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        _mailpit = new ContainerBuilder("axllent/mailpit")
            .WithPortBinding(0, 1025)  // SMTP
            .WithPortBinding(0, 8025)  // HTTP API
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025)))
            .Build();

        _browserless = new ContainerBuilder("browserless/chrome")
            .WithPortBinding(0, 3000)
            .WithEnvironment("MAX_CONCURRENT_SESSIONS", "2")
            .WithEnvironment("MAX_QUEUE_LENGTH", "5")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000)))
            .Build();

        await Task.WhenAll(
            _postgres.StartAsync(),
            _mailpit.StartAsync(),
            _browserless.StartAsync());

        PostgresConnectionString = _postgres.GetConnectionString();
        MailpitSmtpHost = _mailpit.Hostname;
        MailpitSmtpPort = _mailpit.GetMappedPublicPort(1025);
        MailpitApiUrl = $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}";
        BrowserlessUrl = $"http://{_browserless.Hostname}:{_browserless.GetMappedPublicPort(3000)}";

        // Start WireMock
        _wireMock = WireMockServer.Start();

        // Start content server
        await StartContentServerAsync();

        // Run EF migrations
        await RunMigrationsAsync();

        // Seed base data
        await SeedDataAsync();
    }

    public async Task DisposeAsync()
    {
        _wireMock?.Stop();
        _wireMock?.Dispose();

        if (_contentServer != null)
        {
            await _contentServer.StopAsync();
            await _contentServer.DisposeAsync();
        }

        await Task.WhenAll(
            _postgres?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _mailpit?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _browserless?.DisposeAsync().AsTask() ?? Task.CompletedTask);
    }

    /// <summary>
    /// Registers a static HTML page at the given path on the content server.
    /// Browserless will scrape this URL instead of a real internet URL.
    /// </summary>
    public void RegisterContentPage(string path, string html, int statusCode = 200)
    {
        var key = path.TrimStart('/');
        _contentPages[key] = new ContentPage(html, statusCode);
    }

    /// <summary>
    /// Clears registered content pages.
    /// </summary>
    public void ClearContentPages()
    {
        _contentPages.Clear();
    }

    /// <summary>
    /// Resets state between tests: truncates application tables (not Parents/Children),
    /// resets WireMock stubs/logs, and clears content pages.
    /// </summary>
    public async Task ResetAsync()
    {
        // Truncate application tables (keep Parents and Children)
        await using var serviceProvider = CreateServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Summaries\" CASCADE");
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"NewsItems\" CASCADE");
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Messages\" CASCADE");

        // Reset WireMock
        _wireMock.Reset();

        // Clear content pages
        ClearContentPages();

        // Delete all Mailpit messages
        using var httpClient = new HttpClient();
        await httpClient.DeleteAsync($"{MailpitApiUrl}/api/v1/messages");
    }

    /// <summary>
    /// Creates a configured IServiceProvider wired to the test containers.
    /// </summary>
    public ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Options.Create(new AnthropicOptions
        {
            ApiKey = "test-anthropic-key",
        }));
        services.AddSingleton(Options.Create(new BrowserlessOptions
        {
            BaseUrl = BrowserlessUrl,
        }));
        services.AddSingleton(Options.Create(new SmtpOptions
        {
            Host = MailpitSmtpHost,
            Port = MailpitSmtpPort,
            Username = string.Empty,
            Password = string.Empty,
            FromEmail = "test-sender@example.com",
        }));
        services.AddSingleton(Options.Create(new PipelineScheduleOptions
        {
            DayOfWeek = 1,
            Hour = 8,
        }));
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(PostgresConnectionString,
                npgsql => npgsql.MigrationsAssembly("TalkingPointsSummary")));

        // DelegatingHandler that rewrites absolute external URLs to point at WireMock
        var wireMockUri = new Uri(WireMockUrl);

        // Typed HTTP clients with URL rewriting to redirect external API calls to WireMock
        services.AddHttpClient<ITalkingPointsApiClient, TalkingPointsApiClient>()
            .AddHttpMessageHandler(() => new UrlRewritingHandler(wireMockUri));

        services.AddHttpClient<IMessageCategorizer, MessageCategorizer>()
            .AddHttpMessageHandler(() => new UrlRewritingHandler(wireMockUri));

        services.AddHttpClient<INewsletterScraper, NewsletterScraper>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        services.AddHttpClient<ISummaryGenerator, SummaryGenerator>()
            .AddHttpMessageHandler(() => new UrlRewritingHandler(wireMockUri));

        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddSingleton<IMarkdownConverter, MarkdownConverter>();
        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<PipelineOrchestrator>();
        services.AddSingleton<WeeklyPipelineService>();

        return services.BuildServiceProvider();
    }

    // --- WireMock Stub Helpers ---

    /// <summary>
    /// Stubs the TalkingPoints API to return the given messages.
    /// </summary>
    public void StubTalkingPointsApi(List<TalkingPointsMessage> messages)
    {
        var response = new TalkingPointsApiResponse
        {
            Data = new TalkingPointsData { Messages = messages }
        };

        _wireMock
            .Given(Request.Create()
                .WithPath("/api/parents/v3/messages/feed")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));
    }

    /// <summary>
    /// Stubs the Anthropic API for message categorization (Claude response).
    /// The jsonPayload should be the raw JSON object like:
    /// { "message_id": "...", "has_newsletter_url": false, "is_news_itself": true, "summary": "..." }
    /// </summary>
    public void StubAnthropicCategorization(string jsonPayload)
    {
        var anthropicResponse = new
        {
            content = new[]
            {
                new { type = "text", text = jsonPayload }
            }
        };

        _wireMock
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(anthropicResponse)));
    }

    /// <summary>
    /// Stubs the Anthropic API categorization call for a specific message.
    /// </summary>
    public void StubAnthropicCategorizationForMessage(string messageId, AnthropicStubResponse response)
    {
        var request = Request.Create()
            .WithPath("/v1/messages")
            .UsingPost()
            .WithBody(new WildcardMatcher("*claude-haiku-4-5-20251001*"))
            .WithBody(new WildcardMatcher($"*MessageID: {messageId}*"));

        if (response.IsError)
        {
            _wireMock
                .Given(request)
                .RespondWith(Response.Create()
                    .WithStatusCode(response.StatusCode));

            return;
        }

        var anthropicResponse = new
        {
            content = new[]
            {
                new { type = "text", text = response.JsonPayload }
            }
        };

        _wireMock
            .Given(request)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(anthropicResponse)));
    }

    /// <summary>
    /// Stubs the Anthropic API summary-generation call.
    /// </summary>
    public void StubAnthropicSummary(string markdown)
    {
        var anthropicResponse = new
        {
            content = new[]
            {
                new { type = "text", text = markdown }
            }
        };

        _wireMock
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost()
                .WithBody(new WildcardMatcher("*claude-sonnet-4-5-20250929*")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(anthropicResponse)));
    }

    /// <summary>
    /// Stubs the Anthropic API with an artificial delay (ms).
    /// </summary>
    public void StubAnthropicCategorizationWithDelay(string jsonPayload, int delayMs)
    {
        var anthropicResponse = new
        {
            content = new[]
            {
                new { type = "text", text = jsonPayload }
            }
        };

        _wireMock
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(anthropicResponse))
                .WithDelay(delayMs));
    }

    /// <summary>
    /// Gets the number of emails delivered to Mailpit.
    /// </summary>
    public async Task<int> GetMailpitMessageCountAsync()
    {
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"{MailpitApiUrl}/api/v1/messages");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("messages_count").GetInt32();
    }

    /// <summary>
    /// Gets delivered Mailpit messages with details.
    /// </summary>
    public async Task<List<MailpitMessage>> GetMailpitMessagesAsync()
    {
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"{MailpitApiUrl}/api/v1/messages");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var messages = new List<MailpitMessage>();
        foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            var to = new List<string>();
            foreach (var addr in msg.GetProperty("To").EnumerateArray())
            {
                to.Add(addr.GetProperty("Address").GetString()!);
            }

            messages.Add(new MailpitMessage
            {
                Id = msg.GetProperty("ID").GetString()!,
                Subject = msg.GetProperty("Subject").GetString()!,
                To = to,
            });
        }

        return messages;
    }

    /// <summary>
    /// Gets the HTML body of a specific Mailpit message.
    /// </summary>
    public async Task<string> GetMailpitMessageHtmlAsync(string messageId)
    {
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"{MailpitApiUrl}/api/v1/message/{messageId}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("HTML").GetString() ?? string.Empty;
    }

    // --- Private helpers ---

    private async Task StartContentServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // Listen on all interfaces so Docker containers can reach us via host.docker.internal
        builder.WebHost.UseKestrel(o => o.Listen(System.Net.IPAddress.Any, 0));
        builder.Logging.ClearProviders();

        _contentServer = builder.Build();

        _contentServer.MapGet("/{**path}", (string path) =>
        {
            if (_contentPages.TryGetValue(path, out var page))
            {
                if (page.StatusCode != 200)
                {
                    return Results.StatusCode(page.StatusCode);
                }
                return Results.Content(page.Html, "text/html");
            }
            return Results.NotFound();
        });

        await _contentServer.StartAsync();

        // Get the actual bound port from the server features
        var serverAddresses = _contentServer.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var boundAddress = serverAddresses!.Addresses.First();
        var uri = new Uri(boundAddress);

        // Browserless runs in Docker and needs host.docker.internal to reach the host
        ContentServerUrl = $"http://host.docker.internal:{uri.Port}";
    }

    private async Task RunMigrationsAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(PostgresConnectionString,
                npgsql => npgsql.MigrationsAssembly("TalkingPointsSummary")));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    private async Task SeedDataAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(PostgresConnectionString,
                npgsql => npgsql.MigrationsAssembly("TalkingPointsSummary")));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var parent = new Parent
        {
            Name = "Test Parent",
            TalkingPointsToken = "test-token",
            TalkingPointsContactId = "test-contact",
            EmailRecipients = "test@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Parents.Add(parent);
        await db.SaveChangesAsync();

        SeededParentId = parent.Id;

        var child1 = new Child
        {
            ParentId = parent.Id,
            Name = "Alice",
            School = "Lincoln Elementary",
            StartingGrade = 0,
            StartingYear = 2024,
        };
        var child2 = new Child
        {
            ParentId = parent.Id,
            Name = "Bob",
            School = "Lincoln Elementary",
            StartingGrade = 2,
            StartingYear = 2022,
        };
        db.Children.AddRange(child1, child2);
        await db.SaveChangesAsync();

        SeededChild1Id = child1.Id;
        SeededChild2Id = child2.Id;
    }
}

public record AnthropicStubResponse(string? JsonPayload, bool IsError = false, int StatusCode = 200)
{
    public static AnthropicStubResponse Ok(string json) => new(json);
    public static AnthropicStubResponse Error(int statusCode = 500) => new(null, true, statusCode);
}

public class MailpitMessage
{
    public string Id { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public List<string> To { get; set; } = [];
}

/// <summary>
/// DelegatingHandler that rewrites all outgoing request URLs to point at WireMock,
/// preserving the original path and query string.
/// This is needed because the services construct full absolute URLs (e.g., https://api.anthropic.com/v1/messages)
/// and setting HttpClient.BaseAddress has no effect on absolute URIs.
/// </summary>
internal class UrlRewritingHandler : DelegatingHandler
{
    private readonly Uri _targetBase;

    public UrlRewritingHandler(Uri targetBase)
    {
        _targetBase = targetBase;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != null)
        {
            var rewritten = new UriBuilder(_targetBase)
            {
                Path = request.RequestUri.AbsolutePath,
                Query = request.RequestUri.Query,
            };
            request.RequestUri = rewritten.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }
}
