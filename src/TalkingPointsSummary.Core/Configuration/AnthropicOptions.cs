using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    [Required]
    public string ApiKey { get; set; } = string.Empty;
}