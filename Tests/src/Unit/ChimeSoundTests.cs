namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ChimeSoundTests
{
    private readonly ChimeSound sound = new();

    [Fact]
    public void Wav_IsAWellFormedMonoSixteenBitFileAroundThePcm()
    {
        ReadOnlySpan<byte> wav = sound.Wav.Span;

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav[..4]));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav[4..8]));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav[8..16]));
        Assert.Equal(sound.SampleRate, BitConverter.ToInt32(wav[24..28]));
        Assert.Equal("data", Encoding.ASCII.GetString(wav[36..40]));
        Assert.Equal(sound.Pcm.Length, BitConverter.ToInt32(wav[40..44]));
        Assert.True(wav[44..].SequenceEqual(sound.Pcm.Span));
    }

    [Fact]
    public void Pcm_RingsAndDiesAway()
    {
        byte[] pcm = sound.Pcm.ToArray();
        int Peak(int from, int to) => Enumerable.Range(from, to - from).Max(index => Math.Abs((int)BitConverter.ToInt16(pcm, index * 2)));

        Assert.True(Peak(0, 2000) > 5000);
        Assert.True(Peak(pcm.Length / 2 - 500, pcm.Length / 2) < 500);
    }
}
