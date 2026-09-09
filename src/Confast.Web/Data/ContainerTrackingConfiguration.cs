using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Data;

internal static class ContainerTrackingConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        var supplier = modelBuilder.Entity<Supplier>();
        supplier.ToTable("suppliers", t => t.HasCheckConstraint("CK_suppliers_name", "btrim(name) <> ''"));
        supplier.HasKey(x => x.Id);
        supplier.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        supplier.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        supplier.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        supplier.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        supplier.HasIndex(x => x.Name);

        var shipment = modelBuilder.Entity<Shipment>();
        shipment.ToTable("shipments", t => t.HasCheckConstraint("CK_shipments_freight_cost", "freight_cost >= 0"));
        shipment.HasKey(x => x.Id);
        shipment.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        shipment.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        shipment.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        shipment.Property(x => x.FreightCost).HasColumnName("freight_cost").HasPrecision(18, 2);

        var number = modelBuilder.Entity<ShipmentBillNumber>();
        number.ToTable("shipment_bill_numbers", t => t.HasCheckConstraint("CK_shipment_bill_numbers_number", "btrim(number) <> ''"));
        number.HasKey(x => x.Id);
        number.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        number.Property(x => x.ShipmentId).HasColumnName("shipment_id");
        number.Property(x => x.Number).HasColumnName("number").HasMaxLength(100).IsRequired();
        number.HasOne(x => x.Shipment).WithMany(x => x.BillNumbers).HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        number.HasIndex(x => x.Number);

        var container = modelBuilder.Entity<Container>();
        container.ToTable("containers", t =>
        {
            t.HasCheckConstraint("CK_containers_number", "btrim(container_number) <> ''");
            t.HasCheckConstraint("CK_containers_money", "quoted_rate >= 0 AND drayage_charge >= 0");
        });
        container.HasKey(x => x.Id);
        container.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        container.Property(x => x.ShipmentId).HasColumnName("shipment_id");
        container.Property(x => x.ContainerNumber).HasColumnName("container_number").HasMaxLength(100).IsRequired();
        container.Property(x => x.CbpNumber).HasColumnName("cbp_number").HasMaxLength(100);
        container.Property(x => x.ReceivedDate).HasColumnName("received_date");
        container.Property(x => x.ReceivedAtUtc).HasColumnName("received_at_utc");
        container.Property(x => x.ReceivedByUserId).HasColumnName("received_by_user_id").HasMaxLength(450);
        container.Property(x => x.QuotedRate).HasColumnName("quoted_rate").HasPrecision(18, 2);
        container.Property(x => x.DrayageCharge).HasColumnName("drayage_charge").HasPrecision(18, 2);
        container.Property(x => x.EstimatedDepartureDate).HasColumnName("estimated_departure_date");
        container.Property(x => x.EstimatedArrivalDate).HasColumnName("estimated_arrival_date");
        container.Property(x => x.AddedToProductionSchedule).HasColumnName("added_to_production_schedule");
        container.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        container.HasOne(x => x.Shipment).WithMany(x => x.Containers).HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        container.HasIndex(x => x.ContainerNumber);

        var bill = modelBuilder.Entity<BillOfLading>();
        bill.ToTable("bills_of_lading", t =>
        {
            t.HasCheckConstraint("CK_bills_of_lading_number", "number <> '' AND number = upper(btrim(number))");
            t.HasCheckConstraint("CK_bills_of_lading_duty", "duty >= 0");
        });
        bill.HasKey(x => x.Id);
        bill.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        bill.Property(x => x.Number).HasColumnName("number").HasMaxLength(100).IsRequired();
        bill.Property(x => x.SupplierId).HasColumnName("supplier_id");
        bill.Property(x => x.Duty).HasColumnName("duty").HasPrecision(18, 2);
        bill.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        bill.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_bills_of_lading_number");
        bill.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);

        var group = modelBuilder.Entity<ContainerGroup>();
        group.ToTable("container_groups", t => t.HasCheckConstraint("CK_container_groups_totals", "total_weight >= 0 AND pallet_count >= 0"));
        group.HasKey(x => x.Id);
        group.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        group.Property(x => x.ContainerId).HasColumnName("container_id");
        group.Property(x => x.BillOfLadingId).HasColumnName("bill_of_lading_id");
        group.Property(x => x.TotalWeight).HasColumnName("total_weight").HasPrecision(18, 3);
        group.Property(x => x.PalletCount).HasColumnName("pallet_count");
        group.Property(x => x.InvoiceNumber).HasColumnName("invoice_number").HasMaxLength(100);
        group.Property(x => x.CertificationsReceived).HasColumnName("certifications_received");
        group.HasOne(x => x.Container).WithMany(x => x.Groups).HasForeignKey(x => x.ContainerId).OnDelete(DeleteBehavior.Cascade);
        group.HasOne(x => x.BillOfLading).WithMany(x => x.Groups).HasForeignKey(x => x.BillOfLadingId).OnDelete(DeleteBehavior.Restrict);

        var line = modelBuilder.Entity<ContainerGroupPart>();
        line.ToTable("container_group_parts", t =>
        {
            t.HasCheckConstraint("CK_container_group_parts_po", "btrim(purchase_order_number) <> ''");
            t.HasCheckConstraint("CK_container_group_parts_quantity", "quantity >= 0");
            t.HasCheckConstraint("CK_container_group_parts_actual_received_quantity", "actual_received_quantity IS NULL OR actual_received_quantity >= 0");
        });
        line.HasKey(x => x.Id);
        line.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        line.Property(x => x.ContainerGroupId).HasColumnName("container_group_id");
        line.Property(x => x.PartId).HasColumnName("part_id");
        line.Property(x => x.PurchaseOrderNumber).HasColumnName("purchase_order_number").HasMaxLength(100).IsRequired();
        line.Property(x => x.Quantity).HasColumnName("quantity");
        line.Property(x => x.ActualReceivedQuantity).HasColumnName("actual_received_quantity");
        line.HasOne(x => x.ContainerGroup).WithMany(x => x.Parts).HasForeignKey(x => x.ContainerGroupId).OnDelete(DeleteBehavior.Cascade);
        line.HasOne(x => x.Part).WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        line.HasIndex(x => x.PurchaseOrderNumber);

        var receiptHistory = modelBuilder.Entity<ContainerReceiptHistory>();
        receiptHistory.ToTable("container_receipt_history");
        receiptHistory.HasKey(x => x.Id);
        receiptHistory.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        receiptHistory.Property(x => x.ContainerId).HasColumnName("container_id");
        receiptHistory.Property(x => x.PreviousReceivedDate).HasColumnName("previous_received_date");
        receiptHistory.Property(x => x.ReceivedDate).HasColumnName("received_date");
        receiptHistory.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000);
        receiptHistory.Property(x => x.ActingUserId).HasColumnName("acting_user_id").HasMaxLength(450).IsRequired();
        receiptHistory.Property(x => x.PerformedAtUtc).HasColumnName("performed_at_utc");
        receiptHistory.HasOne(x => x.Container).WithMany(x => x.ReceiptHistory).HasForeignKey(x => x.ContainerId).OnDelete(DeleteBehavior.Restrict);
        receiptHistory.HasIndex(x => x.ContainerId);

        var quantityHistory = modelBuilder.Entity<ContainerReceivedQuantityHistory>();
        quantityHistory.ToTable("container_received_quantity_history", t =>
            t.HasCheckConstraint("CK_container_received_quantity_history_nonnegative", "previous_quantity >= 0 AND new_quantity >= 0"));
        quantityHistory.HasKey(x => x.Id);
        quantityHistory.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        quantityHistory.Property(x => x.ContainerGroupPartId).HasColumnName("container_group_part_id");
        quantityHistory.Property(x => x.PreviousQuantity).HasColumnName("previous_quantity");
        quantityHistory.Property(x => x.NewQuantity).HasColumnName("new_quantity");
        quantityHistory.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        quantityHistory.Property(x => x.ActingUserId).HasColumnName("acting_user_id").HasMaxLength(450).IsRequired();
        quantityHistory.Property(x => x.PerformedAtUtc).HasColumnName("performed_at_utc");
        quantityHistory.HasOne(x => x.ContainerGroupPart).WithMany(x => x.ReceivedQuantityHistory).HasForeignKey(x => x.ContainerGroupPartId).OnDelete(DeleteBehavior.Restrict);
        quantityHistory.HasIndex(x => x.ContainerGroupPartId);

        var allocation = modelBuilder.Entity<ContainerReceiptAllocation>();
        allocation.ToTable("container_receipt_allocations", t =>
        {
            t.HasCheckConstraint("CK_container_receipt_allocations_positive_quantity", "quantity > 0");
            t.HasCheckConstraint("CK_container_receipt_allocations_manufacturer_lot", "btrim(manufacturer_lot_number) <> ''");
            t.HasCheckConstraint("CK_container_receipt_allocations_action", "action IN (1, 2)");
            t.HasCheckConstraint("CK_container_receipt_allocations_reversal", "(reversed_at_utc IS NULL AND reversed_by_user_id IS NULL AND reversal_reason IS NULL) OR (reversed_at_utc IS NOT NULL AND reversed_by_user_id IS NOT NULL AND btrim(reversal_reason) <> '')");
        });
        allocation.HasKey(x => x.Id);
        allocation.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        allocation.Property(x => x.OperationId).HasColumnName("operation_id");
        allocation.Property(x => x.ContainerGroupPartId).HasColumnName("container_group_part_id");
        allocation.Property(x => x.InspectionId).HasColumnName("inspection_id");
        allocation.Property(x => x.Quantity).HasColumnName("quantity");
        allocation.Property(x => x.ManufacturerLotNumber).HasColumnName("manufacturer_lot_number").HasMaxLength(200).IsRequired();
        allocation.Property(x => x.InternalLotNumber).HasColumnName("internal_lot_number").HasMaxLength(200);
        allocation.Property(x => x.Action).HasColumnName("action");
        allocation.Property(x => x.EstablishedDestinationManufacturerLot).HasColumnName("established_destination_manufacturer_lot");
        allocation.Property(x => x.ManufacturerLotEstablishmentReason).HasColumnName("manufacturer_lot_establishment_reason").HasMaxLength(1000);
        allocation.Property(x => x.ActingUserId).HasColumnName("acting_user_id").HasMaxLength(450).IsRequired();
        allocation.Property(x => x.PerformedAtUtc).HasColumnName("performed_at_utc");
        allocation.Property(x => x.ReversalReason).HasColumnName("reversal_reason").HasMaxLength(1000);
        allocation.Property(x => x.ReversedByUserId).HasColumnName("reversed_by_user_id").HasMaxLength(450);
        allocation.Property(x => x.ReversedAtUtc).HasColumnName("reversed_at_utc");
        allocation.HasOne(x => x.ContainerGroupPart).WithMany(x => x.ReceiptAllocations).HasForeignKey(x => x.ContainerGroupPartId).OnDelete(DeleteBehavior.Restrict);
        allocation.HasOne(x => x.Inspection).WithMany().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.SetNull);
        allocation.HasIndex(x => x.OperationId).IsUnique().HasDatabaseName("UX_container_receipt_allocations_operation_id");
        allocation.HasIndex(x => x.ContainerGroupPartId);
        allocation.HasIndex(x => x.InspectionId);
    }
}
