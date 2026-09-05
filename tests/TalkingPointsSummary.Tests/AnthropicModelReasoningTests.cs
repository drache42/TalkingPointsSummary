using FluentAssertions;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

/// <summary>
/// Covers the model-id parsing behind the startup cross-check directly. A misparse here either
/// blocks a valid configuration at startup or lets an impossible model and thinking pairing
/// through to fail with HTTP 400 on every single call, which reads as an outage rather than a
/// configuration mistake.
/// </summary>
public class AnthropicModelReasoningTests
{
    private readonly AnthropicModelReasoning _reasoning = new();

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-haiku-5")]
    // A gateway prefix sits in front of the family and version but changes neither.
    [InlineData("anthropic/claude-sonnet-5")]
    [InlineData("us.anthropic.claude-opus-5-v1:0")]
    public void GetShape_ClaudeFiveAndLater_IsAdaptive(string modelId)
    {
        AnthropicModelReasoning.GetShape(modelId).Should().Be(AnthropicReasoningShape.Adaptive);
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("claude-haiku-4-5-20251001")]
    // The version sits before the family in this generation's ids, so both parts have to be
    // located independently rather than by position.
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-3-haiku-20240307")]
    [InlineData("us.anthropic.claude-3-5-sonnet-20241022-v2:0")]
    [InlineData("anthropic/claude-sonnet-4-5-20250929")]
    public void GetShape_ClaudeFourFiveAndEarlier_IsBudget(string modelId)
    {
        AnthropicModelReasoning.GetShape(modelId).Should().Be(AnthropicReasoningShape.Budget);
    }

    [Theory]
    // Opus and Sonnet 4.7/4.8: budget_tokens is removed there, so only adaptive is accepted even
    // though the major version is 4, not 5.
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-opus-4-8")]
    [InlineData("us.anthropic.claude-opus-4-7-v1:0")]
    public void GetShape_OpusOrSonnetFourSevenAndLater_IsAdaptive(string modelId)
    {
        AnthropicModelReasoning.GetShape(modelId).Should().Be(AnthropicReasoningShape.Adaptive);
    }

    [Theory]
    // Opus and Sonnet 4.6 only: budget_tokens still works there as a deprecated transitional
    // escape hatch alongside the recommended adaptive shape, so both are accepted.
    [InlineData("claude-opus-4-6")]
    [InlineData("claude-sonnet-4-6")]
    public void GetShape_OpusOrSonnetFourSix_IsEither(string modelId)
    {
        AnthropicModelReasoning.GetShape(modelId).Should().Be(AnthropicReasoningShape.Either);
    }

    [Fact]
    public void GetShape_HaikuAtTheOpusSonnetTransitionalMinorVersion_IsStillBudget()
    {
        // Haiku has no adaptive-capable release, so the 4.6 carve-out that applies to Opus and
        // Sonnet must not also apply to Haiku just because the minor version number matches.
        AnthropicModelReasoning.GetShape("claude-haiku-4-6").Should().Be(AnthropicReasoningShape.Budget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // No "claude" marker at all: a gateway alias, or another vendor entirely.
    [InlineData("lab-llm")]
    [InlineData("gpt-4o")]
    // A family with no readable version.
    [InlineData("claude-sonnet")]
    // A release date is not a version. Reading 20251001 as a major version would classify this as
    // adaptive and send {"type":"adaptive"} to a model that rejects it.
    [InlineData("claude-sonnet-20251001")]
    [InlineData("claude-20240307-haiku")]
    // A version with no family.
    [InlineData("claude-5")]
    public void GetShape_NothingReadable_IsUnknown(string? modelId)
    {
        AnthropicModelReasoning.GetShape(modelId).Should().Be(AnthropicReasoningShape.Unknown);
    }

    [Fact]
    public void GetShape_VersionTokenIsLiteralZero_DoesNotMisreadTheNextTokenAsMajorVersion()
    {
        // A "not found yet" sentinel of 0 would collide with a version token that legitimately
        // parses to 0: majorVersion would stay 0 after reading "0", so the next token ("5") would
        // overwrite it instead of becoming the minor version, misclassifying this as Adaptive.
        AnthropicModelReasoning.GetShape("claude-opus-0-5").Should().Be(AnthropicReasoningShape.Budget);
    }

    /// <summary>
    /// Unknown is a pass-through, not a rejection. A gateway that renames models has to keep
    /// working, so an id this code cannot read constrains nothing.
    /// </summary>
    [Theory]
    [InlineData("lab-llm", AiThinkingModes.Adaptive)]
    [InlineData("lab-llm", AiThinkingModes.Budget)]
    [InlineData("claude-sonnet-20251001", AiThinkingModes.Adaptive)]
    [InlineData("claude-sonnet-20251001", AiThinkingModes.Budget)]
    public void IsCompatible_UnreadableModelId_AllowsEitherThinkingMode(string modelId, string thinking)
    {
        _reasoning.IsCompatible(modelId, thinking).Should().BeTrue();
    }

    [Theory]
    [InlineData("claude-sonnet-5", AiThinkingModes.Adaptive, true)]
    [InlineData("claude-sonnet-5", AiThinkingModes.Budget, false)]
    [InlineData("anthropic/claude-opus-5", AiThinkingModes.Budget, false)]
    [InlineData("claude-3-5-sonnet-20241022", AiThinkingModes.Adaptive, false)]
    [InlineData("claude-3-5-sonnet-20241022", AiThinkingModes.Budget, true)]
    // "none" sends no thinking parameter at all, so no model can reject it.
    [InlineData("claude-sonnet-5", AiThinkingModes.None, true)]
    [InlineData("claude-3-5-sonnet-20241022", AiThinkingModes.None, true)]
    public void IsCompatible_ReadableModelId_AnswersForTheFamily(string modelId, string thinking, bool expected)
    {
        _reasoning.IsCompatible(modelId, thinking).Should().Be(expected);
    }

    [Theory]
    [InlineData("claude-opus-4-6", AiThinkingModes.Adaptive)]
    [InlineData("claude-opus-4-6", AiThinkingModes.Budget)]
    [InlineData("claude-opus-4-6", AiThinkingModes.None)]
    [InlineData("claude-sonnet-4-6", AiThinkingModes.Adaptive)]
    [InlineData("claude-sonnet-4-6", AiThinkingModes.Budget)]
    public void IsCompatible_EitherShape_AcceptsAdaptiveAndBudget(string modelId, string thinking)
    {
        _reasoning.IsCompatible(modelId, thinking).Should().BeTrue();
    }

    [Theory]
    [InlineData("claude-opus-4-7", AiThinkingModes.Budget, false)]
    [InlineData("claude-opus-4-7", AiThinkingModes.Adaptive, true)]
    [InlineData("claude-opus-4-8", AiThinkingModes.Budget, false)]
    public void IsCompatible_OpusFourSevenAndLater_RejectsBudget(string modelId, string thinking, bool expected)
    {
        _reasoning.IsCompatible(modelId, thinking).Should().Be(expected);
    }
}
