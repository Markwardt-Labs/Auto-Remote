namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One way a loop can end by itself, as the settings of a loop list it.
/// </summary>
/// <param name="Condition">The condition.</param>
/// <param name="Name">The text shown for the choice.</param>
internal sealed record LoopConditionChoice(LoopCondition Condition, string Name);
