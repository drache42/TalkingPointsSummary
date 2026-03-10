using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

public sealed class BrowserlessOptions
{
    public const string SectionName = "Browserless";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;
}