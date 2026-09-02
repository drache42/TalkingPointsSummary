using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// How badly a critique finding would mislead the parent who reads the digest.
/// </summary>
public enum CritiqueSeverity
{
    /// <summary>
    /// Cosmetic or redundant content. The parent is not misled.
    /// </summary>
    Low,

    /// <summary>
    /// The draft is wrong, but a parent acting on it would not miss anything.
    /// </summary>
    Medium,

    /// <summary>
    /// A parent acting on the draft would go to the wrong place on the wrong day
    /// or miss a deadline.
    /// </summary>
    High
}

/// <summary>
/// The defect classes the critic is asked to report. The values are the literal strings
/// the prompt template names, so a finding round-trips between the model and this code
/// without translation.
/// </summary>
public static class CritiqueFindingKinds
{
    /// <summary>
    /// A relative date reference in a source message ("next Friday", "tomorrow") that the
    /// draft resolved to the wrong absolute date. This is the finding no code check can
    /// reproduce, because only the critic sees the original wording beside the resolved date.
    /// </summary>
    public const string UnresolvedRelativeDate = "unresolved-relative-date";

    /// <summary>
    /// An item the draft assigned to the wrong child or the wrong school.
    /// </summary>
    public const string WrongAttribution = "wrong-attribution";

    /// <summary>
    /// A statement in the draft that appears in no source news item.
    /// </summary>
    public const string UnsupportedClaim = "unsupported-claim";

    /// <summary>
    /// Content the coverage ledger shows an earlier digest already delivered.
    /// </summary>
    public const string Repeat = "repeat";

    /// <summary>
    /// Two active events that read as the same event on different dates.
    /// </summary>
    public const string ConflictingEvent = "conflicting-event";

    /// <summary>
    /// A source item that has no trace anywhere in the draft: no title, fact, name, date, or any
    /// other detail from it appears, not even merged into another item's sentence.
    /// </summary>
    /// <remarks>
    /// This is not a defect the digest needs correcting for: the prompt tells the model to merge
    /// or drop the weakest items rather than exceed its length and bullet caps, so an omission is
    /// often the correct outcome for a busy week. It is reported anyway so the orchestrator can
    /// leave the omitted item's <see cref="TalkingPointsSummary.Models.NewsItem.IncludedInSummaryId"/>
    /// unset, which carries it into next week's digest instead of marking it reported for content
    /// the parent never actually received. This kind is deliberately excluded from the revision
    /// prompt and from the decision to attempt a revision at all: asking the reviser to "fix" an
    /// omission would just mean cramming the item back in, which undoes the caps this finding
    /// exists to respect.
    /// </remarks>
    public const string OmittedItem = "omitted-item";

    /// <summary>
    /// Placeholder used when the model returned a finding with no kind. The finding is kept
    /// rather than discarded: the problem text still describes a real defect.
    /// </summary>
    public const string Unspecified = "unspecified";

    /// <summary>
    /// All defect kinds the prompt asks the critic for, excluding
    /// <see cref="Unspecified"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        UnresolvedRelativeDate,
        WrongAttribution,
        UnsupportedClaim,
        Repeat,
        ConflictingEvent,
        OmittedItem
    ];
}

/// <summary>
/// A single defect the critic found in a draft digest.
/// </summary>
/// <param name="Severity">How badly the defect would mislead the parent.</param>
/// <param name="Kind">
/// One of the values in <see cref="CritiqueFindingKinds"/>. A kind the model invented that
/// matches none of them is preserved verbatim rather than discarded.
/// </param>
/// <param name="Quote">Text copied from the draft that carries the defect. May be empty.</param>
/// <param name="Problem">What is wrong, and which source item proves it.</param>
/// <param name="SuggestedFix">Corrected text, or the instruction needed to fix it. May be empty.</param>
/// <param name="SourceItemNumber">
/// The 1-based "SOURCE ITEM N" number the finding is about. Populated (and range-checked against
/// the source items the request carried) only for <see cref="CritiqueFindingKinds.OmittedItem"/>;
/// <see langword="null"/> for every other kind.
/// </param>
public sealed record CritiqueFinding(
    CritiqueSeverity Severity,
    string Kind,
    string Quote,
    string Problem,
    string SuggestedFix,
    int? SourceItemNumber = null);

/// <summary>
/// Everything the critic is shown when reviewing one draft digest.
/// </summary>
/// <param name="SourceItems">
/// The news items the digest was generated from, in any order. Their
/// <see cref="NewsItem.SentAt"/> values are what every relative date reference in their prose
/// resolves against, so the critic can only check date resolution when they are supplied.
/// </param>
/// <param name="DraftMarkdown">The generated digest markdown under review.</param>
/// <param name="ActiveEvents">
/// The active tracked events, rendered exactly as the summary generator rendered them, so the
/// critic reviews the list the draft was actually written from. Null renders as "None".
/// </param>
/// <param name="CoverageLedger">
/// What earlier digests already delivered to this parent, used to catch repeats.
/// Null renders as "None".
/// </param>
public sealed record SummaryCritiqueRequest(
    IReadOnlyList<NewsItem> SourceItems,
    string DraftMarkdown,
    string? ActiveEvents = null,
    string? CoverageLedger = null);

/// <summary>
/// Reviews a generated digest against the sources it was written from and reports defects.
/// </summary>
public interface ISummaryCritic
{
    /// <summary>
    /// Critiques a draft digest and returns the defects found.
    /// </summary>
    /// <remarks>
    /// The critic is advisory and must never stop a digest from being emailed. Every failure
    /// mode short of caller-requested cancellation (a provider error, a timeout, a truncated
    /// response, malformed JSON, an empty completion) is logged and reported as zero findings,
    /// so the caller proceeds with the draft it already has.
    /// </remarks>
    /// <param name="request">Draft digest and the sources it should be checked against.</param>
    /// <param name="ct">Token used to cancel the critique request.</param>
    /// <returns>The defects found, empty when the draft is clean or the critique failed.</returns>
    Task<IReadOnlyList<CritiqueFinding>> CritiqueAsync(
        SummaryCritiqueRequest request,
        CancellationToken ct = default);
}
