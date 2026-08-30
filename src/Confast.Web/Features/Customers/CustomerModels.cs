using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.Customers;

public sealed record CustomerListItem(long Id, string Name, bool IsActive);

public sealed class CustomerEditModel
{
    public long Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    public string Name { get; set; } = string.Empty;

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

public sealed class PlantEditModel
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    [Required(ErrorMessage = "Plant name is required.")]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Plant code")]
    public string? PlantCode { get; set; }

    [Display(Name = "Address line 1")]
    public string? AddressLine1 { get; set; }

    [Display(Name = "Address line 2")]
    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    [Display(Name = "Postal code")]
    public string? PostalCode { get; set; }

    public uint Version { get; set; }
}

public enum PlantOperationStatus { Succeeded, NotFound, Conflict, DuplicateName, Blocked, ValidationFailed }

public sealed record PlantOperationResult(PlantOperationStatus Status, long? Id = null, uint? Version = null, string? Message = null);

public sealed class PlantCertificationRecipientEditModel
{
    public long Id { get; set; }

    public long PlantId { get; set; }

    [StringLength(200)]
    public string? Name { get; set; }

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(320)]
    public string EmailAddress { get; set; } = string.Empty;

    public CertificationRecipientType RecipientType { get; set; } = CertificationRecipientType.To;

    public uint Version { get; set; }
}

public sealed class PlantCertificationTypeChoice
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsRequired { get; set; }
}

public sealed class PlantCertificationDeliveryEditModel
{
    public long PlantId { get; set; }

    public List<PlantCertificationRecipientEditModel> Recipients { get; set; } = [];

    public List<PlantCertificationTypeChoice> CertificationTypes { get; set; } = [];

    public HashSet<long> OriginalRequiredCertificationTypeIds { get; set; } = [];

    public string? FilenameTemplate { get; set; }

    public string? SinglePartMultiLotFilenameTemplate { get; set; }

    public string? MultiPartFilenameTemplate { get; set; }

    public uint? SettingsVersion { get; set; }
}

public enum PlantCertificationOperationStatus
{
    Succeeded,
    NotFound,
    Conflict,
    ValidationFailed
}

public sealed record PlantCertificationOperationResult(
    PlantCertificationOperationStatus Status,
    long? RecipientId = null,
    uint? Version = null,
    string? Message = null);
