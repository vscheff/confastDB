using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.Customers;

public sealed record CustomerListItem(long Id, string Name, bool IsActive);

public sealed class CustomerEditModel
{
    public long Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Address line 1")]
    public string? AddressLine1 { get; set; }

    [Display(Name = "Address line 2")]
    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    [Display(Name = "Postal code")]
    public string? PostalCode { get; set; }

    [Display(Name = "Active customer")]
    public bool IsActive { get; set; }

    public uint Version { get; set; }
}

public enum SaveCustomerStatus
{
    Saved,
    NotFound,
    Conflict
}

public sealed record SaveCustomerResult(SaveCustomerStatus Status, uint? Version = null);
