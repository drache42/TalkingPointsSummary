using System.Globalization;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Builds the AI prompt used to correct a drafted digest against the defects reported for it.
/// </summary>
/// <remarks>
/// The reviser is deliberately shown the draft and the defect list only, not the source news
/// items. Rebuilding the digest from the sources would produce a different digest rather than a
/// corrected one, and the defects already carry the corrections: the validator names the weekday
/// a date actually falls on, and the critic names the text to replace.
/// </remarks>
public sealed class SummaryRevisionPromptBuilder
{
    private const string TodayToken = "{{TODAY}}";
    private const string IssuesToken = "{{ISSUES}}";
    private const string UpcomingDatesToken = "{{UPCOMING_DATES}}";
    private const string DraftToken = "{{DRAFT}}";
    private const string LongDateFormat = "MMMM d, yyyy";

    /// <summary>
    /// Text substituted for an input the caller had nothing to supply for.
    /// </summary>
    public const string EmptyPlaceholder = "None";

    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    private readonly string _template;

    /// <summary>
    /// Initializes a prompt builder with the default revision template.
    /// </summary>
    public SummaryRevisionPromptBuilder()
        : this(DefaultTemplate.Value)
    {
    }

    /// <summary>
    /// Initializes a prompt builder with a custom template.
    /// </summary>
    /// <param name="template">Template text containing the supported tokens.</param>
    /// <exception cref="ArgumentException"><paramref name="template"/> is empty.</exception>
    public SummaryRevisionPromptBuilder(string template)
    {
        _template = string.IsNullOrWhiteSpace(template)
            ? throw new ArgumentException("Prompt template cannot be empty.", nameof(template))
            : template;
    }

    /// <summary>
    /// Builds the revision prompt for one draft digest.
    /// </summary>
    /// <param name="draftMarkdown">Draft digest markdown to correct.</param>
    /// <param name="issues">Rendered defect list the reviser must fix.</param>
    /// <param name="upcomingDates">
    /// Rendered upcoming dates section, or null when there is none.
    /// </param>
    /// <param name="nowLocal">Current local date, written into the prompt as today's date.</param>
    /// <returns>The prompt text with every supported token replaced.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="draftMarkdown"/> or <paramref name="issues"/> is null.
    /// </exception>
    public string Build(string draftMarkdown, string issues, string? upcomingDates, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(draftMarkdown);
        ArgumentNullException.ThrowIfNull(issues);

        var prompt = _template;
        prompt = prompt.Replace(
            TodayToken,
            nowLocal.ToString(LongDateFormat, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        prompt = prompt.Replace(IssuesToken, Placeholder(issues), StringComparison.Ordinal);
        prompt = prompt.Replace(UpcomingDatesToken, Placeholder(upcomingDates), StringComparison.Ordinal);
        prompt = prompt.Replace(DraftToken, draftMarkdown, StringComparison.Ordinal);
        return prompt;
    }

    private static string Placeholder(string? value)
        => string.IsNullOrWhiteSpace(value) ? EmptyPlaceholder : value.Trim();

    private static string LoadDefaultTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "SummaryRevisionPromptTemplate.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Summary revision prompt template not found at '{path}'.", path);

        return File.ReadAllText(path);
    }
}
