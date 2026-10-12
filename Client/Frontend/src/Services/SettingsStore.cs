namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Remembers the server the client last connected to.
/// </summary>
internal interface ISettingsStore
{
    /// <summary>
    /// Reads the saved connection settings.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The settings, or <see langword="null" /> if none were saved or the file is unreadable.</returns>
    Task<ConnectionSettings?> Load(CancellationToken cancellation);

    /// <summary>
    /// Saves the connection settings.
    /// </summary>
    /// <param name="settings">The settings to remember.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task Save(ConnectionSettings settings, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SettingsStore(string path) : ISettingsStore
{
    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <inheritdoc />
    public async Task<ConnectionSettings?> Load(CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ConnectionSettings>(stream, options, cancellation);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task Save(ConnectionSettings settings, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, options), cancellation);
    }
}
