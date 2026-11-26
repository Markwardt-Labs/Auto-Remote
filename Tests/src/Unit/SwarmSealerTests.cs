namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SwarmSealerTests
{
    private readonly SwarmSealer sealer = new();

    private readonly SwarmIdentity sender = Make();

    private readonly SwarmIdentity receiver = Make();

    private static SwarmIdentity Make() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    private byte[] Seal(ISwarmIdentity? from = null) => sealer.Seal(from ?? sender, receiver.Public, Guid.NewGuid(), SwarmMessageKind.Task, "{\"secret\":1}"u8, "bundle bytes"u8, DateTimeOffset.UtcNow).ToArray();

    private SwarmPublicIdentity? Paired(string id) => id == sender.Public.Id ? sender.Public : null;

    [Fact]
    public void Open_GivesTheReceiverTheContentWhichTheSealedBytesDoNotShow()
    {
        byte[] data = Seal();

        SwarmMessage message = sealer.Open(receiver, Paired, data);

        Assert.Equal((SwarmMessageKind.Task, sender.Public.Id, receiver.Public.Id), (message.Kind, message.From, message.To));
        Assert.Equal("{\"secret\":1}", Encoding.UTF8.GetString(message.Body.Span));
        Assert.Equal("bundle bytes", Encoding.UTF8.GetString(message.Blob.Span));
        Assert.Equal(-1, data.AsSpan().IndexOf("secret"u8));
        Assert.Equal(-1, data.AsSpan().IndexOf("bundle bytes"u8));
    }

    [Fact]
    public void Open_RefusesAPackageWithAnyByteChanged()
    {
        byte[] data = Seal();

        for (int index = 0; index < data.Length; index += 7)
        {
            byte[] changed = [.. data];
            changed[index] ^= 1;

            Assert.Throws<InvalidDataException>(() => sealer.Open(receiver, Paired, changed));
        }

        Assert.Throws<InvalidDataException>(() => sealer.Open(receiver, Paired, data.AsSpan(0, data.Length - 1)));
    }

    [Fact]
    public void Open_RefusesAnotherReceiverAServerThatIsNotPairedAndASignatureOfSomeoneElse()
    {
        byte[] data = Seal();
        SwarmIdentity stranger = Make();

        Assert.Throws<InvalidDataException>(() => sealer.Open(stranger, Paired, data));
        Assert.Throws<InvalidDataException>(() => sealer.Open(receiver, _ => null, data));
        Assert.Throws<InvalidDataException>(() => sealer.Open(receiver, _ => stranger.Public, data));
        Assert.Throws<InvalidDataException>(() => sealer.Open(receiver, Paired, Seal(stranger)));
        Assert.Throws<InvalidDataException>(() => sealer.Peek("not a package"u8));
    }
}
