using Android.Media;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Records speech through Android's <see cref="AudioRecord" />, asking the user for the microphone permission the first time.
/// </summary>
internal sealed class AndroidVoiceRecorder : IVoiceRecorder
{
    private AudioRecord? record;

    private MemoryStream? audio;

    private Task? reading;

    private bool recording;

    /// <inheritdoc />
    public int SampleRate => 16000;

    /// <inheritdoc />
    public async Task Start(CancellationToken cancellation)
    {
        if (record is not null)
        {
            throw new InvalidOperationException("Voice input is already recording.");
        }

        if (!await MainActivity.AskForMicrophone())
        {
            throw new InvalidOperationException("Voice input needs the microphone permission; allow it in the app's settings.");
        }

        int size = Math.Max(AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit), SampleRate) * 2;
        AudioRecord created = new(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, size);

        if (created.State != State.Initialized)
        {
            created.Release();
            created.Dispose();
            throw new InvalidOperationException("The microphone could not be used.");
        }

        created.StartRecording();
        record = created;
        audio = new MemoryStream();
        recording = true;
        reading = Task.Run(Read);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> Stop()
    {
        if (record is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        recording = false;
        await reading!;
        record.Stop();
        record.Release();
        record.Dispose();
        record = null;
        byte[] bytes = audio!.ToArray();
        audio.Dispose();
        audio = null;
        return bytes.AsMemory(0, bytes.Length & ~1);
    }

    private void Read()
    {
        byte[] buffer = new byte[4096];

        while (recording)
        {
            int read = record!.Read(buffer, 0, buffer.Length);

            if (read < 0)
            {
                break;
            }

            audio!.Write(buffer, 0, read);
        }
    }
}
