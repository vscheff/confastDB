using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

public sealed class ShipDateCalculatorTests
{
    [Theory]
    [InlineData(2026, 8, 31, 2026, 9, 1)] // Monday
    [InlineData(2026, 9, 3, 2026, 9, 4)] // Thursday
    [InlineData(2026, 9, 4, 2026, 9, 7)] // Friday
    [InlineData(2026, 9, 5, 2026, 9, 7)] // Saturday
    [InlineData(2026, 9, 6, 2026, 9, 7)] // Sunday
    public void NextWorkDaySkipsTheWeekend(
        int year,
        int month,
        int day,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        var result = ShipDateCalculator.NextWorkDay(new DateOnly(year, month, day));

        Assert.Equal(new DateOnly(expectedYear, expectedMonth, expectedDay), result);
    }
}
