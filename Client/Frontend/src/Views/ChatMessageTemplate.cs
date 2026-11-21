namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Builds the view of a chat message from the template of its kind (the user's prompt, the work the AI did on the way, or what the AI said), so only the view that is shown is ever created: a template that held all three and hid two would render the formatted text of every message, user prompts and work included.
/// </summary>
public sealed class ChatMessageTemplate : IDataTemplate
{
    /// <summary>
    /// Gets or sets the template of a message the user wrote.
    /// </summary>
    public DataTemplate? User { get; set; }

    /// <summary>
    /// Gets or sets the template of a message that is work the AI did on the way, such as a tool call.
    /// </summary>
    public DataTemplate? Work { get; set; }

    /// <summary>
    /// Gets or sets the template of a message the AI said.
    /// </summary>
    public DataTemplate? Answer { get; set; }

    /// <inheritdoc />
    public bool Match(object? data) => data is ChatMessage;

    /// <inheritdoc />
    public Control? Build(object? param) => param is ChatMessage message ? TemplateOf(message)?.Build(param) : null;

    private DataTemplate? TemplateOf(ChatMessage message) => message switch
    {
        { Role: MessageRole.User } => User,
        { IsWork: true } => Work,
        _ => Answer
    };
}
