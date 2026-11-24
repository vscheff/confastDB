namespace Confast.Web.Features.Backups;

internal enum BackupFrequency { Hourly, Daily, Weekly, Monthly }

internal sealed record BackupSchedule(
    BackupFrequency Frequency,
    TimeOnly Time,
    int Hours = 1,
    int Weekdays = 62,
    int MonthDay = 1,
    int MonthShift = 0,
    DateOnly HourAnchor = default)
{
    private static readonly DateOnly DefaultAnchor = new(2000, 1, 1);

    internal void Validate()
    {
        if (!Enum.IsDefined(Frequency) || Hours is < 1 or > 168 || Weekdays is < 1 or > 127 ||
            MonthDay is < 1 or > 31 || MonthShift is < -4 or > 4 ||
            Time.Ticks % TimeSpan.TicksPerMinute != 0 ||
            (HourAnchor != default && (HourAnchor.Year < 1900 || HourAnchor.Year > 9900)))
            throw new BackupException("Schedule", "Enter a valid backup schedule.");
    }

    internal DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone) => Find(after, zone, 1).Utc;
    internal DateTimeOffset Previous(DateTimeOffset atOrBefore, TimeZoneInfo zone) => Find(atOrBefore, zone, -1).Utc;

    internal IReadOnlyList<DateTimeOffset> Preview(DateTimeOffset after, TimeZoneInfo zone)
    {
        var upcoming = new List<DateTimeOffset>(5);
        for (var i = 0; i < 5; i++)
        {
            after = Next(after, zone);
            upcoming.Add(after);
        }
        return upcoming;
    }

    internal BackupSchedule InZone(TimeZoneInfo source, TimeZoneInfo target, DateTimeOffset reference)
    {
        if (source.Equals(target)) return this;
        var (nominal, utc) = Find(reference, source, 1);
        var wall = TimeZoneInfo.ConvertTime(utc, target).DateTime;
        var shift = (wall.Date - nominal.Date).Days;
        var result = this with { Time = new TimeOnly(wall.Hour, wall.Minute) };
        return Frequency switch
        {
            BackupFrequency.Hourly => result with { HourAnchor = DateOnly.FromDateTime(wall) },
            BackupFrequency.Weekly => result with { Weekdays = RotateWeekdays(Weekdays, shift) },
            BackupFrequency.Monthly => result with { MonthShift = MonthShift + shift },
            _ => result
        };
    }

    internal static TimeZoneInfo BrowserZone(string? id, TimeZoneInfo server, out bool fallback)
    {
        if (id is { Length: > 0 and <= 128 } && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            fallback = false;
            return zone;
        }
        fallback = true;
        return server;
    }

    internal static DateTimeOffset ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        // Advance gaps to the first valid minute. During a fold use the earlier instant once.
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    private (DateTime Nominal, DateTimeOffset Utc) Find(DateTimeOffset instant, TimeZoneInfo zone, int direction)
    {
        // Search on both sides of the current wall date. A fold can place an earlier
        // occurrence later on the wall clock, and a gap can move a slot into the next day.
        var wall = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var pivot = wall.AddDays(-2 * direction);
        var anchor = (HourAnchor == default ? DefaultAnchor : HourAnchor).ToDateTime(Time);
        var start = Frequency switch
        {
            BackupFrequency.Hourly => anchor.AddHours(Math.Floor((pivot - anchor).TotalHours / Hours) * Hours),
            BackupFrequency.Monthly => new DateTime(pivot.Year, pivot.Month, 1).AddMonths(-direction),
            _ => pivot.Date
        };

        for (var position = start; ; position = Advance(position, direction))
        {
            if (Frequency == BackupFrequency.Weekly && (Weekdays & (1 << (int)position.DayOfWeek)) == 0)
                continue;
            var nominal = Frequency switch
            {
                BackupFrequency.Hourly => position,
                BackupFrequency.Monthly => position.AddDays(
                    Math.Min(MonthDay, DateTime.DaysInMonth(position.Year, position.Month)) - 1 + MonthShift).Add(Time.ToTimeSpan()),
                _ => position.Add(Time.ToTimeSpan())
            };
            var candidate = ToUtc(nominal, zone);
            if (direction > 0 ? candidate > instant : candidate <= instant) return (nominal, candidate);
        }
    }

    private DateTime Advance(DateTime position, int direction) => Frequency switch
    {
        BackupFrequency.Hourly => position.AddHours(Hours * direction),
        BackupFrequency.Monthly => position.AddMonths(direction),
        _ => position.AddDays(direction)
    };

    private static int RotateWeekdays(int mask, int shift)
    {
        shift = (shift + 7) % 7;
        return ((mask << shift) | (mask >> (7 - shift))) & 127;
    }
}

internal static class BackupOccurrences
{
    internal static readonly TimeSpan PollGrace = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    internal static DateTimeOffset? TakeDue(BackupSettings settings, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!settings.Enabled || settings.NextDueAtUtc is not { } due || due > now) return null;
        var latest = settings.Schedule.Previous(now, zone);
        settings.NextDueAtUtc = settings.Schedule.Next(now, zone);
        // If an occurrence was missed, one run represents the most recent due slot.
        return latest >= due && (settings.CatchUp || now - latest <= PollGrace) ? latest : null;
    }
}
