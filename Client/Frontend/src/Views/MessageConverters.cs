namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Converters that show a user message as its typed text and its attached files.
/// </summary>
public static class MessageConverters
{
    /// <summary>
    /// Gets the converter from a stored prompt to the text the user typed.
    /// </summary>
    public static readonly IValueConverter Body = new FuncValueConverter<string?, string>(text => MessageParts.Parse(text ?? string.Empty).Body);

    /// <summary>
    /// Gets the converter from a stored prompt to whether the user typed any text.
    /// </summary>
    public static readonly IValueConverter HasBody = new FuncValueConverter<string?, bool>(text => MessageParts.Parse(text ?? string.Empty).Body.Length > 0);

    /// <summary>
    /// Gets the converter from a stored prompt to its attached files.
    /// </summary>
    public static readonly IValueConverter Files = new FuncValueConverter<string?, IEnumerable<object>>(text => MessageParts.Parse(text ?? string.Empty).Files);

    /// <summary>
    /// Gets the converter from a stored prompt to whether it has attached files.
    /// </summary>
    public static readonly IValueConverter HasFiles = new FuncValueConverter<string?, bool>(text => MessageParts.Parse(text ?? string.Empty).Files.Count > 0);
}
