namespace TalkingPointsSummary.Services;

/// <summary>
/// Result returned when validating whether a newsletter URL may be scraped.
/// </summary>
public sealed record NewsletterUrlValidationResult
{
    /// <summary>
    /// Initializes a new validation result.
    /// </summary>
    /// <param name="allowed">Whether the URL is allowed.</param>
    /// <param name="reason">Explanation of the decision.</param>
    public NewsletterUrlValidationResult(bool allowed, string reason)
    {
        Allowed = allowed;
        Reason = reason;
    }

    /// <summary>
    /// Whether the URL is allowed.
    /// </summary>
    public bool Allowed { get; init; }

    /// <summary>
    /// Explanation of the validation decision.
    /// </summary>
    public string Reason { get; init; }

    /// <summary>
    /// Creates an allowed validation result.
    /// </summary>
    public static NewsletterUrlValidationResult Allow() => new(true, "Allowed");

    /// <summary>
    /// Creates a blocked validation result.
    /// </summary>
    /// <param name="reason">Explanation of why the URL was blocked.</param>
    public static NewsletterUrlValidationResult Block(string reason) => new(false, reason);
}