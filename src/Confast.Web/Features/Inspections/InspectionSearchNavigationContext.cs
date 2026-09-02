namespace Confast.Web.Features.Inspections;

// Scoped to the interactive Blazor circuit: a temporary, inspection-only found set.
// It deliberately has no database representation and is lost on a full browser refresh.
public sealed class InspectionSearchNavigationContext
{
    private List<InspectionListItem> results = [];

    public InspectionFindModel? Criteria { get; private set; }

    public IReadOnlyList<InspectionListItem> Results => results;

    public long? CurrentInspectionId { get; private set; }

    public bool IsActive => Criteria is not null;

    public string Summary => Criteria is null ? string.Empty : InspectionFindCriteriaFormatter.Describe(Criteria);

    public int CurrentIndex => CurrentInspectionId is long inspectionId
        ? results.FindIndex(x => x.Id == inspectionId)
        : -1;

    public void Replace(InspectionFindModel criteria, IReadOnlyList<InspectionListItem> matchingResults)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(matchingResults);

        Criteria = InspectionFindModel.Clone(criteria);
        results = matchingResults.ToList();
        CurrentInspectionId = null;
    }

    public void SetCurrentInspection(long inspectionId)
    {
        CurrentInspectionId = results.Any(x => x.Id == inspectionId) ? inspectionId : null;
    }

    public InspectionListItem? Previous => CurrentIndex > 0 ? results[CurrentIndex - 1] : null;

    public InspectionListItem? Next => CurrentIndex is var index && index >= 0 && index < results.Count - 1
        ? results[index + 1]
        : null;

    public long? RemoveUnavailableInspection(long inspectionId)
    {
        var index = results.FindIndex(x => x.Id == inspectionId);
        if (index < 0)
        {
            return null;
        }

        results.RemoveAt(index);
        if (CurrentInspectionId != inspectionId)
        {
            return null;
        }

        CurrentInspectionId = null;
        return results.ElementAtOrDefault(index)?.Id ?? results.ElementAtOrDefault(index - 1)?.Id;
    }

    public void Clear()
    {
        Criteria = null;
        results = [];
        CurrentInspectionId = null;
    }
}

public static class InspectionFindCriteriaFormatter
{
    public static string Describe(InspectionFindModel criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var terms = new List<string>();
        AddText(terms, "Part Number", criteria.PartNumber);
        AddText(terms, "Lot Number", criteria.LotNumber);
        AddText(terms, "Conformance PO#", criteria.ConformancePoNumber);
        AddText(terms, "Manufacturer's Lot#", criteria.ManufacturerLotNumber);
        AddValue(terms, "Date Received", criteria.DateReceived?.ToString("yyyy-MM-dd"));
        AddValue(terms, "Inspection Date", criteria.InspectionDate?.ToString("yyyy-MM-dd"));
        AddValue(terms, "Quantity Received", criteria.QuantityReceived?.ToString());
        AddValue(terms, "Quantity Inspected", criteria.QuantityInspected?.ToString());
        AddText(terms, "Inspector", criteria.Inspector);
        return terms.Count == 0 ? "All inspections" : string.Join(", ", terms);
    }

    private static void AddText(List<string> terms, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            terms.Add($"{label} = {value.Trim()}");
        }
    }

    private static void AddValue(List<string> terms, string label, string? value)
    {
        if (value is not null)
        {
            terms.Add($"{label} = {value}");
        }
    }
}
