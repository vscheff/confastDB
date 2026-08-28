using Confast.Web.Features.Parts;
using Confast.Web.Features.InspectionCriteria;

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

    public ICollection<CustomerCertificationRecipient> CertificationRecipients { get; } = [];

    public ICollection<CustomerCertificationRequirement> CertificationRequirements { get; } = [];

    public CustomerCertificationSettings? CertificationSettings { get; set; }
}

public sealed class CustomerCertificationRecipient
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public string? Name { get; set; }

    public string EmailAddress { get; set; } = string.Empty;

    public CertificationRecipientType RecipientType { get; set; }

    public uint Version { get; set; }
}

public enum CertificationRecipientType
{
    To = 1,
    Cc = 2
}

public sealed class CustomerCertificationRequirement
{
    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public long CertificationTypeId { get; set; }

    public CertificationType CertificationType { get; set; } = null!;
}

public sealed class CustomerCertificationSettings
{
    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public string? FilenameTemplate { get; set; }

    public string? MultiLotFilenameTemplate { get; set; }

    public uint Version { get; set; }
}
