namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The ding as sound data, so every head plays the same bell-like tone.
/// </summary>
public sealed class ChimeSound
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChimeSound" /> class.
    /// </summary>
    public ChimeSound()
    {
        Pcm = Synthesize();
        Wav = Wrap(Pcm.Span);
    }

    /// <summary>
    /// Gets the samples per second.
    /// </summary>
    public int SampleRate { get; } = 22050;

    /// <summary>
    /// Gets the sound as mono 16-bit little-endian PCM.
    /// </summary>
    public ReadOnlyMemory<byte> Pcm { get; }

    /// <summary>
    /// Gets the sound as a complete WAV file.
    /// </summary>
    public ReadOnlyMemory<byte> Wav { get; }

    private ReadOnlyMemory<byte> Synthesize()
    {
        (double frequency, double level, double decay)[] partials = [(880, 1.0, 5.0), (1320, 0.45, 7.0), (2640, 0.2, 10.0)];
        byte[] pcm = new byte[SampleRate * 4 / 5 * 2];

        for (int index = 0; index < pcm.Length / 2; index++)
        {
            double time = (double)index / SampleRate;
            double sample = partials.Sum(partial => partial.level * Math.Exp(-partial.decay * time) * Math.Sin(2 * Math.PI * partial.frequency * time)) * Math.Min(1, time / 0.005) * 0.35;
            BitConverter.TryWriteBytes(pcm.AsSpan(index * 2), (short)(sample * short.MaxValue));
        }

        return pcm;
    }

    private ReadOnlyMemory<byte> Wrap(ReadOnlySpan<byte> pcm)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return stream.ToArray();
    }
}
