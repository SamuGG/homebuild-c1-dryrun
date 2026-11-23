namespace HomeBuild.Domain.Tests;

/// <summary>C10 Lab 6: working days on dates, in Europe/London, with weekends and configured bank holidays.</summary>
public sealed class WorkingCalendarTests
{
    private static readonly WorkingCalendar Calendar = new([new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 28), new DateOnly(2027, 3, 26), new DateOnly(2027, 3, 29)], WorkingCalendar.London);

    [Theory]
    [InlineData("2026-12-04", 2, "2026-12-08")] // Friday: Monday, Tuesday
    [InlineData("2026-12-05", 2, "2026-12-08")] // Saturday: Monday, Tuesday
    [InlineData("2026-12-07", 2, "2026-12-09")] // Monday: Tuesday, Wednesday
    [InlineData("2026-12-24", 2, "2026-12-30")] // Christmas Eve: 25th and 28th are bank holidays
    [InlineData("2027-03-25", 1, "2027-03-30")] // Easter 2027: Good Friday and Easter Monday
    [InlineData("2026-12-04", 0, "2026-12-04")]
    public void Adds_working_days_on_dates(string from, int days, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Calendar.AddWorkingDays(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), days));

    // The Break: reported Friday 27 March 2026 at 10:00 (GMT). The clocks go forward on Sunday 29 March, so Tuesday
    // 31 March 10:00 local is 09:00 UTC. Arithmetic on instants (4 × 24 hours) gives 10:00 UTC, 11:00 local: an hour late.
    [Fact]
    public void Two_working_days_across_the_march_clock_change_keep_the_local_time()
    {
        var reported = new DateTimeOffset(2026, 3, 27, 10, 0, 0, TimeSpan.Zero);

        var due = Calendar.AddWorkingDays(reported, 2);

        Assert.Equal(new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero), due);
        Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, WorkingCalendar.London).DateTime));
    }

    // And back in October: Friday 23 October 2026 10:00 BST (09:00 UTC) is due Tuesday 27 October 10:00 GMT (10:00 UTC).
    [Fact]
    public void Two_working_days_across_the_october_clock_change_keep_the_local_time() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero), Calendar.AddWorkingDays(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero), 2));

    // A local time that does not exist on the due day (01:30 on 29 March 2026) moves forward an hour.
    [Fact]
    public void A_local_time_in_the_spring_gap_moves_forward() =>
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), Calendar.At(new DateOnly(2026, 3, 29), new TimeOnly(1, 30)));

    [Fact]
    public void Today_is_the_london_date() =>
        Assert.Equal(new DateOnly(2026, 7, 2), Calendar.Today(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero))));
}
