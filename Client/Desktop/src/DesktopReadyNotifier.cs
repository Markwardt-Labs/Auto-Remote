namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Shows a desktop notification saying a workspace is ready: a Windows toast through PowerShell on Windows, otherwise <c>notify-send</c>. Activating it, by clicking the notification, raises <see cref="Opened" />. Where neither tool is available the notification is dropped, and a notification server that has no actions can only show it.
/// </summary>
internal sealed class DesktopReadyNotifier : IReadyNotifier
{
    private readonly string toast = "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText01); $template.GetElementsByTagName('text').Item(0).AppendChild($template.CreateTextNode($env:AUTO_REMOTE_TOAST)) | Out-Null; $note = [Windows.UI.Notifications.ToastNotification]::new($template); Register-ObjectEvent -InputObject $note -EventName Activated -SourceIdentifier activated | Out-Null; Register-ObjectEvent -InputObject $note -EventName Dismissed -SourceIdentifier dismissed | Out-Null; Register-ObjectEvent -InputObject $note -EventName Failed -SourceIdentifier failed | Out-Null; [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show($note); $event = Wait-Event -Timeout 600; if ($event -and $event.SourceIdentifier -eq 'activated') { 'activated' }";

    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Raised with the workspace when the user activates its notification, from a background thread.
    /// </summary>
    public event Action<Guid>? Opened;

    /// <inheritdoc />
    public void Notify(Guid workspaceId, string workspace) => _ = Task.Run(() => Run(workspaceId, $"{workspace} is ready"));

    private async Task Run(Guid workspaceId, string text)
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", toast])
            : new ProcessStartInfo("notify-send", ["--app-name=Auto Remote", "--action=default=Open", $"--expire-time={(int)lifetime.TotalMilliseconds}", text]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        start.Environment["AUTO_REMOTE_TOAST"] = text;

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
            {
                return;
            }

            Task<string> errors = process.StandardError.ReadToEndAsync();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            await errors;

            if (output.Trim() is "default" or "activated")
            {
                Opened?.Invoke(workspaceId);
            }
        }
        catch (Win32Exception)
        {
        }
    }
}
