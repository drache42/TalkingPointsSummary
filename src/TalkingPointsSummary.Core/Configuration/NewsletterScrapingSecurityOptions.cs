namespace TalkingPointsSummary.Configuration;

public sealed class NewsletterScrapingSecurityOptions
{
    public const string SectionName = "NewsletterScrapingSecurity";

    public bool Enabled { get; init; } = true;

    public bool RequireHttps { get; init; } = true;

    public List<string> AllowedHosts { get; init; } = [];

    public List<string> AllowHttpHosts { get; init; } = [];
}