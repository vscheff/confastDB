using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.ContainerTracking;

public sealed record TrackingSaveResult(bool Succeeded, string? Message = null, long? Id = null, uint? Version = null, long? ExistingBillOfLadingId = null)
{
    public static TrackingSaveResult Invalid(string message) => new(false, message);
    public static TrackingSaveResult Conflict() => Invalid("This record changed since you opened it. Reload before editing again.");
}

public sealed class ShipmentEditModel
{
    public long Id { get; set; }
    public uint Version { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? FreightCost { get; set; }
    public List<ShipmentBillEditModel> BillNumbers { get; set; } = [];
}

public sealed class ShipmentBillEditModel
{
    public long Id { get; set; }
    [Required, StringLength(100)]
    public string Number { get; set; } = string.Empty;
}

public sealed class ContainerEditModel
{
    public long Id { get; set; }
    public long ShipmentId { get; set; }
    public uint Version { get; set; }
    [Required, StringLength(100)]
    public string ContainerNumber { get; set; } = string.Empty;
    [StringLength(100)]
    public string? CbpNumber { get; set; }
    public DateOnly? ReceivedDate { get; set; }
    public bool ReceiptAuditRecorded { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? QuotedRate { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? DrayageCharge { get; set; }
    public DateOnly? EstimatedDepartureDate { get; set; }
    public DateOnly? EstimatedArrivalDate { get; set; }
    public bool AddedToProductionSchedule { get; set; }
}

public sealed class BillOfLadingEditModel
{
    public long Id { get; set; }
    public uint Version { get; set; }
    [Required, StringLength(100)]
    public string Number { get; set; } = string.Empty;
    [Range(1, long.MaxValue, ErrorMessage = "Select a supplier.")]
    public long SupplierId { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? Duty { get; set; }
}

public sealed class ContainerContentsEditModel
{
    public long ContainerId { get; set; }
    public uint Version { get; set; }
    public List<ContainerGroupEditModel> Groups { get; set; } = [];
}

public sealed class ContainerGroupEditModel
{
    public long Id { get; set; }
    [Range(1, long.MaxValue, ErrorMessage = "Select a B/L.")]
    public long BillOfLadingId { get; set; }
    [Range(typeof(decimal), "0", "999999999999999.999")]
    public decimal? TotalWeight { get; set; }
    [Range(0, int.MaxValue)]
    public int? PalletCount { get; set; }
    [StringLength(100)]
    public string? InvoiceNumber { get; set; }
    public bool CertificationsReceived { get; set; }
    public List<ContainerPartEditModel> Parts { get; set; } = [];
}

public sealed class ContainerPartEditModel
{
    public long Id { get; set; }
    [Range(1, long.MaxValue, ErrorMessage = "Select a Part.")]
    public long PartId { get; set; }
    [Required(ErrorMessage = "The PO Number field is required."), StringLength(100)]
    public string PurchaseOrderNumber { get; set; } = string.Empty;
    [Required, Range(0, int.MaxValue)]
    public int? Quantity { get; set; }
}

public sealed record TrackingChoice(long Id, long? SupplierId, string PartNumber, string CustomerName, bool IsActive)
{
    public string Label => PartNumber + " — " + CustomerName + (IsActive ? "" : " (inactive)");
}
public sealed record BillOfLadingChoice(long Id, long SupplierId, string Number, string SupplierName, decimal? Duty);
public sealed record ContainerSummary(long Id, uint Version, string Number, string? CbpNumber, DateOnly? Etd, DateOnly? Eta, DateOnly? ReceivedDate,
    bool ReceiptAuditRecorded, bool AddedToProductionSchedule, int GroupCount, int Pallets, decimal Weight, List<ContainerGroupSummary> Groups);
public sealed record ContainerGroupSummary(string SupplierName, string BillNumber, decimal? Duty, decimal? Weight,
    int? Pallets, string? InvoiceNumber, bool CertificationsReceived, List<ContainerPartSummary> Parts);
public sealed record ContainerPartSummary(string PartNumber, string CustomerName, string PurchaseOrderNumber, int Quantity);
public sealed record ShipmentSummary(long Id, uint Version, decimal? FreightCost, List<string> BillNumbers, List<ContainerSummary> Containers);
public sealed record ContainerDetail(ContainerEditModel Metadata, ContainerContentsEditModel Contents, List<string> ShipmentBills);

public enum ReceiptAllocationStatus
{
    Unassigned,
    PartiallyAssigned,
    FullyAssigned
}

public sealed record ReceivedPartAllocationItem(
    long Id,
    long? InspectionId,
    string ManufacturerLotNumber,
    string? InternalLotNumber,
    int Quantity,
    ReceiptAllocationAction Action,
    DateTimeOffset PerformedAtUtc,
    string ActingUserId,
    DateTimeOffset? ReversedAtUtc,
    string? ReversalReason,
    bool InspectionAccepted,
    bool InspectionCompleted);

public sealed record ReceivedPartLineItem(
    long Id,
    long PartId,
    string PartNumber,
    string CustomerName,
    string PurchaseOrderNumber,
    string SupplierName,
    string BillNumber,
    int ExpectedQuantity,
    int ActualReceivedQuantity,
    int AllocatedQuantity,
    int RemainingQuantity,
    ReceiptAllocationStatus AllocationStatus,
    bool HasMatchingInspectionCandidates,
    uint ContainerVersion,
    List<ReceivedPartAllocationItem> Allocations);

public sealed record ReceivedContainerItem(
    long Id,
    string ContainerNumber,
    DateOnly ReceivedDate,
    List<ReceivedPartLineItem> Lines);

public sealed record ReceiptInspectionCandidate(
    long InspectionId,
    string? InternalLotNumber,
    string? ManufacturerLotNumber,
    string PartNumber,
    string? PurchaseOrderNumber,
    int CurrentQuantity,
    bool Accepted,
    bool Completed,
    uint Version);

public sealed record ReceiptOperationResult(bool Succeeded, string? Message = null, long? InspectionId = null)
{
    public static ReceiptOperationResult Invalid(string message) => new(false, message);
    public static ReceiptOperationResult Conflict() => Invalid("This receipt changed while you were working. Reload and try again.");
}

public sealed class BeginReceiptInspectionModel
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public long ContainerGroupPartId { get; set; }
    public uint ContainerVersion { get; set; }
    [Required, StringLength(200)] public string ManufacturerLotNumber { get; set; } = string.Empty;
    [Required, StringLength(200)] public string InternalLotNumber { get; set; } = string.Empty;
    [Range(1, int.MaxValue)] public int Quantity { get; set; }
    public string? Inspector { get; set; }
}

public sealed class BumpUpReceiptModel
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public long ContainerGroupPartId { get; set; }
    public uint ContainerVersion { get; set; }
    public long InspectionId { get; set; }
    public uint InspectionVersion { get; set; }
    [Required, StringLength(200)] public string ManufacturerLotNumber { get; set; } = string.Empty;
    [Range(1, int.MaxValue)] public int Quantity { get; set; }
    public bool EstablishMissingManufacturerLot { get; set; }
    [StringLength(1000)] public string? EstablishmentReason { get; set; }
}

internal static class TrackingValidation
{
    public static string? Validate(object model)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(model, new ValidationContext(model), results, true)
            ? null : string.Join(" ", results.Select(x => x.ErrorMessage));
    }

    public static bool ValidMoney(decimal? amount) => amount is null ||
        (amount >= 0 && amount <= 9999999999999999.99m && decimal.Round(amount.Value, 2) == amount);
}
