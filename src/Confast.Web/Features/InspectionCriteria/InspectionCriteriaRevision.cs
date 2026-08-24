using Confast.Web.Features.Parts;

namespace Confast.Web.Features.InspectionCriteria;

public sealed class InspectionCriteriaRevision
{
    public long Id { get; set; }

    public long PartId { get; set; }

    public Part Part { get; set; } = null!;

    public int RevisionNumber { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public DateTimeOffset? SupersededAtUtc { get; set; }

    public string? ChangeNote { get; set; }

    public uint Version { get; set; }

    public ICollection<InspectionCriterion> Criteria { get; } = [];
}
