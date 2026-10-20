namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Records speech from the microphone for voice input; each head that can record implements it with its platform's way, and the recording is turned into text by the server.
/// </summary>
public interface IVoiceRecorder
{
    /// <summary>
    /// Gets the sample rate, in hertz, of the audio <see cref="Stop" /> returns.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Starts recording from the default microphone.
    /// </summary>
    /// <param name="cancellation">Cancels the start.</param>
    /// <returns>A task that completes once recording has begun.</returns>
    /// <exception cref="InvalidOperationException">Recording is already running, there is no microphone or the user did not allow it; the message says why and is shown to the user.</exception>
    Task Start(CancellationToken cancellation);

    /// <summary>
    /// Ends the recording and releases the microphone.
    /// </summary>
    /// <returns>The recorded audio as mono, 16 bit, little endian PCM at <see cref="SampleRate" />; empty if nothing was recorded.</returns>
    Task<ReadOnlyMemory<byte>> Stop();
}
