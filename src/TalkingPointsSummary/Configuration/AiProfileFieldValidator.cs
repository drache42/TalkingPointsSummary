using Microsoft.Extensions.Options;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Validates each AI profile's ModelId, Thinking, Effort, MaxTokens, and ThinkingBudgetTokens
/// fields by looping over the three profiles rather than repeating one .Validate() call per
/// profile per rule. A rule added later needs one change to the loop body, not a copy-pasted line
/// per profile that is easy to forget for one of them.
/// </summary>
internal sealed class AiProfileFieldValidator : IValidateOptions<AiOptions>
{
    private const int MinimumThinkingBudgetTokens = 1024;

    /// <summary>
    /// Smallest number of tokens that must remain after the thinking budget for the model to
    /// still produce a usable visible answer. Thinking tokens count against MaxTokens, so a
    /// budget merely smaller than MaxTokens is not enough: MaxTokens=1025 with a budget of 1024
    /// passes that check while leaving 1 token for the actual response.
    /// </summary>
    private const int MinimumOutputTokensAfterThinkingBudget = 256;

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var failures = new List<string>();

        CheckProfile("Categorization", options.Profiles.Categorization, failures);
        CheckProfile("Summarization", options.Profiles.Summarization, failures);
        CheckProfile("Validation", options.Profiles.Validation, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void CheckProfile(string profileName, AiProfileOptions profile, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(profile.ModelId))
        {
            failures.Add($"Ai:Profiles:{profileName}:ModelId is required.");
        }

        if (!AiThinkingModes.All.Contains(profile.Thinking, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Ai:Profiles:{profileName}:Thinking must be one of: {string.Join(", ", AiThinkingModes.All)}.");
        }

        if (profile.Effort is not null
            && !AiEffortLevels.All.Contains(profile.Effort, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Ai:Profiles:{profileName}:Effort must be one of: {string.Join(", ", AiEffortLevels.All)}, "
                + "or omitted.");
        }

        foreach (var field in AiReasoningFieldRules.AllFields)
        {
            if (!UsesReasoningFieldCorrectly(profile, field))
            {
                failures.Add(
                    $"Ai:Profiles:{profileName}:{field} is only used when Ai:Profiles:{profileName}:Thinking is "
                    + $"'{AiReasoningFieldRules.RequiredModeFor(field)}'; it is otherwise ignored.");
            }
        }

        if (profile.MaxTokens < 1)
        {
            failures.Add($"Ai:Profiles:{profileName}:MaxTokens must be at least 1.");
        }

        if (!HasValidThinkingBudget(profile))
        {
            failures.Add(
                $"Ai:Profiles:{profileName}:ThinkingBudgetTokens must be at least {MinimumThinkingBudgetTokens} "
                + $"and leave at least {MinimumOutputTokensAfterThinkingBudget} tokens of "
                + $"Ai:Profiles:{profileName}:MaxTokens for the visible response when Thinking is 'budget'.");
        }
    }

    private static bool UsesReasoningFieldCorrectly(AiProfileOptions profile, AiReasoningField field)
    {
        var isSet = field switch
        {
            AiReasoningField.Effort => profile.Effort is not null,
            AiReasoningField.ThinkingBudgetTokens => profile.ThinkingBudgetTokens != 0,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        return !isSet || AiReasoningFieldRules.AppliesTo(field, profile.Thinking);
    }

    private static bool HasValidThinkingBudget(AiProfileOptions profile)
    {
        if (!string.Equals(profile.Thinking, AiThinkingModes.Budget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return profile.ThinkingBudgetTokens >= MinimumThinkingBudgetTokens
            && profile.ThinkingBudgetTokens <= profile.MaxTokens - MinimumOutputTokensAfterThinkingBudget;
    }
}
