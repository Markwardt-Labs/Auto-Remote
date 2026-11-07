namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Reads and validates the server's JSON config file.
/// </summary>
internal interface IConfigLoader
{
    /// <summary>
    /// Loads the config file at the given path.
    /// </summary>
    /// <param name="path">The path of the JSON config file.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>The validated config.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not valid JSON, or a setting is missing or out of range.</exception>
    Task<ServerConfig> Load(string path, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ConfigLoader : IConfigLoader
{
    private readonly JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<ServerConfig> Load(string path, CancellationToken cancellation)
    {
        ServerConfig? config;

        try
        {
            await using FileStream stream = File.OpenRead(path);
            config = await JsonSerializer.DeserializeAsync<ServerConfig>(stream, options, cancellation);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Config file '{path}' is invalid: {exception.Message}", exception);
        }

        if (config is null)
        {
            throw new InvalidDataException($"Config file '{path}' is empty.");
        }

        if (config.Port is < 1 or > 65535)
        {
            throw new InvalidDataException("Config 'port' must be between 1 and 65535.");
        }

        if (string.IsNullOrEmpty(config.Key))
        {
            throw new InvalidDataException("Config 'key' must not be empty.");
        }

        if (!Directory.Exists(config.DefaultFolder))
        {
            throw new InvalidDataException($"Config 'defaultFolder' '{config.DefaultFolder}' is not an existing folder.");
        }

        return config;
    }
}
