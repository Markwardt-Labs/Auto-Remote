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

    /// <summary>
    /// Gets the converter from the time of a message to the short text shown under it, in the local time zone.
    /// </summary>
    public static readonly IValueConverter Time = new FuncValueConverter<DateTimeOffset?, string>(time => time?.ToStamp(DateTimeOffset.UtcNow) ?? string.Empty);

    /// <summary>
    /// Gets the converter from the time of a message to the whole local date and time, which its stamp shows as a tooltip.
    /// </summary>
    public static readonly IValueConverter FullTime = new FuncValueConverter<DateTimeOffset?, string>(time => time?.ToLocalTime().ToString("F", CultureInfo.CurrentCulture) ?? string.Empty);

    /// <summary>
    /// Gets the converter from the time of a message to whether the message has one.
    /// </summary>
    public static readonly IValueConverter HasTime = new FuncValueConverter<DateTimeOffset?, bool>(time => time is not null);
}
