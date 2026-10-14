namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class MessagePartsTests
{
    [Fact]
    public void Parse_SplitsTheTypedTextFromTheTrailingAttachmentLines()
    {
        MessageParts parts = MessageParts.Parse("look at these\nAttached file: /data/attachments/ab/1a2b3c4d-shot one.png\nAttached file: C:\\data\\9f8e7d6c-notes.txt");

        Assert.Equal("look at these", parts.Body);
        Assert.Equal(["shot one.png", "notes.txt"], parts.Files.Select(file => file.Name));
        Assert.Equal("/data/attachments/ab/1a2b3c4d-shot one.png", parts.Files[0].Path);
    }

    [Fact]
    public void Parse_AllowsAMessageOfOnlyAttachments()
    {
        MessageParts parts = MessageParts.Parse("Attached file: /srv/a.png");

        Assert.Equal(string.Empty, parts.Body);
        Assert.Equal("a.png", Assert.Single(parts.Files).Name);
    }

    [Theory]
    [InlineData("plain message")]
    [InlineData("Attached file: x\nand then more text")]
    [InlineData("Attached file: ")]
    public void Parse_LeavesOtherTextAlone(string text)
    {
        MessageParts parts = MessageParts.Parse(text);

        Assert.Empty(parts.Files);
        Assert.Equal(text.TrimEnd(), parts.Body);
    }

    [Fact]
    public void Parse_KeepsANameThatOnlyLooksLikeAPrefix()
    {
        Assert.Equal("deadbeef-", Assert.Single(MessageParts.Parse("Attached file: /srv/deadbeef-").Files).Name);
    }
}
