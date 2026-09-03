using Confast.Web.Features.Parts;
using Confast.Web.Features.Suppliers;

namespace Confast.Web.Features.ContainerTracking;

public sealed class Shipment
{
    public long Id { get; set; }
    // Changing child bill rows must also advance the shipment's concurrency token.
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public uint Version { get; set; }
    public decimal? FreightCost { get; set; }
    public ICollection<ShipmentBillNumber> BillNumbers { get; } = [];
    public ICollection<Container> Containers { get; } = [];
}

public sealed class ShipmentBillNumber
{
    public long Id { get; set; }
    public long ShipmentId { get; set; }
    public Shipment Shipment { get; set; } = null!;
    public string Number { get; set; } = string.Empty;
}

public sealed class Container
{
    public long Id { get; set; }
    public long ShipmentId { get; set; }
    public Shipment Shipment { get; set; } = null!;
    public string ContainerNumber { get; set; } = string.Empty;
    public string? CbpNumber { get; set; }
    public DateOnly? ReceivedDate { get; set; }
    public decimal? QuotedRate { get; set; }
    public decimal? DrayageCharge { get; set; }
    public DateOnly? EstimatedDepartureDate { get; set; }
    public DateOnly? EstimatedArrivalDate { get; set; }
    public bool AddedToProductionSchedule { get; set; }
    public uint Version { get; set; }
    public ICollection<ContainerGroup> Groups { get; } = [];
}

public sealed class BillOfLading
{
    public long Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public long SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public decimal? Duty { get; set; }
    public uint Version { get; set; }
    public ICollection<ContainerGroup> Groups { get; } = [];
}

public sealed class ContainerGroup
{
    public long Id { get; set; }
    public long ContainerId { get; set; }
    public Container Container { get; set; } = null!;
    public long BillOfLadingId { get; set; }
    public BillOfLading BillOfLading { get; set; } = null!;
    public decimal? TotalWeight { get; set; }
    public int? PalletCount { get; set; }
    public string? InvoiceNumber { get; set; }
    public bool CertificationsReceived { get; set; }
    public ICollection<ContainerGroupPart> Parts { get; } = [];
}

public sealed class ContainerGroupPart
{
    public long Id { get; set; }
    public long ContainerGroupId { get; set; }
    public ContainerGroup ContainerGroup { get; set; } = null!;
    public long PartId { get; set; }
    public Part Part { get; set; } = null!;
    public string PurchaseOrderNumber { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public static class ContainerEditPolicy
{
    public static bool HasDeparted(DateOnly? estimatedDepartureDate, DateOnly today) =>
        estimatedDepartureDate is { } etd && today > etd;

    public static bool CanEditContents(DateOnly? estimatedDepartureDate, DateOnly today) =>
        !HasDeparted(estimatedDepartureDate, today);

    public static bool CanEditMetadata(DateOnly? estimatedDepartureDate, DateOnly today, bool isAdministrator) =>
        isAdministrator || CanEditContents(estimatedDepartureDate, today);
}
