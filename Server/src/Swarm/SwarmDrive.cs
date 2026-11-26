namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Delivery by drive, for servers that are never on a network together: in an <c>auto-remote</c> folder of the drive, pairing files in <c>pairing</c> and packages in <c>mail/&lt;receiver&gt;/&lt;sender&gt;</c>, so one drive can go round several machines.
/// </summary>
internal interface ISwarmDrive
{
    /// <summary>
    /// Takes in everything on the drive addressed to this server and writes what waits for the air gapped servers.
    /// </summary>
    /// <param name="drive">The folder of the drive.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>What happened, to show.</returns>
    /// <exception cref="RequestException">The drive folder does not exist or cannot be written.</exception>
    Task<string> Sync(string drive, CancellationToken cancellation);

    /// <summary>
    /// Writes an invitation to pair.
    /// </summary>
    /// <param name="drive">The folder of the drive.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>A task that completes once written.</returns>
    /// <exception cref="RequestException">The drive folder does not exist or cannot be written.</exception>
    Task Invite(string drive, CancellationToken cancellation);

    /// <summary>
    /// Lists the pairings on the drive that wait to be confirmed.
    /// </summary>
    /// <param name="drive">The folder of the drive.</param>
    /// <returns>The pairings; none when the folder cannot be read.</returns>
    Task<IReadOnlyList<SwarmDrivePairing>> Pairings(string drive);

