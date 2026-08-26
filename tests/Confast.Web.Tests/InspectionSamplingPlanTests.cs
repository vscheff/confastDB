using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

public sealed class InspectionSamplingPlanTests
{
    [Fact]
    public void NewInspectionDefaultsToToday()
    {
        Assert.Equal(
            DateOnly.FromDateTime(DateTime.Today),
            new CreateInspectionModel().InspectionDate);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(50, 5)]
    [InlineData(51, 7)]
    [InlineData(90, 7)]
    [InlineData(91, 11)]
    [InlineData(150, 11)]
    [InlineData(151, 13)]
    [InlineData(280, 13)]
    [InlineData(281, 16)]
    [InlineData(500, 16)]
    [InlineData(501, 19)]
    [InlineData(1200, 19)]
    [InlineData(1201, 23)]
    [InlineData(3200, 23)]
    [InlineData(3201, 29)]
    [InlineData(9999, 29)]
    [InlineData(10000, 29)]
    [InlineData(10001, 35)]
    [InlineData(35000, 35)]
    [InlineData(35001, 40)]
    public void QuantityReceivedUsesTheExpectedSampleSize(
        int quantityReceived,
        int expectedQuantityInspected)
    {
        Assert.Equal(
            expectedQuantityInspected,
            InspectionSamplingPlan.GetQuantityInspected(quantityReceived));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingOrInvalidQuantityReceivedHasNoSampleSize(int? quantityReceived)
    {
        Assert.Null(InspectionSamplingPlan.GetQuantityInspected(quantityReceived));
    }
}
