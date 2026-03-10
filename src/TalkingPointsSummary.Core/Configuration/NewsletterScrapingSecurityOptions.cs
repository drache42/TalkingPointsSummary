namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values that restrict which newsletter URLs may be scraped.
/// </summary>
public sealed class NewsletterScrapingSecurityOptions
{
    /// <summary>
    /// Configuration section name for newsletter scraping security settings.
    /// </summary>
    public const string SectionName = "NewsletterScrapingSecurity";

    /// <summary>
    /// Whether newsletter URL validation is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether scraped newsletter URLs must use HTTPS unless explicitly exempted.
    /// </summary>
    public bool RequireHttps { get; init; } = true;

    /// <summary>
    /// Hosts allowed to bypass private-address blocking.
    /// </summary>
    public List<string> AllowedHosts { get; init; } = [];

    /// <summary>
    /// Hosts allowed to use HTTP when HTTPS is otherwise required.
    /// </summary>
    public List<string> AllowHttpHosts { get; init; } = [];
}