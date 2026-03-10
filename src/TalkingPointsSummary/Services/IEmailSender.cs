namespace TalkingPointsSummary.Services;

public interface IEmailSender
{
    Task SendAsync(string recipients, string subject, string htmlBody, CancellationToken ct = default);
}
