using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public enum CheckStatus { Pass, Warn, Fail }

public record ValidationCheckResult(string Name, CheckStatus Status, string Detail);

/// <summary>
/// Validates all required secrets and external service connections before the worker starts.
/// </summary>
public class StartupValidator
{
    private readonly AnthropicOptions _anthropic;
    private readonly BrowserlessOptions _browserless;
    private readonly SmtpOptions _smtp;
    private readonly AppDbContext _db;
    private readonly ITalkingPointsApiClient _talkingPointsClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StartupValidator> _logger;

    public StartupValidator(
        IOptions<AnthropicOptions> anthropic,
        IOptions<BrowserlessOptions> browserless,
        IOptions<SmtpOptions> smtp,
        AppDbContext db,
        ITalkingPointsApiClient talkingPointsClient,
        IHttpClientFactory httpClientFactory,
        ILogger<StartupValidator> logger)
    {
        _anthropic = anthropic.Value;
        _browserless = browserless.Value;
        _smtp = smtp.Value;
        _db = db;
        _talkingPointsClient = talkingPointsClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<ValidationCheckResult>> RunAllChecksAsync(CancellationToken ct = default)
    {
        var results = new List<ValidationCheckResult>
        {
            CheckConfigPresence()
        };

        results.Add(await CheckDatabaseAsync(ct));
        results.Add(await CheckAnthropicApiKeyAsync(ct));
        results.Add(await CheckBrowserlessAsync(ct));
        results.Add(await CheckSmtpAsync(ct));
        results.AddRange(await CheckTalkingPointsParentsAsync(ct));

        return results;
    }

    private ValidationCheckResult CheckConfigPresence()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(_anthropic.ApiKey))
            missing.Add("Anthropic:ApiKey");
        if (string.IsNullOrWhiteSpace(_smtp.Host))
            missing.Add("Smtp:Host");
        if (string.IsNullOrWhiteSpace(_smtp.FromEmail))
            missing.Add("Smtp:FromEmail");

