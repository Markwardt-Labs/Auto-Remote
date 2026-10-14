namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Plays the ding that tells the user a workspace is ready again; each head implements it with its platform's audio.
/// </summary>
public interface IChime
{
    /// <summary>
    /// Plays the ding without waiting for it to finish; a platform that cannot play sound does nothing.
    /// </summary>
    void Play();
}
