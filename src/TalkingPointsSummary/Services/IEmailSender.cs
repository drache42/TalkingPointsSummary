namespace TalkingPointsSummary.Services;

/// <summary>
/// Sends rendered summary emails.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email to one or more recipients.
    /// </summary>
    /// <param name="recipients">Recipient list understood by the sender implementation.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="htmlBody">HTML body content.</param>
    /// <param name="ct">Token used to cancel the send operation.</param>
    Task SendAsync(string recipients, string subject, string htmlBody, CancellationToken ct = default);
}
