using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

public sealed class InspectionSearchNavigationContextTests
{
    [Fact]
    public void ResultSetKeepsDisplayedOrderAndNavigatesWithoutWrapping()
    {
        var context = new InspectionSearchNavigationContext();
        var first = Result(31, "FIRST");
        var second = Result(22, "SECOND");
        var third = Result(14, "THIRD");

        context.Replace(new InspectionFindModel { PartNumber = " ABC123 " }, [first, second, third]);
        context.SetCurrentInspection(second.Id);

        Assert.Equal("Part Number = ABC123", context.Summary);
        Assert.Equal(1, context.CurrentIndex);
        Assert.Equal(first.Id, context.Previous!.Id);
        Assert.Equal(third.Id, context.Next!.Id);

        context.SetCurrentInspection(first.Id);
        Assert.Equal(0, context.CurrentIndex);
        Assert.Null(context.Previous);
        Assert.Equal(second.Id, context.Next!.Id);

        context.SetCurrentInspection(third.Id);
        Assert.Equal(2, context.CurrentIndex);
        Assert.Equal(second.Id, context.Previous!.Id);
        Assert.Null(context.Next);
    }

    [Fact]
    public void ViewResultsStateSurvivesSaveAndCancelledSearchButNewSearchReplacesIt()
    {
        var context = new InspectionSearchNavigationContext();
        var originalCriteria = new InspectionFindModel { PartNumber = "ABC123" };
        var original = new[] { Result(1, "ONE"), Result(2, "TWO") };

        context.Replace(originalCriteria, original);
        context.SetCurrentInspection(2);

        // A save and a cancelled dialog do not modify the context; the page simply
        // keeps using this scoped object while the inspection is reloaded.
        Assert.Equal(1, context.CurrentIndex);
        Assert.Equal([1L, 2L], context.Results.Select(x => x.Id));
        Assert.Equal("ABC123", context.Criteria!.PartNumber);

        context.Replace(new InspectionFindModel { LotNumber = "LOT-9" }, [Result(9, "NINE")]);
        Assert.Equal("Lot Number = LOT-9", context.Summary);
        Assert.Equal([9L], context.Results.Select(x => x.Id));
        Assert.Equal(-1, context.CurrentIndex);
    }

    [Fact]
    public void MissingCurrentInspectionIsRemovedAndMovesToTheFollowingResult()
    {
        var context = new InspectionSearchNavigationContext();
        context.Replace(new InspectionFindModel(), [Result(1, "ONE"), Result(2, "TWO"), Result(3, "THREE")]);
        context.SetCurrentInspection(2);

        var replacement = context.RemoveUnavailableInspection(2);

        Assert.Equal(3, replacement);
        Assert.Equal([1L, 3L], context.Results.Select(x => x.Id));
        Assert.Equal(-1, context.CurrentIndex);

        context.SetCurrentInspection(replacement!.Value);
        Assert.Equal(1, context.CurrentIndex);
    }

    [Fact]
    public void ClearRemovesTheEntireFoundSet()
    {
        var context = new InspectionSearchNavigationContext();
        context.Replace(new InspectionFindModel { Inspector = "Pat" }, [Result(4, "FOUR")]);
        context.SetCurrentInspection(4);

        context.Clear();

        Assert.False(context.IsActive);
        Assert.Empty(context.Results);
        Assert.Null(context.CurrentInspectionId);
        Assert.Equal(-1, context.CurrentIndex);
    }

    private static InspectionListItem Result(long id, string lotNumber) => new(
        id,
        "ABC123",
        "Acme",
        1,
        lotNumber,
        new DateOnly(2026, 9, 2),
        DateTimeOffset.UnixEpoch,
        1,
        null,
        false,
        false);
}
