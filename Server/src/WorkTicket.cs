namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What became of a prompt given to the work queue.
/// </summary>
/// <param name="Queued">Whether the prompt waits for the local AI instead of having started.</param>
/// <param name="Started">Completes with <see langword="true" /> once the work started, or with <see langword="false" /> when it was removed from the queue or stopped while it started; fails with a <see cref="RequestException" /> saying why when it could not start. A waiting report that a prompt of the user joined counts as started, since it is part of that prompt from then on.</param>
internal sealed record WorkTicket(bool Queued, Task<bool> Started);
