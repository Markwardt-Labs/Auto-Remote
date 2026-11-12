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

    /// <summary>
    /// Reads when the downloads of an update were completed, which is how a client that its own update restarted knows that its server is probably still restarting.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The time in UTC, or <see langword="null" /> if no update is waiting for its server.</returns>
    Task<DateTime?> LoadUpdateStart(CancellationToken cancellation);

    /// <summary>
    /// Remembers when the downloads of an update were completed, or forgets it.
    /// </summary>
    /// <param name="start">The time in UTC, or <see langword="null" /> once the server answers again or the wait is over.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task SaveUpdateStart(DateTime? start, CancellationToken cancellation);
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

    /// <inheritdoc />
    public async Task<DateTime?> LoadUpdateStart(CancellationToken cancellation)
    {
        string file = path + ".update";

        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return DateTime.TryParse(await File.ReadAllTextAsync(file, cancellation), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime start) ? start : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveUpdateStart(DateTime? start, CancellationToken cancellation)
    {
        string file = path + ".update";

        if (start is not { } time)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllTextAsync(file, time.ToString("O", CultureInfo.InvariantCulture), cancellation);
    }
}
