using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Uses the configured AI provider as an adversarial reviewer of a generated digest, checking it
/// against the news items it was written from, the active events, and what earlier digests already
/// delivered.
/// </summary>
/// <remarks>
/// This service is advisory and is deliberately incapable of stopping a send. Every failure short
/// of caller-requested cancellation is swallowed into an empty finding list, because a parent
/// receiving an unreviewed digest is a far better outcome than a parent receiving nothing because
/// the reviewer timed out.
/// </remarks>
public partial class SummaryCritic : ISummaryCritic
{
    private static readonly SummaryCritiquePromptBuilder PromptBuilder = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyList<CritiqueFinding> NoFindings = Array.Empty<CritiqueFinding>();

    private readonly IAiClient _aiClient;
    private readonly AiOptions _options;
    private readonly ILogger<SummaryCritic> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _scheduleTimeZone;

    /// <summary>
    /// Initializes a summary critic.
    /// </summary>
    /// <param name="aiClient">AI client used to send critique requests.</param>
    /// <param name="aiOptions">AI configuration including the critique profile.</param>
    /// <param name="schedule">
    /// Pipeline schedule configuration, used for the local timezone that source item send dates
    /// are read in so relative date phrases resolve against the school's calendar day.
    /// </param>
    /// <param name="logger">Logger used for critique diagnostics.</param>
    /// <param name="timeProvider">Optional time provider used to bound the reference calendar.</param>
    public SummaryCritic(
        IAiClient aiClient,
        IOptions<AiOptions> aiOptions,
        IOptions<PipelineScheduleOptions> schedule,
        ILogger<SummaryCritic> logger,
        TimeProvider? timeProvider = null)
    {
        _aiClient = aiClient;
        _options = aiOptions.Value;
        _scheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Value.TimeZone);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CritiqueFinding>> CritiqueAsync(
        SummaryCritiqueRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DraftMarkdown))
        {
            _logger.LogWarning("Summary critique skipped because the draft digest is empty.");
            return NoFindings;
        }

        AiCompletionResult aiResult;

        try
        {
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.GetUtcDateTime(), _scheduleTimeZone);

            var prompt = PromptBuilder.Build(
                request.SourceItems,
                request.ActiveEvents,
                request.CoverageLedger,
                request.DraftMarkdown,
                nowLocal,
                _scheduleTimeZone);

            var profile = _options.Profiles.Critique;

            _logger.LogInformation(
                "Critiquing draft digest against {SourceItemCount} source item(s) " +
                "(model: {Model}, maxTokens: {MaxTokens}, thinking: {Thinking}, effort: {Effort})",
                request.SourceItems.Count,
                profile.ModelId,
                profile.MaxTokens,
                profile.Thinking,
                profile.Effort ?? "none");

            // The reasoning settings travel with the profile. A critic run with thinking off would
            // still cost the raised token ceiling while missing exactly the date-arithmetic errors
            // it exists to catch.
            aiResult = await _aiClient.CompleteAsync(
                new AiCompletionRequest(
                    prompt,
                    profile.ModelId,
                    profile.MaxTokens,
                    profile.Thinking,
                    profile.ThinkingBudgetTokens,
                    profile.Effort),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller is shutting the run down. That is not a critic failure to absorb.
            throw;
        }
        catch (Exception ex)
        {
            // Anything else, a provider error, a request timeout, a missing prompt template, is
            // absorbed here. Rethrowing would take the already-generated digest down with it.
            _logger.LogWarning(ex,
                "Summary critique request failed; proceeding with the draft digest unreviewed.");
            return NoFindings;
        }

        // Unlike a truncated digest, which must never be emailed, a truncated critique is simply
        // discarded: its JSON is cut off mid-object and there is nothing trustworthy to salvage.
        if (AiResponseTruncatedException.IsTruncated(aiResult.StopReason))
        {
            _logger.LogWarning(
                "Summary critique was truncated at the max_tokens limit of {MaxTokens}; " +
                "proceeding with the draft digest unreviewed.",
                _options.Profiles.Critique.MaxTokens);
            return NoFindings;
        }

        if (AiResponseRefusedException.IsRefusal(aiResult.StopReason))
        {
            // A refusal's text block is prose about declining, not a findings document. Parsing it
            // would fail anyway; saying so plainly keeps the log honest about why the digest went
            // out unreviewed.
            _logger.LogWarning(
                "Model {Model} refused to critique the draft digest (stop reason '{StopReason}'); "
                + "proceeding with the draft digest unreviewed.",
                _options.Profiles.Critique.ModelId, aiResult.StopReason);
            return NoFindings;
        }

        if (string.IsNullOrWhiteSpace(aiResult.Text))
        {
            // Observed in practice: a response whose first content block is a thinking block with
            // no text. There is no critique in it, and the digest still has to go out.
            _logger.LogWarning(
                "Summary critique returned no text (stop reason: {StopReason}); " +
                "proceeding with the draft digest unreviewed.",
                aiResult.StopReason ?? "none");
            return NoFindings;
        }

        return ParseFindings(aiResult.Text, request.SourceItems.Count);
    }

    private IReadOnlyList<CritiqueFinding> ParseFindings(string rawText, int sourceItemCount)
    {
        var text = StripCodeFences().Replace(rawText, "").Trim();

        CritiqueJsonResponse? response;

        try
        {
            response = JsonSerializer.Deserialize<CritiqueJsonResponse>(text, SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse the summary critique response; proceeding with the draft digest " +
                "unreviewed. Raw: {Text}",
                text);
            return NoFindings;
        }

        var candidates = response?.Findings;
        if (candidates is null || candidates.Count == 0)
        {
            _logger.LogInformation("Summary critique found no defects in the draft digest.");
            return NoFindings;
        }

        var findings = new List<CritiqueFinding>(candidates.Count);

        foreach (var candidate in candidates)
        {
            // A finding with nothing to say is noise that would push the reviser into rewriting
            // text for no stated reason, so it is dropped rather than passed on.
            var problem = candidate.Problem?.Trim();
            if (string.IsNullOrEmpty(problem))
                continue;

            var kind = NormalizeKind(candidate.Kind);

            // An omitted-item finding is useless without a source item number the orchestrator can
            // trust: it is the only thing that tells it which news item to leave unreported so the
            // item survives into next week's digest rather than being marked delivered for content
            // the parent never received. A number outside the range of source items this request
            // carried is a hallucination, not a real reference, so the finding is dropped entirely
            // rather than risk misfiling a completely different item as omitted.
            int? sourceItemNumber = null;
            if (kind == CritiqueFindingKinds.OmittedItem)
            {
                if (candidate.SourceItemNumber is not int number
                    || number < 1
                    || number > sourceItemCount)
                {
                    _logger.LogWarning(
                        "Discarding an omitted-item critique finding with an unusable source item "
                        + "number {SourceItemNumber} against {SourceItemCount} source item(s).",
                        candidate.SourceItemNumber, sourceItemCount);
                    continue;
                }

                sourceItemNumber = number;
            }

            findings.Add(new CritiqueFinding(
                // Forced rather than trusted: an omitted item is bookkeeping, not a defect, and
                // must never be able to trigger a revision attempt by arriving as high or medium.
                kind == CritiqueFindingKinds.OmittedItem ? CritiqueSeverity.Low : ParseSeverity(candidate.Severity),
                kind,
                candidate.Quote?.Trim() ?? string.Empty,
                problem,
                candidate.SuggestedFix?.Trim() ?? string.Empty,
                sourceItemNumber));
        }

        _logger.LogInformation(
            "Summary critique returned {FindingCount} finding(s) in the draft digest.",
            findings.Count);

        return findings;
    }

    /// <summary>
    /// Maps the severity word the model returned onto the enum.
    /// </summary>
    /// <remarks>
    /// An unrecognized or missing severity becomes <see cref="CritiqueSeverity.Medium"/>. Treating
    /// it as low would quietly bury a real defect, and treating it as high would let one sloppy
    /// answer hold up a digest.
    /// </remarks>
    /// <param name="value">Severity word from the model response.</param>
    internal static CritiqueSeverity ParseSeverity(string? value)
    {
        var text = value?.Trim();

        if (string.Equals(text, "high", StringComparison.OrdinalIgnoreCase))
            return CritiqueSeverity.High;

        if (string.Equals(text, "low", StringComparison.OrdinalIgnoreCase))
            return CritiqueSeverity.Low;

        return CritiqueSeverity.Medium;
    }

    /// <summary>
    /// Maps the kind the model returned onto one of the known kinds.
    /// </summary>
    /// <remarks>
    /// A kind that matches none of them is preserved as written rather than discarded: the finding
    /// still carries a problem and a fix, and silently dropping it would hide a real defect behind
    /// a vocabulary mismatch.
    /// </remarks>
    /// <param name="value">Kind string from the model response.</param>
    internal static string NormalizeKind(string? value)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text))
            return CritiqueFindingKinds.Unspecified;

        foreach (var known in CritiqueFindingKinds.All)
        {
            if (string.Equals(text, known, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return text;
    }

    [GeneratedRegex(@"```json|```")]
    private static partial Regex StripCodeFences();
}
