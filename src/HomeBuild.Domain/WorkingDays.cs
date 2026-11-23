namespace HomeBuild.Domain;

/// <summary>
/// C10 Lab 6: working days in the UK. Weekends and the configured bank holidays are not working days. Arithmetic is on
/// dates in the home's time zone (Europe/London), never on instants: "two working days after Friday 10:00" is Tuesday
/// 10:00 local, whatever the clocks did in between (the Break: 48 hours across the March change is 11:00).
/// </summary>
public sealed class WorkingCalendar
{
    private readonly HashSet<DateOnly> _bankHolidays;

    public WorkingCalendar(IEnumerable<DateOnly> bankHolidays, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(bankHolidays);
        ArgumentNullException.ThrowIfNull(zone);
        _bankHolidays = [.. bankHolidays];
        Zone = zone;
    }

    public static TimeZoneInfo London { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public TimeZoneInfo Zone { get; }

    public bool IsWorkingDay(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_bankHolidays.Contains(date);

    /// <summary>The date <paramref name="days"/> working days after <paramref name="from"/> (from itself never counts).</summary>
    public DateOnly AddWorkingDays(DateOnly from, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        var date = from;
        for (var counted = 0; counted < days;)
        {
            date = date.AddDays(1);
            if (IsWorkingDay(date))
            {
                counted++;
            }
        }

        return date;
    }

    /// <summary>
    /// The instant <paramref name="days"/> working days after <paramref name="from"/>: the same local time of day on the
    /// working day found by date arithmetic. A local time that does not exist that day (inside the spring gap) moves
    /// forward an hour.
    /// </summary>
    public DateTimeOffset AddWorkingDays(DateTimeOffset from, int days)
    {
        var local = TimeZoneInfo.ConvertTime(from, Zone);
        var date = AddWorkingDays(DateOnly.FromDateTime(local.DateTime), days);
        return At(date, TimeOnly.FromDateTime(local.DateTime));
    }

    /// <summary>The instant of a local date and time in this zone (an ambiguous autumn time is the first, summer, one).</summary>
    public DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>Today's date in this zone.</summary>
    public DateOnly Today(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);
    }
}
