namespace Confast.Web.Features.Gages;

public sealed class GageType
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }

    public ICollection<Gage> Gages { get; } = [];
}
