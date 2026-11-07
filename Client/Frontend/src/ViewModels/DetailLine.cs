namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One line of what an AI session says about how it runs.
/// </summary>
/// <param name="Name">What the line is about.</param>
/// <param name="Value">What it is.</param>
internal sealed record DetailLine(string Name, string Value);
