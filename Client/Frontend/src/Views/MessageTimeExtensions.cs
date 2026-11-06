namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Writes the time of a chat message the way the conversation shows it.
/// </summary>
public static class MessageTimeExtensions
{
    extension(DateTimeOffset time)
    {
        /// <summary>
        /// Writes the time in the local time zone and the user's culture, as short as it can be without being unclear: the time of day alone for today, with <c>Yesterday</c> for yesterday, with the abbreviated month and the day for the rest of this year, and with the whole date for an earlier year.
        /// </summary>
        /// <param name="now">The current time.</param>
        /// <returns>The text.</returns>
        public string ToStamp(DateTimeOffset now)
        {
            DateTimeOffset local = time.ToLocalTime();
            DateTime today = now.ToLocalTime().Date;
            string clock = local.ToString("t", CultureInfo.CurrentCulture);

            if (local.Date == today)
            {
                return clock;
            }

            if (local.Date == today.AddDays(-1))
            {
                return $"Yesterday {clock}";
            }

            return local.Year == today.Year ? $"{local.ToString(CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal), CultureInfo.CurrentCulture)}, {clock}" : $"{local.ToString("d", CultureInfo.CurrentCulture)}, {clock}";
        }
    }
}
