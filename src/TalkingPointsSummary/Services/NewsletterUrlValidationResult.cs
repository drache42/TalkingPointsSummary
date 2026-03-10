namespace TalkingPointsSummary.Services;

public sealed record NewsletterUrlValidationResult(bool Allowed, string Reason)
{
    public static NewsletterUrlValidationResult Allow() => new(true, "Allowed");

    public static NewsletterUrlValidationResult Block(string reason) => new(false, reason);
}