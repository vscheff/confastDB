namespace Confast.Web.Features.Inspections;

public static class InspectionSamplingPlan
{
    public static int? GetQuantityInspected(int? quantityReceived) => quantityReceived switch
    {
        null or <= 0 => null,
        < 51 => 5,
        < 91 => 7,
        < 151 => 11,
        < 281 => 13,
        < 501 => 16,
        < 1201 => 19,
        < 3201 => 23,
        < 10001 => 29,
        < 35001 => 35,
        _ => 40
    };
}