        return missing.Count == 0
            ? new ValidationCheckResult("Config presence", CheckStatus.Pass, "All required environment variables are set")
            : new ValidationCheckResult("Config presence", CheckStatus.Fail, $"Missing: {string.Join(", ", missing)}");
    }

    private async Task<ValidationCheckResult> CheckDatabaseAsync(CancellationToken ct)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(ct);
            if (!canConnect)
                return new ValidationCheckResult("Database connection", CheckStatus.Fail, "Cannot connect to the database");

            var pending = (await _db.Database.GetPendingMigrationsAsync(ct)).ToList();
            var applied = (await _db.Database.GetAppliedMigrationsAsync(ct)).ToList();

            if (applied.Count == 0 && pending.Count == 0)
                return new ValidationCheckResult("Database connection", CheckStatus.Fail,
                    "No migrations are defined — run: dotnet ef migrations add InitialCreate --project src/TalkingPointsSummary");

            return pending.Count == 0
                ? new ValidationCheckResult("Database connection", CheckStatus.Pass,
                    $"Connected; {applied.Count} migration(s) applied, schema is up to date")
                : new ValidationCheckResult("Database connection", CheckStatus.Fail,
                    $"Connected but {pending.Count} pending migration(s): {string.Join(", ", pending)} — run MigrateAsync or restart the app");
        }
        catch (Exception ex)
        {
            return new ValidationCheckResult("Database connection", CheckStatus.Fail, ex.Message);
        }
    }

    private async Task<ValidationCheckResult> CheckAnthropicApiKeyAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_anthropic.ApiKey))
            return new ValidationCheckResult("Anthropic API key", CheckStatus.Fail, "Anthropic:ApiKey is not set");

        try
        {
            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _anthropic.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            // Minimal body — we only care whether auth succeeds, not whether the request is valid
            request.Content = JsonContent.Create(new
            {
                model = "claude-haiku-3-5-20241022",
                max_tokens = 1,
                messages = Array.Empty<object>()
            });

            var response = await client.SendAsync(request, ct);

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    new ValidationCheckResult("Anthropic API key", CheckStatus.Fail, "API key rejected (401 Unauthorized)"),
                System.Net.HttpStatusCode.Forbidden =>
                    new ValidationCheckResult("Anthropic API key", CheckStatus.Fail, "API key forbidden (403 Forbidden)"),
                _ =>
                    new ValidationCheckResult("Anthropic API key", CheckStatus.Pass,
                        $"Key accepted by API (HTTP {(int)response.StatusCode})")
            };
        }
        catch (Exception ex)
        {
            return new ValidationCheckResult("Anthropic API key", CheckStatus.Fail, $"Request failed: {ex.Message}");
        }
    }

    private async Task<ValidationCheckResult> CheckBrowserlessAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var scrapeUrl = _browserless.BaseUrl.TrimEnd('/') + "/scrape";
            using var request = new HttpRequestMessage(HttpMethod.Post, scrapeUrl)
            {
                Content = JsonContent.Create(new
                {
                    url = "https://example.com",
                    elements = new[] { new { selector = "h1" } },
                    gotoOptions = new { waitUntil = "networkidle2", timeout = 10000 }
                })
            };

            var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new ValidationCheckResult("Browserless reachability", CheckStatus.Fail,
                    $"Browserless scrape endpoint failed at {scrapeUrl} (HTTP {(int)response.StatusCode})");
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            return payload.Contains("Example Domain", StringComparison.Ordinal)
                ? new ValidationCheckResult("Browserless reachability", CheckStatus.Pass,
                    $"Scrape endpoint responded successfully at {scrapeUrl}")
                : new ValidationCheckResult("Browserless reachability", CheckStatus.Fail,
                    $"Scrape endpoint responded at {scrapeUrl} but did not return expected content");
        }
        catch (Exception ex)
        {
            return new ValidationCheckResult("Browserless reachability", CheckStatus.Fail,
                $"Cannot reach {_browserless.BaseUrl}: {ex.Message}");
        }
    }

    private async Task<ValidationCheckResult> CheckSmtpAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_smtp.Host))
            return new ValidationCheckResult("SMTP connectivity", CheckStatus.Fail, "Smtp:Host is not set");

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.Auto, ct);

            if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            {
                await client.DisconnectAsync(true, ct);
                return new ValidationCheckResult("SMTP connectivity", CheckStatus.Warn,
                    $"Connected to {_smtp.Host}:{_smtp.Port} — server does not require authentication (e.g. Mailpit)");
            }

            if (string.IsNullOrWhiteSpace(_smtp.Username) || string.IsNullOrWhiteSpace(_smtp.Password))
            {
                await client.DisconnectAsync(true, ct);
                return new ValidationCheckResult("SMTP connectivity", CheckStatus.Warn,
                    $"Connected to {_smtp.Host}:{_smtp.Port} but credentials are not set — authentication skipped");
            }

            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
            await client.DisconnectAsync(true, ct);

            return new ValidationCheckResult("SMTP connectivity", CheckStatus.Pass,
                $"Connected and authenticated to {_smtp.Host}:{_smtp.Port}");
        }
        catch (AuthenticationException ex)
        {
            return new ValidationCheckResult("SMTP connectivity", CheckStatus.Fail,
                $"Authentication failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ValidationCheckResult("SMTP connectivity", CheckStatus.Fail,
                $"Connection failed: {ex.Message}");
        }
    }

    private async Task<List<ValidationCheckResult>> CheckTalkingPointsParentsAsync(CancellationToken ct)
    {
        List<ValidationCheckResult> results = [];
        List<Parent> parents;

        try
        {
            parents = await _db.Parents.Where(p => p.IsActive).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            results.Add(new ValidationCheckResult("TalkingPoints (parents)", CheckStatus.Fail,
                $"Could not load parents from database: {ex.Message}"));
            return results;
        }

        if (parents.Count == 0)
        {
            results.Add(new ValidationCheckResult("TalkingPoints (parents)", CheckStatus.Warn,
                "No active parents registered in the database"));
            return results;
        }

        foreach (var parent in parents)
        {
            var checkName = $"TalkingPoints — {parent.Name}";
            try
            {
                var messages = await _talkingPointsClient.FetchMessagesAsync(parent, ct);
                var (status, detail) = messages.Count > 0
                    ? (CheckStatus.Pass, $"API returned {messages.Count} message(s)")
                    : (CheckStatus.Warn, "API succeeded but returned 0 messages (token may be expired or no messages exist)");
                results.Add(new ValidationCheckResult(checkName, status, detail));
            }
            catch (Exception ex)
            {
                results.Add(new ValidationCheckResult(checkName, CheckStatus.Fail, ex.Message));
            }
        }

        return results;
    }
}
