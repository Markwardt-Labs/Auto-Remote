namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Decides which accounts of this machine may ask the local channel for the access key.
/// </summary>
internal interface ILocalAccess
{
    /// <summary>
    /// Gets a value indicating whether the local channel is open at all, which it is not when the config lists no accounts.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets whether the account with a user id may ask for the key.
    /// </summary>
    /// <param name="uid">The numeric user id of the connecting process.</param>
    /// <returns><see langword="true" /> if the account is allowed.</returns>
    bool Allows(uint uid);

    /// <summary>
    /// Creates <see cref="ILocalAccess" /> instances.
    /// </summary>
    public interface IFactory
    {
        /// <summary>
        /// Creates the rule for a config.
        /// </summary>
        /// <param name="users">The accounts the config allows: <see langword="null" /> for every regular user account, an empty list for none, otherwise exactly the named accounts.</param>
        /// <returns>The rule.</returns>
        ILocalAccess Create(IReadOnlyList<string>? users);
    }

    /// <inheritdoc />
    public sealed class Factory(string passwd = "/etc/passwd", string logins = "/etc/login.defs") : IFactory
    {
        /// <inheritdoc />
        public ILocalAccess Create(IReadOnlyList<string>? users)
        {
            Dictionary<string, uint> accounts = [];

            if (File.Exists(passwd))
            {
                foreach (string line in File.ReadLines(passwd))
                {
                    string[] fields = line.Split(':');

                    if (fields.Length > 2 && uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid))
                    {
                        accounts.TryAdd(fields[0], uid);
                    }
                }
            }

            uint minimum = 1000;

            if (File.Exists(logins))
            {
                foreach (string line in File.ReadLines(logins))
                {
                    string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

                    if (fields is ["UID_MIN", { } value, ..] && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
                    {
                        minimum = parsed;
                    }
                }
            }

            return new LocalAccess(users, name => accounts.TryGetValue(name, out uint uid) ? uid : null, minimum);
        }
    }
}

/// <inheritdoc />
internal sealed class LocalAccess : ILocalAccess
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LocalAccess" /> class.
    /// </summary>
    /// <param name="users">The accounts the config allows: <see langword="null" /> for every regular user account, an empty list for none, otherwise exactly the named accounts.</param>
    /// <param name="lookup">Finds the user id of an account by its name, or <see langword="null" /> when there is no such account.</param>
    /// <param name="minimum">The lowest user id of a regular account; the ids below it belong to the system and its services.</param>
    public LocalAccess(IReadOnlyList<string>? users, Func<string, uint?> lookup, uint minimum)
    {
        this.users = users;
        this.minimum = minimum;

        foreach (string name in users ?? [])
        {
            if (lookup(name.Trim()) is { } uid)
            {
                allowed.Add(uid);
            }
        }
    }

    private readonly IReadOnlyList<string>? users;

    private readonly uint minimum;

    private readonly uint nobody = 65534;

    private readonly HashSet<uint> allowed = [];

    /// <inheritdoc />
    public bool IsEnabled => users is null || users.Count > 0;

    /// <inheritdoc />
    public bool Allows(uint uid) => users is null ? uid >= minimum && uid != nobody && uid != uint.MaxValue - 1 : allowed.Contains(uid);
}
