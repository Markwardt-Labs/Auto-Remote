namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One entry in a list the user picks a value from.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <param name="Value">The value the choice stands for.</param>
/// <param name="Name">The text shown for the choice.</param>
internal sealed record Choice<T>(T Value, string Name);
