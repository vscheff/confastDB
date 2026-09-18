namespace Confast.Web.Time;

/// <summary>
/// Holds a development-only local business-date override while preserving real
/// time for audit records, expiry, and other elapsed-time behavior.
/// </summary>
public sealed class DevelopmentDateOverrideTimeProvider
{
    private const int NoDateOverride = -1;
    private int overriddenDayNumber = NoDateOverride;

    public bool HasOverride => Volatile.Read(ref overriddenDayNumber) != NoDateOverride;

    public DateOnly CurrentDate
    {
        get
        {
            var dayNumber = Volatile.Read(ref overriddenDayNumber);
            return dayNumber == NoDateOverride
                ? DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime)
                : DateOnly.FromDayNumber(dayNumber);
        }
    }

    public void SetDate(DateOnly date) =>
        Volatile.Write(ref overriddenDayNumber, date.DayNumber);

    public void UseSystemDate() =>
        Volatile.Write(ref overriddenDayNumber, NoDateOverride);

}
