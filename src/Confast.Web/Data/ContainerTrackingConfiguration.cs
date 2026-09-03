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
        });
        line.HasKey(x => x.Id);
        line.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        line.Property(x => x.ContainerGroupId).HasColumnName("container_group_id");
        line.Property(x => x.PartId).HasColumnName("part_id");
        line.Property(x => x.PurchaseOrderNumber).HasColumnName("purchase_order_number").HasMaxLength(100).IsRequired();
        line.Property(x => x.Quantity).HasColumnName("quantity");
        line.HasOne(x => x.ContainerGroup).WithMany(x => x.Parts).HasForeignKey(x => x.ContainerGroupId).OnDelete(DeleteBehavior.Cascade);
        line.HasOne(x => x.Part).WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        line.HasIndex(x => x.PurchaseOrderNumber);
    }
}
