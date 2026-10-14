using Android.Media;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Plays the ding through Android's audio output as a notification sound.
/// </summary>
/// <param name="sound">The ding to play.</param>
internal sealed class AndroidChime(ChimeSound sound) : IChime
{
    private readonly byte[] pcm = sound.Pcm.ToArray();

    /// <inheritdoc />
    public void Play() => _ = Task.Run(Run);

    private async Task Run()
    {
        using AudioTrack track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Notification)!.SetContentType(AudioContentType.Sonification)!.Build()!)
            .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(sound.SampleRate)!.SetChannelMask(ChannelOut.Mono)!.Build()!)
            .SetBufferSizeInBytes(pcm.Length)
            .SetTransferMode(AudioTrackMode.Static)
            .Build();
        track.Write(pcm, 0, pcm.Length);
        track.Play();
        await Task.Delay(TimeSpan.FromSeconds(1));
        track.Stop();
    }
}
