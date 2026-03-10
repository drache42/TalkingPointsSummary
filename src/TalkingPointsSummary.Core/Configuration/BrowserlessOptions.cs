using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values for the Browserless scraping service.
/// </summary>
public sealed class BrowserlessOptions
{
    /// <summary>
    /// Configuration section name for Browserless settings.
    /// </summary>
    public const string SectionName = "Browserless";

    /// <summary>
    /// Base URL used for Browserless HTTP requests.
    /// </summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;
}