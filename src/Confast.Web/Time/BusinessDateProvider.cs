namespace Confast.Web.Time;

public sealed class BusinessDateProvider(DevelopmentDateOverrideTimeProvider developmentDateOverride)
{
    public DateOnly Today => developmentDateOverride.CurrentDate;
}
