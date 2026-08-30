using Confast.Web.Features.Parts;
using Confast.Web.Features.InspectionCriteria;

namespace Confast.Web.Features.Customers;

public sealed class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public uint Version { get; set; }

    public ICollection<Part> Parts { get; } = [];

    public ICollection<Plant> Plants { get; } = [];

}

public sealed class Plant
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string? PlantCode { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }

    public uint Version { get; set; }

    public ICollection<PartPlant> PartPlants { get; } = [];

    public ICollection<PlantCertificationRecipient> CertificationRecipients { get; } = [];

    public ICollection<PlantCertificationRequirement> CertificationRequirements { get; } = [];

    public PlantCertificationSettings? CertificationSettings { get; set; }
}

public sealed class PlantCertificationRecipient
{
    public long Id { get; set; }

    public long PlantId { get; set; }

    public Plant Plant { get; set; } = null!;

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

public sealed class PlantCertificationRequirement
{
    public long PlantId { get; set; }

    public Plant Plant { get; set; } = null!;

    public long CertificationTypeId { get; set; }

    public CertificationType CertificationType { get; set; } = null!;
}

public sealed class PlantCertificationSettings
{
    public long PlantId { get; set; }

    public Plant Plant { get; set; } = null!;

    public string? FilenameTemplate { get; set; }

    public string? SinglePartMultiLotFilenameTemplate { get; set; }

    public string? MultiPartFilenameTemplate { get; set; }

    public uint Version { get; set; }
}
