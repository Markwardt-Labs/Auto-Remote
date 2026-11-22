namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Keeps display choices the user made while looking at a workspace on the device, so they come back after the app was closed.
/// </summary>
internal interface IViewPreferences
{
    /// <summary>
    /// Reads the markdown files of a workspace whose sections the user chose to number.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The paths of the files, relative to the workspace folder.</returns>
    Task<IReadOnlySet<string>> NumberedFiles(Guid workspaceId, CancellationToken cancellation);

    /// <summary>
    /// Remembers that the sections of a markdown file are numbered, or forgets it when they are not; not numbering is the default and is never stored.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="path">The file, relative to the workspace folder.</param>
    /// <param name="numbered">Whether the sections are numbered.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task SetNumbered(Guid workspaceId, string path, bool numbered, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ViewPreferences(string path) : IViewPreferences
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> NumberedFiles(Guid workspaceId, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            return (await Read(cancellation)).GetValueOrDefault(workspaceId.ToString("N"))?.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetNumbered(Guid workspaceId, string file, bool numbered, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            Dictionary<string, List<string>> all = await Read(cancellation);
            string key = workspaceId.ToString("N");
            List<string> files = all.GetValueOrDefault(key) ?? [];

            if (numbered ? files.Contains(file, StringComparer.Ordinal) : !files.Remove(file))
            {
                return;
            }

            if (numbered)
            {
                files.Add(file);
                all[key] = files;
            }
            else if (files.Count == 0)
            {
                all.Remove(key);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(all, options), cancellation);
            File.Move(temporary, path, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<string, List<string>>> Read(CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, List<string>>>(stream, options, cancellation) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
    }
}
