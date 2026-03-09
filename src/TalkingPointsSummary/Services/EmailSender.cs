using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Sends HTML email via SMTP using MailKit.
/// </summary>
public class EmailSender
{
    private readonly AppSettings _settings;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<AppSettings> settings, ILogger<EmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Sends an HTML email to the specified recipients.
    /// </summary>
    /// <param name="recipients">Semicolon-delimited email addresses.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="htmlBody">HTML content of the email.</param>
    public async Task SendAsync(string recipients, string subject, string htmlBody, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_settings.Smtp.FromEmail));

        foreach (var email in recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(MailboxAddress.Parse(email));
        }

        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
        message.Body = bodyBuilder.ToMessageBody();

        _logger.LogInformation("Sending email to {Recipients} with subject '{Subject}'", recipients, subject);

        using var client = new SmtpClient();
        await client.ConnectAsync(_settings.Smtp.Host, _settings.Smtp.Port, SecureSocketOptions.Auto, ct);

        if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication)
            && !string.IsNullOrWhiteSpace(_settings.Smtp.Username))
        {
            await client.AuthenticateAsync(_settings.Smtp.Username, _settings.Smtp.Password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("Email sent successfully");
    }
}
