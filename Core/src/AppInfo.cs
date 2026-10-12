namespace Markwardt.AutoRemote;

/// <summary>
/// Identifies the Auto Remote product and its build, shared by the server and the clients.
/// </summary>
public interface IAppInfo
{
    /// <summary>
    /// Gets the product display name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the product version in <c>major.minor.patch</c> form.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Creates <see cref="IAppInfo" /> instances.
    /// </summary>
    public interface IFactory
    {
        /// <summary>
        /// Creates product info for the given assembly's version.
        /// </summary>
        /// <param name="assembly">The assembly whose version is reported.</param>
        /// <returns>The product info.</returns>
        IAppInfo Create(Assembly assembly);
    }

    /// <inheritdoc />
    public sealed class Factory : IFactory
    {
        /// <inheritdoc />
        public IAppInfo Create(Assembly assembly) => new AppInfo(assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }
}

internal sealed class AppInfo(string version) : IAppInfo
{
    /// <inheritdoc />
    public string Name { get; } = "Auto Remote";

    /// <inheritdoc />
    public string Version { get; } = version;
}
