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
public class EmailSender : IEmailSender
{
    private readonly SmtpOptions _smtp;
    private readonly ILogger<EmailSender> _logger;

    /// <summary>
    /// Initializes an SMTP email sender.
    /// </summary>
    /// <param name="smtp">SMTP configuration options.</param>
    /// <param name="logger">Logger used for delivery diagnostics.</param>
    public EmailSender(IOptions<SmtpOptions> smtp, ILogger<EmailSender> logger)
    {
        _smtp = smtp.Value;
        _logger = logger;
    }

    /// <summary>
    /// Sends an HTML email to the specified recipients.
    /// </summary>
    /// <param name="recipients">Semicolon-delimited email addresses.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="htmlBody">HTML content of the email.</param>
    /// <param name="ct">Token used to cancel the send operation.</param>
    public async Task SendAsync(string recipients, string subject, string htmlBody, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_smtp.FromEmail));

        foreach (var email in recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(MailboxAddress.Parse(email));
        }

        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
        message.Body = bodyBuilder.ToMessageBody();

        _logger.LogInformation("Sending email to {Recipients} with subject '{Subject}'", recipients, subject);

        using var client = new SmtpClient();
        await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.Auto, ct);

        if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication)
            && !string.IsNullOrWhiteSpace(_smtp.Username))
        {
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("Email sent successfully");
    }
}
