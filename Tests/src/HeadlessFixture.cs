namespace Markwardt.AutoRemote.Tests;

public sealed class HeadlessFixture : IDisposable
{
    private readonly HeadlessUnitTestSession headless = HeadlessUnitTestSession.StartNew(typeof(HeadlessFixture));

    public static Avalonia.AppBuilder BuildAvaloniaApp() => Avalonia.AppBuilder.Configure<ClientApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public Task<T> Run<T>(Func<T> action) => headless.Dispatch(action, CancellationToken.None);

    public Task<T> RunAsync<T>(Func<Task<T>> action) => headless.Dispatch(action, CancellationToken.None);

    public void Dispose() => headless.Dispose();
}
