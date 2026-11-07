namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A JSON file the server stores in its data folder, with a version number in it so that a later release can change what the file holds and still read what an earlier one wrote.
/// </summary>
internal interface IStoredFile
{
    /// <summary>
    /// Gets the version this release writes: 1, the initial version, plus one for every upgrade step.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Reads the file and upgrades what an earlier release wrote: the upgraded content replaces the file, after the original was kept next to it as <c>name.vN.bak</c> (<c>N</c> being the version it had; a copy that is already there is not overwritten).
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The content at the current version, or <see langword="null" /> when the file does not exist.</returns>
    /// <exception cref="InvalidDataException">The file is not valid JSON or not a JSON object.</exception>
    /// <exception cref="NewerFileException">A newer release wrote the file.</exception>
    Task<JsonObject?> Read(string path, CancellationToken cancellation);

    /// <summary>
    /// Writes content at the current version, as a whole: the file is never left half written.
    /// </summary>
    /// <param name="path">The path of the file, whose folder is created when missing.</param>
    /// <param name="content">The content; the version is added to a copy of it.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>A task that completes once the file is written.</returns>
    Task Write(string path, JsonObject content, CancellationToken cancellation);
}

/// <inheritdoc />
/// <param name="steps">The upgrade steps in order. A file without a version is the initial version, 1, which is what the server stored before files were versioned, so step <c>i</c> turns version <c>i + 1</c> into <c>i + 2</c>. A step is only ever appended, never changed, since files of every version exist.</param>
/// <param name="isPrivate">Whether the file is readable by its owner only, for a file that can hold secrets.</param>
internal sealed class StoredFile(IReadOnlyList<Action<JsonObject>> steps, bool isPrivate = false) : IStoredFile
{
    private readonly string versionName = "version";

    private readonly int initial = 1;

    private readonly JsonSerializerOptions options = new() { WriteIndented = true };

    /// <inheritdoc />
    public int Version => initial + steps.Count;

    /// <inheritdoc />
    public async Task<JsonObject?> Read(string path, CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text = await File.ReadAllTextAsync(path, cancellation);
        JsonObject content;

        try
        {
            content = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"The file '{path}' does not hold a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The file '{path}' is not valid JSON: {exception.Message}", exception);
        }

        int found = content[versionName] is JsonValue value && value.TryGetValue(out int number) ? number : initial;

        if (found < initial)
        {
            throw new InvalidDataException($"The file '{path}' has the version {found}, which does not exist.");
        }

        if (found > Version)
        {
            throw new NewerFileException($"The file '{path}' was written by a newer release (version {found}, this one reads up to {Version}).");
        }

        if (found == Version)
        {
            return content;
        }

        for (int step = found - initial; step < steps.Count; step++)
        {
            steps[step](content);
        }

        string backup = $"{path}.v{found}.bak";

        if (!File.Exists(backup))
        {
            await File.WriteAllTextAsync(backup, text, cancellation);
        }

        JsonObject upgraded = Stamped(content);
        await Save(path, upgraded, cancellation);
        return upgraded;
    }

    /// <inheritdoc />
    public Task Write(string path, JsonObject content, CancellationToken cancellation) => Save(path, Stamped(content), cancellation);

    private JsonObject Stamped(JsonObject content)
    {
        JsonObject stamped = new() { [versionName] = Version };

        foreach ((string name, JsonNode? node) in content)
        {
            if (name != versionName)
            {
                stamped[name] = node?.DeepClone();
            }
        }

        return stamped;
    }

    private async Task Save(string path, JsonObject stamped, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, stamped.ToJsonString(options), cancellation);

        if (isPrivate && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, true);
    }
}
