namespace TalkingPointsSummary.Services;

/// <summary>
/// Exception thrown when a requested entity does not exist.
/// </summary>
public sealed class EntityNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new exception with a not-found message.
    /// </summary>
    /// <param name="message">Explanation of which entity could not be found.</param>
    public EntityNotFoundException(string message) : base(message)
    {
    }
}