namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A stored file was written by a newer release than this one, which cannot read it and must not overwrite it.
/// </summary>
/// <param name="message">What was found.</param>
internal sealed class NewerFileException(string message) : IOException(message);
