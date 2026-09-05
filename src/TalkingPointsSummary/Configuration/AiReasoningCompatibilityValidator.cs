using Microsoft.Extensions.Options;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Cross-checks each profile's <c>Thinking</c> mode against its <c>ModelId</c>, using whichever
/// <see cref="IAiReasoningCompatibility"/> the configured AI provider registers. This class names
/// no vendor or model family itself; that knowledge lives entirely behind the interface, so
/// adding a second provider means adding a second implementation, not touching this validator.
/// </summary>
internal sealed class AiReasoningCompatibilityValidator : IValidateOptions<AiOptions>
{
    private readonly IAiReasoningCompatibility _compatibility;

    public AiReasoningCompatibilityValidator(IAiReasoningCompatibility compatibility)
    {
        _compatibility = compatibility;
    }

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var failures = new List<string>();

        foreach (var (profileName, profile) in options.Profiles.All())
        {
            CheckProfile(profileName, profile, failures);
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private void CheckProfile(string profileName, AiProfileOptions profile, List<string> failures)
    {
        if (!_compatibility.IsCompatible(profile.ModelId, profile.Thinking))
        {
            failures.Add(_compatibility.IncompatibleMessage(profileName));
        }
    }
}
