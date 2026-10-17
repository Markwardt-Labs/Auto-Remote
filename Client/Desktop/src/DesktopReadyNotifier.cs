namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Shows a desktop notification saying a workspace is ready: a Windows toast through PowerShell on Windows, otherwise <c>notify-send</c>. Where neither is available the notification is dropped.
/// </summary>
internal sealed class DesktopReadyNotifier : IReadyNotifier
{
    private readonly string toast = "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText01); $template.GetElementsByTagName('text').Item(0).AppendChild($template.CreateTextNode($env:AUTO_REMOTE_TOAST)) | Out-Null; [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show([Windows.UI.Notifications.ToastNotification]::new($template))";

    /// <inheritdoc />
    public void Notify(string workspace) => _ = Task.Run(() => Run($"{workspace} is ready"));

    private async Task Run(string text)
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", toast])
            : new ProcessStartInfo("notify-send", ["--app-name=Auto Remote", text]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        start.Environment["AUTO_REMOTE_TOAST"] = text;

        try
        {
            using Process? process = Process.Start(start);

            if (process is not null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (Win32Exception)
        {
        }
    }
}
