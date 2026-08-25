namespace Confast.Web.Features.Gages;

public sealed class Gage
{
    public long Id { get; set; }

    public long GageTypeId { get; set; }

    public GageType GageType { get; set; } = null!;

    public string GageNumber { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }
}
