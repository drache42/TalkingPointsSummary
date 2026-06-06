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

/// <summary>
/// Severity assigned to an individual startup validation check.
/// </summary>
public enum CheckStatus
{
    /// <summary>
    /// The check passed.
    /// </summary>
    Pass,

    /// <summary>
    /// The check completed with a warning.
    /// </summary>
    Warn,

    /// <summary>
    /// The check failed.
    /// </summary>
    Fail
}

/// <summary>
/// Result returned by a single startup validation check.
/// </summary>
public record ValidationCheckResult
{
    /// <summary>
    /// Initializes a new validation check result.
    /// </summary>
    /// <param name="name">Display name of the check.</param>
    /// <param name="status">Severity assigned to the check result.</param>
    /// <param name="detail">Human-readable detail for the result.</param>
    public ValidationCheckResult(string name, CheckStatus status, string detail)
    {
        Name = name;
        Status = status;
        Detail = detail;
    }

    /// <summary>
    /// Display name of the check.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Severity assigned to the result.
    /// </summary>
    public CheckStatus Status { get; init; }

    /// <summary>
    /// Human-readable detail for the result.
    /// </summary>
    public string Detail { get; init; }
}

/// <summary>
/// Validates all required secrets and external service connections before the worker starts.
/// </summary>
public class StartupValidator
{
    private readonly AiOptions _ai;
    private readonly BrowserlessOptions _browserless;
    private readonly SmtpOptions _smtp;
    private readonly AppDbContext _db;
    private readonly ITalkingPointsApiClient _talkingPointsClient;
    private readonly IAiClient _aiClient;
    private readonly ILogger<StartupValidator> _logger;

    /// <summary>
    /// Initializes a startup validator for required configuration and external dependencies.
    /// </summary>
    /// <param name="ai">AI configuration.</param>
    /// <param name="browserless">Browserless configuration.</param>
    /// <param name="smtp">SMTP configuration.</param>
    /// <param name="db">Database context used to verify connectivity and migrations.</param>
    /// <param name="talkingPointsClient">TalkingPoints client used to probe API access.</param>
    /// <param name="aiClient">AI client used to validate credentials.</param>
    /// <param name="logger">Logger used for validation diagnostics.</param>
    public StartupValidator(
        IOptions<AiOptions> ai,
        IOptions<BrowserlessOptions> browserless,
        IOptions<SmtpOptions> smtp,
        AppDbContext db,
        ITalkingPointsApiClient talkingPointsClient,
        IAiClient aiClient,
        ILogger<StartupValidator> logger)
    {
        _ai = ai.Value;
        _browserless = browserless.Value;
        _smtp = smtp.Value;
        _db = db;
        _talkingPointsClient = talkingPointsClient;
        _aiClient = aiClient;
        _logger = logger;
    }

    /// <summary>
    /// Runs all startup validation checks and returns their results.
    /// </summary>
    /// <param name="ct">Token used to cancel validation.</param>
    public async Task<List<ValidationCheckResult>> RunAllChecksAsync(CancellationToken ct = default)
    {
        var results = new List<ValidationCheckResult>
        {
            CheckConfigPresence()
        };

        results.Add(await CheckDatabaseAsync(ct));
        results.Add(await CheckAiCredentialsAsync(ct));
        results.Add(await CheckBrowserlessAsync(ct));
        results.Add(await CheckSmtpAsync(ct));
        results.AddRange(await CheckTalkingPointsParentsAsync(ct));

        return results;
    }

    private ValidationCheckResult CheckConfigPresence()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(_ai.Anthropic.ApiKey))
            missing.Add("Ai:Anthropic:ApiKey");
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

    private async Task<ValidationCheckResult> CheckAiCredentialsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_ai.Anthropic.ApiKey))
            return new ValidationCheckResult("AI credentials", CheckStatus.Fail, "Ai:Anthropic:ApiKey is not set");

        var result = await _aiClient.ValidateCredentialsAsync(ct);
        return result.IsValid
            ? new ValidationCheckResult("AI credentials", CheckStatus.Pass, result.Reason)
            : new ValidationCheckResult("AI credentials", CheckStatus.Fail, result.Reason);
    }

    private async Task<ValidationCheckResult> CheckBrowserlessAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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
                var messages = await _talkingPointsClient.FetchMessagesAsync(parent, maxPagesOverride: 1, ct: ct);
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
