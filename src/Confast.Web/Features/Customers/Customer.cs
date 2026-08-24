using Confast.Web.Features.Parts;

namespace Confast.Web.Features.Customers;

public sealed class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }

    public ICollection<Part> Parts { get; } = [];
}
