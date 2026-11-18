namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A button on the connect screen that connects to one server on this machine.
/// </summary>
/// <param name="Port">The port of the server.</param>
/// <param name="Text">The label of the button.</param>
/// <param name="Command">The command that connects.</param>
internal sealed record LocalServerChoice(int Port, string Text, AsyncCommand Command);
