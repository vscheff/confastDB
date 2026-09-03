using Confast.Web.Features.Parts;

namespace Confast.Web.Features.Suppliers;

public sealed class Supplier
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }

    public ICollection<Part> Parts { get; } = [];
}
