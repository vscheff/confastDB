using Confast.Web.Features.Customers;
using Confast.Web.Features.InspectionCriteria;

namespace Confast.Web.Features.Parts;

public sealed class Part
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public string PartNumber { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Revision { get; set; }

    public uint Version { get; set; }

    public ICollection<InspectionCriteriaRevision> InspectionCriteriaRevisions { get; } = [];
}
