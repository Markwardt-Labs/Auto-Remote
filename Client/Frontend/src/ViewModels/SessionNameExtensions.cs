namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Names a session after the work it is made for.
/// </summary>
internal static class SessionNameExtensions
{
    extension(string text)
    {
        /// <summary>
        /// Makes a session name from the first words of the first line of an input, leaving its attached files out, with a number behind it when the name is taken.
        /// </summary>
        /// <param name="taken">The names of the sessions there are.</param>
        /// <returns>The name: at most five words and 40 characters before any number.</returns>
        public string ToSessionName(IEnumerable<string> taken)
        {
            int words = 5;
            int length = 40;
            string line = MessageParts.Parse(text).Body.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? string.Empty;
            string cleaned = new([.. line.Select(character => char.IsControl(character) ? ' ' : character)]);
            string name = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(words));

            if (name.Length > length)
            {
                name = name[..length].TrimEnd();
            }

            if (name.Length == 0)
            {
                name = "Session";
            }

            HashSet<string> used = new(taken.Append("main"), StringComparer.OrdinalIgnoreCase);
            string wanted = name;

            for (int number = 2; used.Contains(wanted); number++)
            {
                wanted = $"{name} {number}";
            }

            return wanted;
        }
    }
}
