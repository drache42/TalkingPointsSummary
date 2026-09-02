using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Configurations;
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
    private const string TestcontainersHostAlias = "host.testcontainers.internal";

    /// <summary>
    /// Model id the categorization profile, and therefore event extraction, runs under.
    /// </summary>
    public const string CategorizationModelId = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Model id the summary and revision calls run under.
    /// </summary>
    public const string SummarizationModelId = "claude-sonnet-4-5-20250929";

    /// <summary>
    /// Model id the critique call runs under, distinct so the stub matcher can tell it apart.
    /// </summary>
    public const string CritiqueModelId = "claude-opus-4-1-20250805";

    /// <summary>
    /// Phrase unique to the event extraction prompt, used to route that call to its own stub.
    /// </summary>
    private const string EventExtractionPromptMarker = "The news item below was sent on";

    private PostgreSqlContainer _postgres = null!;
    private IContainer _mailpit = null!;
    private IContainer _browserless = null!;
    private WireMockServer _wireMock = null!;
    private WebApplication _contentServer = null!;
    private int _contentServerPort;

    /// <summary>
    /// Connection string for the PostgreSQL test container.
    /// </summary>
    public string PostgresConnectionString { get; private set; } = null!;

    /// <summary>
    /// Host name for Mailpit SMTP delivery.
    /// </summary>
    public string MailpitSmtpHost { get; private set; } = null!;

    /// <summary>
    /// SMTP port exposed by Mailpit.
    /// </summary>
    public int MailpitSmtpPort { get; private set; }

    /// <summary>
    /// Base URL for the Mailpit HTTP API.
    /// </summary>
    public string MailpitApiUrl { get; private set; } = null!;

    /// <summary>
    /// Base URL for the Browserless test container.
    /// </summary>
    public string BrowserlessUrl { get; private set; } = null!;

    /// <summary>
    /// Base URL for the WireMock test server.
    /// </summary>
    public string WireMockUrl => _wireMock.Url!;

    /// <summary>
    /// Base URL for the local HTML content server.
    /// </summary>
    public string ContentServerUrl { get; private set; } = null!;

    /// <summary>
    /// WireMock server used to stub external HTTP dependencies.
    /// </summary>
    public WireMockServer WireMock => _wireMock;

    /// <summary>
    /// Seeded parent identifier available to tests.
    /// </summary>
    public int SeededParentId { get; private set; }

    /// <summary>
    /// First seeded child identifier available to tests.
    /// </summary>
    public int SeededChild1Id { get; private set; }

    /// <summary>
    /// Second seeded child identifier available to tests.
    /// </summary>
    public int SeededChild2Id { get; private set; }

    // Content server pages — add entries before starting a test
    private readonly Dictionary<string, ContentPage> _contentPages = new();

    /// <summary>
    /// Static content served by the integration-test content server.
    /// </summary>
    public record ContentPage
    {
        /// <summary>
        /// Initializes a new content-page definition.
        /// </summary>
        /// <param name="html">HTML body served for the page.</param>
        /// <param name="statusCode">HTTP status code returned for the page.</param>
        public ContentPage(string html, int statusCode = 200)
        {
            Html = html;
            StatusCode = statusCode;
        }

        /// <summary>
        /// HTML body served for the page.
        /// </summary>
        public string Html { get; init; }

        /// <summary>
        /// HTTP status code returned for the page.
        /// </summary>
        public int StatusCode { get; init; }
    }

    /// <summary>
    /// Starts external test dependencies, applies migrations, and seeds base data.
    /// </summary>
    public async Task InitializeAsync()
    {
        await StartContentServerAsync();
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)_contentServerPort);

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

        // Run EF migrations
        await RunMigrationsAsync();

        // Seed base data
        await SeedDataAsync();
    }

    /// <summary>
    /// Stops and disposes external test dependencies.
    /// </summary>
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
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"PipelineRuns\" CASCADE");
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"TrackedEvents\" CASCADE");
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
    public ServiceProvider CreateServiceProvider(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton(Options.Create(new AiOptions
        {
            Provider = "Anthropic",
            Anthropic = new AnthropicProviderOptions { ApiKey = "test-anthropic-key" },
            Profiles = new AiProfilesOptions
            {
                Categorization = new AiProfileOptions { ModelId = CategorizationModelId, MaxTokens = 1024 },
                Summarization = new AiProfileOptions { ModelId = SummarizationModelId, MaxTokens = 8192 },
                // Given its own model id so critique requests are told apart from summary requests
                // by the stub matcher. Left on the shipped default they would fall through to an
                // unstubbed 404, which the critic swallows, and the review pass would never run.
                Critique = new AiProfileOptions { ModelId = CritiqueModelId, MaxTokens = 8192 },
                Validation = new AiProfileOptions { ModelId = "claude-haiku-3-5-20241022", MaxTokens = 1 }
            }
        }));
        services.AddSingleton(Options.Create(new BrowserlessOptions
        {
            BaseUrl = BrowserlessUrl,
        }));
        services.AddSingleton(Options.Create(new NewsletterScrapingSecurityOptions
        {
            Enabled = true,
            RequireHttps = true,
            AllowedHosts = ["host.docker.internal", TestcontainersHostAlias],
            AllowHttpHosts = ["host.docker.internal", TestcontainersHostAlias],
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

        services.AddHttpClient<IAiClient, AnthropicAiClient>()
            .AddHttpMessageHandler(() => new UrlRewritingHandler(wireMockUri));

        services.AddHttpClient<INewsletterScraper, NewsletterScraper>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        services.AddSingleton<IHostAddressResolver, HostAddressResolver>();
        services.AddScoped<INewsletterUrlValidator, NewsletterUrlValidator>();
        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddSingleton<IMarkdownConverter, MarkdownConverter>();
        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<IMessageCategorizer, MessageCategorizer>();
        // The orchestrator takes these three as well. Registered here so the composition root the
        // end-to-end tests run through matches the worker's, rather than failing to resolve the
        // orchestrator at all.
        services.AddSingleton<SummaryOutputValidator>();
        services.AddScoped<IEventExtractor, EventExtractor>();
        services.AddScoped<ISummaryCritic, SummaryCritic>();
        services.AddScoped<ISummaryGenerator, SummaryGenerator>();
        services.AddParentChildServices();
        services.AddScoped<PipelineOrchestrator>();
        services.AddSingleton<WeeklyPipelineService>();

        configureServices?.Invoke(services);

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

        StubAnthropicEventExtraction();
    }

    /// <summary>
    /// Stubs the Anthropic event extraction call, which runs on the categorization profile.
    /// </summary>
    /// <remarks>
    /// It needs its own mapping because it shares a model id with categorization but sends a
    /// completely different prompt and expects a completely different JSON shape. Without one, the
    /// extraction call is served a categorization payload, or 404s and is swallowed.
    /// </remarks>
    /// <param name="jsonPayload">Extraction JSON to return; defaults to no events.</param>
    public void StubAnthropicEventExtraction(string? jsonPayload = null)
    {
        var payload = jsonPayload
            ?? """{ "events": [], "cancelled_event_ids": [], "reinstated_event_ids": [] }""";

        var anthropicResponse = new
        {
            content = new[]
            {
                new { type = "text", text = payload }
            }
        };

        _wireMock
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost()
                .WithBody(new WildcardMatcher($"*{CategorizationModelId}*"))
                .WithBody(new WildcardMatcher($"*{EventExtractionPromptMarker}*")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(anthropicResponse)));
    }

    /// <summary>
    /// Stubs the Anthropic critique call that reviews a generated digest.
    /// </summary>
    /// <param name="jsonPayload">Critique JSON to return; defaults to no findings.</param>
    public void StubAnthropicCritique(string? jsonPayload = null)
    {
        var anthropicResponse = new
        {
            content = new[]
            {
                new { type = "text", text = jsonPayload ?? """{ "findings": [] }""" }
            }
        };

        _wireMock
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost()
                .WithBody(new WildcardMatcher($"*{CritiqueModelId}*")))
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
        StubAnthropicEventExtraction();

        var request = Request.Create()
            .WithPath("/v1/messages")
            .UsingPost()
            .WithBody(new WildcardMatcher($"*{CategorizationModelId}*"))
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
                .WithBody(new WildcardMatcher($"*{SummarizationModelId}*")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(anthropicResponse)));

        // Every generated digest is reviewed before it is sent, so a test that stubs a summary has
        // to stub the review too or the critic call 404s and is silently swallowed.
        StubAnthropicCritique();
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

        _contentServerPort = uri.Port;

        // Containers reach the test host through Testcontainers' forwarded host alias.
        ContentServerUrl = $"http://{TestcontainersHostAlias}:{uri.Port}";
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

/// <summary>
/// Configures a stubbed Anthropic categorization response for integration tests.
/// </summary>
public record AnthropicStubResponse
{
    /// <summary>
    /// Initializes a new Anthropic stub response definition.
    /// </summary>
    /// <param name="jsonPayload">JSON payload returned by the stub when successful.</param>
    /// <param name="isError">Whether the stub should return an error response.</param>
    /// <param name="statusCode">HTTP status code returned by the stub.</param>
    public AnthropicStubResponse(string? jsonPayload, bool isError = false, int statusCode = 200)
    {
        JsonPayload = jsonPayload;
        IsError = isError;
        StatusCode = statusCode;
    }

    /// <summary>
    /// JSON payload returned by the stub when successful.
    /// </summary>
    public string? JsonPayload { get; init; }

    /// <summary>
    /// Whether the stub should return an error response.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// HTTP status code returned by the stub.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Creates a successful stub response.
    /// </summary>
    /// <param name="json">JSON payload returned by the stub.</param>
    public static AnthropicStubResponse Ok(string json) => new(json);

    /// <summary>
    /// Creates an error stub response.
    /// </summary>
    /// <param name="statusCode">HTTP status code returned by the stub.</param>
    public static AnthropicStubResponse Error(int statusCode = 500) => new(null, true, statusCode);
}

/// <summary>
/// Simplified Mailpit message payload used by integration assertions.
/// </summary>
public class MailpitMessage
{
    /// <summary>
    /// Mailpit message identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Message subject line.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Recipient addresses on the message.
    /// </summary>
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