    /// <summary>
    /// Pairs with the server of a pairing on the drive, which writes the acceptance when it was an invitation.
    /// </summary>
    /// <param name="drive">The folder of the drive.</param>
    /// <param name="peerId">The server.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once paired.</returns>
    /// <exception cref="RequestException">No such pairing waits on the drive.</exception>
    Task Confirm(string drive, string peerId, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SwarmDrive(ISwarmBook book, ISwarmExchange exchange) : ISwarmDrive
{
    private readonly SemaphoreSlim syncing = new(1, 1);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly long maxPackage = 300L * 1024 * 1024;

    /// <inheritdoc />
    public async Task<string> Sync(string drive, CancellationToken cancellation)
    {
        string root = Root(drive);
        await syncing.WaitAsync(cancellation);

        try
        {
            List<string> problems = [];
            int imported = await Import(root, problems, cancellation);
            int exported = await Export(root, cancellation);
            string report = $"Read {imported} package{(imported == 1 ? string.Empty : "s")} from the drive and wrote {exported} to it.";
            return problems.Count > 0 ? $"{report} Problems: {string.Join(" ", problems.Distinct())}" : report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RequestException($"The drive could not be read or written: {exception.Message}");
        }
        finally
        {
            syncing.Release();
        }
    }

    /// <inheritdoc />
    public Task Invite(string drive, CancellationToken cancellation) => WriteInvitation(Root(drive), cancellation);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SwarmDrivePairing>> Pairings(string drive)
    {
        if (drive.Length == 0 || !Directory.Exists(drive))
        {
            return [];
        }

        (HashSet<string> paired, bool invited) = await book.Read(document => (document.Peers.Select(peer => peer.Identity.Id).ToHashSet(), document.InvitedAt is not null));
        string me = book.Identity.Public.Id;
        List<SwarmDrivePairing> pairings = [];

        foreach ((SwarmPairingFile file, bool acceptance, _) in await Files(Path.Combine(drive, "auto-remote")))
        {
            string id = file.Identity.Id;

            if (id != me && !paired.Contains(id) && (!acceptance || file.For == me && invited))
            {
                pairings.RemoveAll(other => other.Id == id);
                pairings.Add(new SwarmDrivePairing { Id = id, Name = file.Name, Fingerprint = file.Identity.Fingerprint, Kind = acceptance ? SwarmPairingKind.Acceptance : SwarmPairingKind.Invitation });
            }
        }

        return pairings;
    }

    /// <inheritdoc />
    public async Task Confirm(string drive, string peerId, CancellationToken cancellation)
    {
        string root = Root(drive);
        SwarmDrivePairing pairing = (await Pairings(drive)).FirstOrDefault(pairing => pairing.Id == peerId) ?? throw new RequestException("That pairing is not on the drive any more.");
        (SwarmPairingFile file, _, string path) = (await Files(root)).First(entry => entry.File.Identity.Id == peerId && entry.IsAcceptance == (pairing.Kind == SwarmPairingKind.Acceptance));
        await book.Pair(file.Identity, file.Name, SwarmMode.AirGapped, string.Empty);

        try
        {
            if (pairing.Kind == SwarmPairingKind.Invitation)
            {
                await Write(Path.Combine(root, "pairing", $"accept-{book.Identity.Public.Id}-for-{peerId}.json"), await Sign(peerId), cancellation);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RequestException($"The drive could not be written: {exception.Message}");
        }
    }

    private async Task WriteInvitation(string root, CancellationToken cancellation)
    {
        try
        {
            await Write(Path.Combine(root, "pairing", $"invite-{book.Identity.Public.Id}.json"), await Sign(string.Empty), cancellation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RequestException($"The drive could not be written: {exception.Message}");
        }

        await book.Change(document => document.InvitedAt = DateTimeOffset.UtcNow);
    }

    private async Task<SwarmPairingFile> Sign(string receiver)
    {
        SwarmPairingFile file = new() { Identity = book.Identity.Public, Name = await book.Read(document => document.Server.Name), For = receiver, CreatedAt = DateTimeOffset.UtcNow };
        return file with { Signature = Convert.ToBase64String(book.Identity.Sign(file.Signed.Span).Span) };
    }

    private async Task Write(string path, SwarmPairingFile file, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, options), cancellation);
    }

    private async Task<List<(SwarmPairingFile File, bool IsAcceptance, string Path)>> Files(string root)
    {
        List<(SwarmPairingFile, bool, string)> files = [];
        string folder = Path.Combine(root, "pairing");

        try
        {
            foreach (string path in Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [])
            {
                try
                {
                    if (new FileInfo(path).Length <= 16 * 1024 && JsonSerializer.Deserialize<SwarmPairingFile>(await File.ReadAllTextAsync(path), options) is { } file && file.Verify())
                    {
                        bool acceptance = file.For.Length > 0;

                        if (acceptance == Path.GetFileName(path).StartsWith("accept-", StringComparison.Ordinal))
                        {
                            files.Add((file, acceptance, path));
                        }
                    }
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return files;
    }

    private async Task<int> Import(string root, List<string> problems, CancellationToken cancellation)
    {
        string inbox = Path.Combine(root, "mail", book.Identity.Public.Id);

        if (!Directory.Exists(inbox))
        {
            return 0;
        }

        HashSet<string> paired = await book.Read(document => document.Peers.Select(peer => peer.Identity.Id).ToHashSet());
        int imported = 0;

        foreach (string folder in Directory.GetDirectories(inbox))
        {
            string sender = Path.GetFileName(folder);

            if (!paired.Contains(sender))
            {
                problems.Add("Packages from a server that is not paired were left on the drive.");
                continue;
            }

            foreach (string path in Directory.GetFiles(folder, "*.msg").Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();

                try
                {
                    if (new FileInfo(path).Length > maxPackage)
                    {
                        throw new InvalidDataException("A package on the drive is too large.");
                    }

                    await exchange.Receive(await File.ReadAllBytesAsync(path, cancellation), false);
                    File.Delete(path);
                    imported++;
                }
                catch (Exception exception) when (exception is InvalidDataException or RequestException)
                {
                    problems.Add(exception.Message);
                }
            }
        }

        return imported;
    }

    private async Task<int> Export(string root, CancellationToken cancellation)
    {
        List<string> peers = await book.Read(document => document.Peers.Where(peer => peer.Mode == SwarmMode.AirGapped).Select(peer => peer.Identity.Id).ToList());
        int exported = 0;

        foreach (string peer in peers)
        {
            string folder = Path.Combine(root, "mail", peer, book.Identity.Public.Id);
            Directory.CreateDirectory(folder);
            IReadOnlyList<SwarmParcel> mail = await exchange.Mail(peer, null);

            foreach (SwarmParcel parcel in mail)
            {
                string path = Path.Combine(folder, parcel.Name);
                await File.WriteAllBytesAsync(path + ".tmp", parcel.Data, cancellation);
                File.Move(path + ".tmp", path, true);
                exported++;
            }

            // What is no longer waiting arrived or was withdrawn.
            foreach (string path in Directory.GetFiles(folder, "*.msg").Where(path => mail.All(parcel => parcel.Name != Path.GetFileName(path))))
            {
                File.Delete(path);
            }
        }

        return exported;
    }

    private string Root(string drive)
    {
        if (drive.Length == 0)
        {
            throw new RequestException("Choose the folder of the drive first.");
        }

        return Directory.Exists(drive) ? Path.Combine(drive, "auto-remote") : throw new RequestException($"The drive folder '{drive}' does not exist on the machine of the server; plug in and mount the drive there.");
    }
}
