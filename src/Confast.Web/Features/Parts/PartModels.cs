using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.Parts;

public sealed record PartListItem(
    long Id,
    long CustomerId,
    string CustomerName,
    string PartNumber,
    string? Revision,
    string? Description);

public sealed record CustomerOption(long Id, string Name, bool IsActive)
{
    public string DisplayName => IsActive ? Name : $"{Name} (inactive)";
}

public sealed class PartEditModel
{
    public long Id { get; set; }

    [Range(1, long.MaxValue, ErrorMessage = "Customer is required.")]
    public long CustomerId { get; set; }

    [Required(ErrorMessage = "Part number is required.")]
    [Display(Name = "Part number")]
    public string PartNumber { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Revision { get; set; }

    public uint Version { get; set; }
}

public sealed record PartDeleteModel(
    long Id,
    string PartNumber,
    string CustomerName,
    uint Version);

public enum SavePartStatus
{
    Saved,
    NotFound,
    Conflict,
    DuplicatePartNumber,
    CustomerNotFound,
    ValidationFailed
}

public sealed record SavePartResult(
    SavePartStatus Status,
    long? Id = null,
    uint? Version = null,
    string? Message = null);

public enum DeletePartStatus
{
    Deleted,
    NotFound,
    Conflict,
    Blocked
}
