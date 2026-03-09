using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public sealed class MessageCategorizationPromptBuilder
{
    private const string FromNameToken = "{{FROM_NAME}}";
    private const string DateToken = "{{DATE_SENT}}";
    private const string MessageTextToken = "{{MESSAGE_TEXT}}";
    private const string MessageIdToken = "{{MESSAGE_ID}}";

    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    private readonly string _template;

    public MessageCategorizationPromptBuilder()
        : this(DefaultTemplate.Value)
    {
    }

    public MessageCategorizationPromptBuilder(string template)
    {
        _template = string.IsNullOrWhiteSpace(template)
            ? throw new ArgumentException("Prompt template cannot be empty.", nameof(template))
            : template;
    }

    public string Build(Message message)
    {
        var prompt = _template;
        prompt = prompt.Replace(FromNameToken, message.FromName, StringComparison.Ordinal);
        prompt = prompt.Replace(DateToken, message.SentAt.ToString("O"), StringComparison.Ordinal);
        prompt = prompt.Replace(MessageTextToken, message.MessageText, StringComparison.Ordinal);
        prompt = prompt.Replace(MessageIdToken, message.ExternalMessageId, StringComparison.Ordinal);
        return prompt;
    }

    private static string LoadDefaultTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "MessageCategorizationPromptTemplate.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Message categorization prompt template not found at '{path}'.", path);

        return File.ReadAllText(path);
    }
}