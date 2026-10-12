namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class HandshakeTests
{
    private static async Task<(HandshakeOutcome outcome, string reply)> Run(string sent, string key)
    {
        using DuplexStream stream = new(Encoding.UTF8.GetBytes(sent));
        HandshakeOutcome outcome = await new Handshake().Perform(stream, key, CancellationToken.None);
        return (outcome, Encoding.UTF8.GetString(stream.Written.ToArray()));
    }

    [Fact]
    public async Task Perform_AcceptsCorrectKey()
    {
        (HandshakeOutcome outcome, string reply) = await Run("{\"accessKey\":\"secret\"}\r\n", "secret");

        Assert.Equal(HandshakeOutcome.Accepted, outcome);
        Assert.Equal("{\"accepted\":true}\n", reply);
    }

    [Theory]
    [InlineData("{\"accessKey\":\"wrong\"}\n", true)]
    [InlineData("{\"accessKey\":\"secre\"}\n", true)]
    [InlineData("{\"accessKey\":\"secret\"}", false)]
    [InlineData("secret\n", false)]
    [InlineData("{}\n", false)]
    [InlineData("\n", false)]
    public async Task Perform_DeniesIncorrectOrIncompleteKeyAndSaysWhy(string sent, bool wrongKey)
    {
        (HandshakeOutcome outcome, string reply) = await Run(sent, "secret");

        Assert.Equal(wrongKey ? HandshakeOutcome.WrongKey : HandshakeOutcome.Invalid, outcome);
        Assert.Equal("{\"accepted\":false}\n", reply);
    }

    [Fact]
    public async Task Perform_DeniesOverlongLine()
    {
        (HandshakeOutcome outcome, _) = await Run("{\"accessKey\":\"" + new string('a', 5000) + "\"}\n", "secret");

        Assert.Equal(HandshakeOutcome.Invalid, outcome);
    }
}
