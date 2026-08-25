using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.Gages;

public sealed record GageTypeListItem(long Id, string Name, bool IsActive, uint Version);

public sealed record GageListItem(
    long Id,
    long GageTypeId,
    string GageTypeName,
    string GageNumber,
    bool IsActive,
    uint Version);

public sealed record GageTypeChoice(long Id, string Name, bool IsActive);

public sealed class GageTypeEditModel
{
    public long Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }
}

public sealed class GageEditModel
{
    public long Id { get; set; }

    [Range(1, long.MaxValue, ErrorMessage = "Gage type is required.")]
    public long GageTypeId { get; set; }

    [Required(ErrorMessage = "Gage number is required.")]
    public string GageNumber { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }
}

public enum SaveGageStatus
{
    Saved,
    NotFound,
    Duplicate,
    Conflict,
    ValidationFailed
}

public sealed record SaveGageResult(SaveGageStatus Status, string? Message = null);
