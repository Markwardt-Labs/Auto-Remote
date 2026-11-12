namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class SettingsScenarios
{
    [RealAiFact]
    public async Task SettingsChangedWithoutAPromptAfterwardsSurviveAServerRestart()
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("persist", AiService.ClaudeCode, configure: false);
        WorkspaceSettings changed = (await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None)) with { Effort = AiEffort.Low, IsShellAllowed = true, IsSystemAccessAllowed = true, IsWebAllowed = false, IsImageGenerationAllowed = true };

        await server.Connection.SetWorkspaceSettings(workspace.Id, changed, CancellationToken.None);
        await server.Stop();
        await server.Launch();

        WorkspaceSettings after = await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        Assert.Equal((AiEffort.Low, true, true, false, true), (after.Effort, after.IsShellAllowed, after.IsSystemAccessAllowed, after.IsWebAllowed, after.IsImageGenerationAllowed));
    }

    [RealAiFact]
    public async Task SettingsChangedAreOnDiskAtOnceSoAKilledServerLosesNothing()
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("killed", AiService.ClaudeCode, configure: false);
        WorkspaceSettings changed = (await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None)) with { Effort = AiEffort.Low, IsShellAllowed = true, IsSystemAccessAllowed = true, IsWebAllowed = false, IsImageGenerationAllowed = true };

        await server.Connection.SetWorkspaceSettings(workspace.Id, changed, CancellationToken.None);
        await Task.Delay(500);
        await server.Stop(kill: true);
        await server.Launch();

        WorkspaceSettings after = await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
        Assert.Equal((AiEffort.Low, true, true, false, true), (after.Effort, after.IsShellAllowed, after.IsSystemAccessAllowed, after.IsWebAllowed, after.IsImageGenerationAllowed));
    }
}
