using Confast.Web.Features.Customers;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;

namespace Confast.Web.Features.Parts;

public sealed class Part
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public string PartNumber { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? SpecificationUsed { get; set; }

    public string? Revision { get; set; }

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }

    public ICollection<InspectionCriteriaRevision> InspectionCriteriaRevisions { get; } = [];

    public ICollection<Inspection> Inspections { get; } = [];

    public ICollection<PartPlant> PartPlants { get; } = [];
}

public sealed class PartPlant
{
    public long PartId { get; set; }

    public Part Part { get; set; } = null!;

    public long PlantId { get; set; }

    public Plant Plant { get; set; } = null!;
}
