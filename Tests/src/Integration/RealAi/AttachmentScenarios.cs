namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class AttachmentScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, 2000, 1500, 400)]
    [InlineData(AiService.ClaudeCode, 3200, 2250, 200)]
    [InlineData(AiService.Codex, 2000, 1500, 400)]
    public async Task AnImageLargerThanTwoMegabytesIsUploadedAndSeenByTheAi(AiService service, int width, int height, int band)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("large", service);
        byte[] image = PngImage.Banded(width, height, band, 255, 128, 0);
        Assert.InRange(image.Length, 3 * 1024 * 1024, 20 * 1024 * 1024);

        string path = await server.Connection.UploadAttachment(workspace.Id, "large.png", image, CancellationToken.None);

        Assert.Equal(image.Length, new FileInfo(path).Length);
        Assert.Contains("orange", await server.Ask(workspace, $"What color is the solid band at the top of the attached image? Answer with one lowercase word.\nAttached file: {path}"), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiFact]
    public async Task AnAttachmentOverTheLimitIsRefusedAndTheConnectionStaysUsable()
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("toolarge", AiService.ClaudeCode);

        await Assert.ThrowsAsync<RemoteException>(() => server.Connection.UploadAttachment(workspace.Id, "huge.bin", new byte[20 * 1024 * 1024 + 1], CancellationToken.None));

        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
    }
}
