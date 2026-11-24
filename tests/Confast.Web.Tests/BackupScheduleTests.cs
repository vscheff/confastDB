using Confast.Web.Features.Backups;

namespace Confast.Web.Tests;

public sealed class BackupScheduleTests
{
    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);
    private static TimeZoneInfo Eastern => TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void HourlyCadenceKeepsItsPhaseAcrossDaysAndRestarts()
    {
        var rule = new BackupSchedule(BackupFrequency.Hourly, new(22, 30), Hours: 5, HourAnchor: new(2026, 11, 23));
        var expected = new[] { Utc(2026, 11, 23, 22, 30), Utc(2026, 11, 24, 3, 30), Utc(2026, 11, 24, 8, 30),
            Utc(2026, 11, 24, 13, 30), Utc(2026, 11, 24, 18, 30) };
        Assert.Equal(expected, rule.Preview(Utc(2026, 11, 23, 22), TimeZoneInfo.Utc));
        Assert.Equal(expected[1], rule.Previous(Utc(2026, 11, 24, 8), TimeZoneInfo.Utc));
        Assert.Equal(expected[2], rule.Next(expected[1], TimeZoneInfo.Utc));
    }

    [Fact]
    public void DailyScheduleUsesTheServerClockAcrossTheSpringGap()
    {
        var rule = new BackupSchedule(BackupFrequency.Daily, new(2, 30));
        Assert.Equal(new[] { Utc(2026, 3, 7, 7, 30), Utc(2026, 3, 8, 7), Utc(2026, 3, 9, 6, 30) },
            rule.Preview(Utc(2026, 3, 7), Eastern).Take(3));
    }

    [Fact]
    public void AmbiguousTimeRunsOnceAtTheEarlierInstant()
    {
        var rule = new BackupSchedule(BackupFrequency.Daily, new(1, 30));
        var first = Utc(2026, 11, 1, 5, 30);
        Assert.Equal(first, rule.Next(Utc(2026, 11, 1), Eastern));
        Assert.Equal(first, rule.Previous(Utc(2026, 11, 1, 6, 5), Eastern));
        Assert.Equal(Utc(2026, 11, 2, 6, 30), rule.Next(first, Eastern));
    }

    [Fact]
    public void WeeklyConversionRotatesAllSelectedDaysAcrossTheDateBoundary()
    {
        var Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var week = new BackupSchedule(BackupFrequency.Weekly, new(1, 0),
            Weekdays: (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Friday));
        var utc = week.InZone(Tokyo, TimeZoneInfo.Utc, Utc(2026, 11, 21));
        Assert.Equal(new TimeOnly(16, 0), utc.Time);
        Assert.Equal(1 | 1 << (int)DayOfWeek.Thursday, utc.Weekdays);
        Assert.Equal(week.Preview(Utc(2026, 11, 21), Tokyo), utc.Preview(Utc(2026, 11, 21), TimeZoneInfo.Utc));
    }

    [Fact]
    public void MonthlyConversionPreservesTheEndOfPreviousMonthRule()
    {
        var Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var local = new BackupSchedule(BackupFrequency.Monthly, new(2, 0), MonthDay: 1);
        var start = Utc(2026, 1, 1);
        var server = local.InZone(Tokyo, TimeZoneInfo.Utc, start);
        Assert.Equal(-1, server.MonthShift);
        Assert.Equal(new[] { Utc(2026, 1, 31, 17), Utc(2026, 2, 28, 17), Utc(2026, 3, 31, 17) },
            server.Preview(start, TimeZoneInfo.Utc).Take(3));
        Assert.Equal(local, server.InZone(TimeZoneInfo.Utc, Tokyo, start));
    }

    [Fact]
    public void DayThirtyOneClampsToFebruaryAndApril()
    {
        var rule = new BackupSchedule(BackupFrequency.Monthly, new(5, 0), MonthDay: 31);
        Assert.Equal(new[] { Utc(2028, 2, 29, 5), Utc(2028, 3, 31, 5), Utc(2028, 4, 30, 5) },
            rule.Preview(Utc(2028, 2, 1), TimeZoneInfo.Utc).Take(3));
    }

    [Fact]
    public void PreviewCommunicatesDifferentDaylightSavingTransitionDates()
    {
        var London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var server = new BackupSchedule(BackupFrequency.Weekly, new(9, 0), Weekdays: 1);
        var local = server.Preview(Utc(2026, 3, 1, 14), Eastern)
            .Select(x => TimeZoneInfo.ConvertTime(x, London).Hour).ToArray();
        Assert.Equal(new[] { 13, 13, 13, 14, 14 }, local);
    }

    [Fact]
    public void MissedSlotsConsolidateAndAdvanceThePersistableCursor()
    {
        var settings = new BackupSettings { Enabled = true, NextDueAtUtc = Utc(2026, 11, 22, 2) };
        Assert.Equal(Utc(2026, 11, 24, 2), BackupOccurrences.TakeDue(settings, Utc(2026, 11, 24, 11), TimeZoneInfo.Utc));
        Assert.Equal(Utc(2026, 11, 25, 2), settings.NextDueAtUtc);
        Assert.Null(BackupOccurrences.TakeDue(settings, Utc(2026, 11, 24, 11), TimeZoneInfo.Utc));
        settings.CatchUp = false;
        Assert.Null(BackupOccurrences.TakeDue(settings, Utc(2026, 11, 25, 3), TimeZoneInfo.Utc));
        Assert.Equal(Utc(2026, 11, 26, 2), settings.NextDueAtUtc);
        Assert.Equal(Utc(2026, 11, 26, 2),
            BackupOccurrences.TakeDue(settings, Utc(2026, 11, 26, 2).AddSeconds(15), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnUnrecognizedBrowserZoneFallsBackToTheServer()
    {
        Assert.Equal(Eastern.Id, BackupSchedule.BrowserZone("not/a/time-zone", Eastern, out var fallback).Id);
        Assert.True(fallback);
        Assert.Equal("Europe/London", BackupSchedule.BrowserZone("Europe/London", Eastern, out fallback).Id);
        Assert.False(fallback);
    }
}
