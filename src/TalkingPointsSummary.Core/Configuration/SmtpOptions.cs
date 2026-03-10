using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values used when sending summary emails over SMTP.
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>
    /// Configuration section name for SMTP settings.
    /// </summary>
    public const string SectionName = "Smtp";

    /// <summary>
    /// SMTP host name.
    /// </summary>
    [Required]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// SMTP port number.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>
    /// Username used for SMTP authentication.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password used for SMTP authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Email address used as the sender.
    /// </summary>
    [Required]
    [EmailAddress]
    public string FromEmail { get; set; } = string.Empty;
}