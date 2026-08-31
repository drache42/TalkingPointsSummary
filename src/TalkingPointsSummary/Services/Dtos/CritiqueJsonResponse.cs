using System.Text.Json.Serialization;

namespace TalkingPointsSummary.Services;

/// <summary>
/// JSON shape expected from the summary critique model response.
/// </summary>
public class CritiqueJsonResponse
{
    /// <summary>
    /// Defects the critic found in the draft digest. An empty or missing list means the
    /// critic found nothing, which is the expected answer for a clean draft.
    /// </summary>
    [JsonPropertyName("findings")]
    public List<CritiqueJsonFinding>? Findings { get; set; }
}

/// <summary>
/// A single defect returned by the summary critique model.
/// </summary>
public class CritiqueJsonFinding
{
    /// <summary>
    /// Severity word returned by the model: "high", "medium", or "low".
    /// </summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    /// <summary>
    /// Defect kind returned by the model, expected to be one of
    /// <see cref="CritiqueFindingKinds.All"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>
    /// Text the model copied out of the draft digest to locate the defect.
    /// </summary>
    [JsonPropertyName("quote")]
    public string? Quote { get; set; }

    /// <summary>
    /// What the model says is wrong with the quoted text.
    /// </summary>
    [JsonPropertyName("problem")]
    public string? Problem { get; set; }

    /// <summary>
    /// Corrected text or repair instruction proposed by the model.
    /// </summary>
    [JsonPropertyName("suggested_fix")]
    public string? SuggestedFix { get; set; }
}
