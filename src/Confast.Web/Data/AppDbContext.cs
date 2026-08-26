using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Part> Parts => Set<Part>();

    public DbSet<GageType> GageTypes => Set<GageType>();

    public DbSet<Gage> Gages => Set<Gage>();

    public DbSet<InspectionCriteriaRevision> InspectionCriteriaRevisions =>
        Set<InspectionCriteriaRevision>();

    public DbSet<InspectionCriterion> InspectionCriteria => Set<InspectionCriterion>();

    public DbSet<SecondaryProcessType> SecondaryProcessTypes => Set<SecondaryProcessType>();

    public DbSet<SecondaryProcessRequirement> SecondaryProcessRequirements =>
        Set<SecondaryProcessRequirement>();

    public DbSet<CertificationType> CertificationTypes => Set<CertificationType>();

    public DbSet<RevisionCertificationRequirement> RevisionCertificationRequirements =>
        Set<RevisionCertificationRequirement>();

    public DbSet<Inspection> Inspections => Set<Inspection>();

    public DbSet<InspectionResult> InspectionResults => Set<InspectionResult>();

    public DbSet<InspectionSecondaryProcess> InspectionSecondaryProcesses =>
        Set<InspectionSecondaryProcess>();

    public DbSet<InspectionCertificationRequirement> InspectionCertificationRequirements =>
        Set<InspectionCertificationRequirement>();

    public DbSet<InspectionCertification> InspectionCertifications =>
        Set<InspectionCertification>();

    public DbSet<CertificationDocument> CertificationDocuments => Set<CertificationDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();

        customer.ToTable("customers");
        customer.HasKey(x => x.Id);
        customer.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        customer.Property(x => x.Name)
            .HasColumnName("name")
            .IsRequired();
        customer.Property(x => x.AddressLine1).HasColumnName("address_line_1");
        customer.Property(x => x.AddressLine2).HasColumnName("address_line_2");
        customer.Property(x => x.City).HasColumnName("city");
        customer.Property(x => x.State).HasColumnName("state");
        customer.Property(x => x.PostalCode).HasColumnName("postal_code");
        customer.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);
        customer.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        var part = modelBuilder.Entity<Part>();

        part.ToTable("parts");
        part.HasKey(x => x.Id).HasName("PK_parts");
        part.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        part.Property(x => x.CustomerId).HasColumnName("customer_id");
        part.Property(x => x.PartNumber)
            .HasColumnName("part_number")
            .IsRequired();
        part.Property(x => x.Description).HasColumnName("description");
        part.Property(x => x.Revision).HasColumnName("revision");
        part.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        part.HasOne(x => x.Customer)
            .WithMany(x => x.Parts)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_parts_customers_customer_id");

        part.HasIndex(x => new { x.CustomerId, x.PartNumber })
            .IsUnique()
            .HasDatabaseName("UX_parts_customer_id_part_number");

        var gageType = modelBuilder.Entity<GageType>();
        gageType.ToTable("gage_types");
        gageType.HasKey(x => x.Id).HasName("PK_gage_types");
        gageType.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        gageType.Property(x => x.Name).HasColumnName("name").IsRequired();
        gageType.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        gageType.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        gageType.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_gage_types_name");

        var gage = modelBuilder.Entity<Gage>();
        gage.ToTable("gages");
        gage.HasKey(x => x.Id).HasName("PK_gages");
        gage.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        gage.Property(x => x.GageTypeId).HasColumnName("gage_type_id");
        gage.Property(x => x.GageNumber).HasColumnName("gage_number").IsRequired();
        gage.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        gage.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        gage.HasOne(x => x.GageType)
            .WithMany(x => x.Gages)
            .HasForeignKey(x => x.GageTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_gages_gage_types_gage_type_id");
        gage.HasIndex(x => x.GageTypeId).HasDatabaseName("IX_gages_gage_type_id");
        gage.HasIndex(x => x.GageNumber).IsUnique().HasDatabaseName("UX_gages_gage_number");

        var revision = modelBuilder.Entity<InspectionCriteriaRevision>();

        revision.ToTable("inspection_criteria_revisions", table =>
        {
            table.HasCheckConstraint(
                "CK_inspection_criteria_revisions_revision_number",
                "revision_number > 0");
            table.HasCheckConstraint(
                "CK_inspection_criteria_revisions_superseded_after_published",
                "superseded_at_utc IS NULL OR published_at_utc IS NOT NULL");
        });
        revision.HasKey(x => x.Id).HasName("PK_inspection_criteria_revisions");
        revision.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        revision.Property(x => x.PartId).HasColumnName("part_id");
        revision.Property(x => x.RevisionNumber).HasColumnName("revision_number");
        revision.Property(x => x.PrintRevisionNumber).HasColumnName("print_revision_number");
        revision.Property(x => x.PartDescription).HasColumnName("part_description");
        revision.Property(x => x.SpecificationUsed).HasColumnName("specification_used");
        revision.Property(x => x.Notes).HasColumnName("notes");
        revision.Property(x => x.MasterPrintFileName)
            .HasColumnName("master_print_file_name")
            .HasMaxLength(255);
        revision.Property(x => x.MasterPrintContent).HasColumnName("master_print_content");
        revision.Property(x => x.MasterPrintUploadedAtUtc)
            .HasColumnName("master_print_uploaded_at_utc");
        revision.Property(x => x.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        revision.Property(x => x.PublishedAtUtc).HasColumnName("published_at_utc");
        revision.Property(x => x.SupersededAtUtc).HasColumnName("superseded_at_utc");
        revision.Property(x => x.ChangeNote).HasColumnName("change_note");
        revision.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        revision.HasOne(x => x.Part)
            .WithMany(x => x.InspectionCriteriaRevisions)
            .HasForeignKey(x => x.PartId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_criteria_revisions_parts_part_id");

        revision.HasIndex(x => new { x.PartId, x.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("UX_inspection_criteria_revisions_part_id_revision_number");
        revision.HasAlternateKey(x => new { x.Id, x.PartId })
            .HasName("AK_inspection_criteria_revisions_id_part_id");
        revision.HasIndex(
                x => x.PartId,
                "IX_inspection_criteria_revisions_draft_guard")
            .IsUnique()
            .HasFilter("published_at_utc IS NULL")
            .HasDatabaseName("UX_inspection_criteria_revisions_one_draft_per_part");
        revision.HasIndex(
                x => x.PartId,
                "IX_inspection_criteria_revisions_current_guard")
            .IsUnique()
            .HasFilter("published_at_utc IS NOT NULL AND superseded_at_utc IS NULL")
            .HasDatabaseName("UX_inspection_criteria_revisions_one_current_per_part");

        var criterion = modelBuilder.Entity<InspectionCriterion>();

        criterion.ToTable("inspection_criteria", table =>
        {
            table.HasCheckConstraint(
                "CK_inspection_criteria_display_order",
                "display_order > 0");
            table.HasCheckConstraint(
                "CK_inspection_criteria_inspection_number",
                "inspection_number > 0");
        });
        criterion.HasKey(x => x.Id).HasName("PK_inspection_criteria");
        criterion.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        criterion.Property(x => x.InspectionCriteriaRevisionId)
            .HasColumnName("inspection_criteria_revision_id");
        criterion.Property(x => x.Name)
            .HasColumnName("name")
            .IsRequired();
        criterion.Property(x => x.InspectionNumber).HasColumnName("inspection_number");
        criterion.Property(x => x.GageTypeId).HasColumnName("gage_type_id");
        criterion.Property(x => x.InspectionMethod).HasColumnName("inspection_method");
        criterion.Property(x => x.Minimum).HasColumnName("minimum");
        criterion.Property(x => x.MaximumOrTolerance).HasColumnName("maximum_or_tolerance");
        criterion.Property(x => x.Unit).HasColumnName("unit");
        criterion.Property(x => x.DisplayOrder).HasColumnName("display_order");
        criterion.Property(x => x.Notes).HasColumnName("notes");
        criterion.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        criterion.HasOne(x => x.Revision)
            .WithMany(x => x.Criteria)
            .HasForeignKey(x => x.InspectionCriteriaRevisionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_criteria_revision_id");

        criterion.HasOne(x => x.GageType)
            .WithMany()
            .HasForeignKey(x => x.GageTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_criteria_gage_types_gage_type_id");

        criterion.HasIndex(x => x.GageTypeId)
            .HasDatabaseName("IX_inspection_criteria_gage_type_id");

        criterion.HasIndex(x => new { x.InspectionCriteriaRevisionId, x.DisplayOrder })
            .IsUnique()
            .HasDatabaseName("UX_inspection_criteria_revision_id_display_order");

        criterion.HasIndex(x => new { x.InspectionCriteriaRevisionId, x.InspectionNumber })
            .IsUnique()
            .HasDatabaseName("UX_inspection_criteria_revision_id_inspection_number");
        criterion.HasAlternateKey(x => new { x.Id, x.InspectionCriteriaRevisionId })
            .HasName("AK_inspection_criteria_id_revision_id");

        var secondaryProcessType = modelBuilder.Entity<SecondaryProcessType>();

        secondaryProcessType.ToTable("secondary_process_types", table =>
        {
            table.HasCheckConstraint(
                "CK_secondary_process_types_name_not_blank",
                "btrim(name) <> ''");
        });
        secondaryProcessType.HasKey(x => x.Id).HasName("PK_secondary_process_types");
        secondaryProcessType.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        secondaryProcessType.Property(x => x.Name)
            .HasColumnName("name")
            .IsRequired();
        secondaryProcessType.HasIndex(x => x.Name)
            .IsUnique()
            .HasDatabaseName("UX_secondary_process_types_name");
        secondaryProcessType.HasData(
            new SecondaryProcessType { Id = 1, Name = "Heat Treat" },
            new SecondaryProcessType { Id = 2, Name = "Clean" },
            new SecondaryProcessType { Id = 3, Name = "Patch" },
            new SecondaryProcessType { Id = 4, Name = "Plate" },
            new SecondaryProcessType { Id = 5, Name = "Sort" });

        var secondaryProcessRequirement = modelBuilder.Entity<SecondaryProcessRequirement>();

        secondaryProcessRequirement.ToTable("secondary_process_requirements");
        secondaryProcessRequirement.HasKey(x => x.Id)
            .HasName("PK_secondary_process_requirements");
        secondaryProcessRequirement.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        secondaryProcessRequirement.Property(x => x.InspectionCriteriaRevisionId)
            .HasColumnName("inspection_criteria_revision_id");
        secondaryProcessRequirement.Property(x => x.SecondaryProcessTypeId)
            .HasColumnName("secondary_process_type_id");
        secondaryProcessRequirement.Property(x => x.Specification)
            .HasColumnName("specification");
        secondaryProcessRequirement.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        secondaryProcessRequirement.HasOne(x => x.InspectionCriteriaRevision)
            .WithMany(x => x.SecondaryProcessRequirements)
            .HasForeignKey(x => x.InspectionCriteriaRevisionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_secondary_process_requirements_revision_id");
        secondaryProcessRequirement.HasOne(x => x.SecondaryProcessType)
            .WithMany()
            .HasForeignKey(x => x.SecondaryProcessTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_secondary_process_requirements_type_id");
        secondaryProcessRequirement.HasIndex(x => x.InspectionCriteriaRevisionId)
            .HasDatabaseName("IX_secondary_process_requirements_revision_id");
        secondaryProcessRequirement.HasIndex(x => x.SecondaryProcessTypeId)
            .HasDatabaseName("IX_secondary_process_requirements_type_id");
        secondaryProcessRequirement.HasAlternateKey(x => new
            {
                x.Id,
                x.InspectionCriteriaRevisionId
            })
            .HasName("AK_secondary_process_requirements_id_revision_id");

        var certificationType = modelBuilder.Entity<CertificationType>();

        certificationType.ToTable("certification_types", table =>
        {
            table.HasCheckConstraint(
                "CK_certification_types_name_not_blank",
                "btrim(name) <> ''");
            table.HasCheckConstraint(
                "CK_certification_types_display_order",
                "display_order > 0");
        });
        certificationType.HasKey(x => x.Id).HasName("PK_certification_types");
        certificationType.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        certificationType.Property(x => x.Name)
            .HasColumnName("name")
            .IsRequired();
        certificationType.Property(x => x.DisplayOrder).HasColumnName("display_order");
        certificationType.HasIndex(x => x.Name)
            .IsUnique()
            .HasDatabaseName("UX_certification_types_name");
        certificationType.HasIndex(x => x.DisplayOrder)
            .IsUnique()
            .HasDatabaseName("UX_certification_types_display_order");
        certificationType.HasData(
            new CertificationType { Id = 1, Name = "CBP", DisplayOrder = 1 },
            new CertificationType { Id = 2, Name = "Clean", DisplayOrder = 2 },
            new CertificationType { Id = 3, Name = "C of C", DisplayOrder = 3 },
            new CertificationType { Id = 4, Name = "Gall", DisplayOrder = 4 },
            new CertificationType { Id = 5, Name = "Hardness", DisplayOrder = 5 },
            new CertificationType { Id = 6, Name = "Heat", DisplayOrder = 6 },
            new CertificationType { Id = 7, Name = "Material", DisplayOrder = 7 },
            new CertificationType { Id = 8, Name = "Patch", DisplayOrder = 8 },
            new CertificationType { Id = 9, Name = "Plate", DisplayOrder = 9 },
            new CertificationType { Id = 10, Name = "Salt Spray", DisplayOrder = 10 },
            new CertificationType { Id = 11, Name = "SPC", DisplayOrder = 11 },
            new CertificationType { Id = 12, Name = "Supplier Inspection", DisplayOrder = 12 },
            new CertificationType { Id = 13, Name = "Tensile/Proof Load/Yield", DisplayOrder = 13 },
            new CertificationType { Id = 14, Name = "Torque", DisplayOrder = 14 },
            new CertificationType { Id = 15, Name = "Notes/Misc", DisplayOrder = 15 });

        var revisionCertificationRequirement =
            modelBuilder.Entity<RevisionCertificationRequirement>();

        revisionCertificationRequirement.ToTable("revision_certification_requirements", table =>
        {
            table.HasCheckConstraint(
                "CK_revision_certification_requirements_type_name_not_blank",
                "btrim(certification_type_name) <> ''");
            table.HasCheckConstraint(
                "CK_revision_certification_requirements_level",
                "requirement_level IN (1, 2)");
        });
        revisionCertificationRequirement.HasKey(x => x.Id)
            .HasName("PK_revision_certification_requirements");
        revisionCertificationRequirement.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        revisionCertificationRequirement.Property(x => x.InspectionCriteriaRevisionId)
            .HasColumnName("inspection_criteria_revision_id");
        revisionCertificationRequirement.Property(x => x.CertificationTypeId)
            .HasColumnName("certification_type_id");
        revisionCertificationRequirement.Property(x => x.CertificationTypeName)
            .HasColumnName("certification_type_name")
            .IsRequired();
        revisionCertificationRequirement.Property(x => x.RequirementLevel)
            .HasColumnName("requirement_level");
        revisionCertificationRequirement.Property(x => x.Notes).HasColumnName("notes");
        revisionCertificationRequirement.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();
        revisionCertificationRequirement.HasOne(x => x.InspectionCriteriaRevision)
            .WithMany(x => x.CertificationRequirements)
            .HasForeignKey(x => x.InspectionCriteriaRevisionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_revision_certification_requirements_revision_id");
        revisionCertificationRequirement.HasOne(x => x.CertificationType)
            .WithMany()
            .HasForeignKey(x => x.CertificationTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_revision_certification_requirements_type_id");
        revisionCertificationRequirement.HasIndex(x => x.CertificationTypeId)
            .HasDatabaseName("IX_revision_certification_requirements_type_id");
        revisionCertificationRequirement.HasIndex(x => new
            {
                x.InspectionCriteriaRevisionId,
                x.CertificationTypeId
            })
            .IsUnique()
            .HasDatabaseName("UX_revision_certification_requirements_revision_id_type_id");

        var inspection = modelBuilder.Entity<Inspection>();

        inspection.ToTable("inspections", table =>
        {
            table.HasCheckConstraint(
                "CK_inspections_quantity_inspected",
                "quantity_inspected IS NULL OR quantity_inspected > 0");
            table.HasCheckConstraint(
                "CK_inspections_quantity_received",
                "quantity_received IS NULL OR quantity_received > 0");
        });
        inspection.HasKey(x => x.Id).HasName("PK_inspections");
        inspection.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        inspection.Property(x => x.PartId).HasColumnName("part_id");
        inspection.Property(x => x.InspectionCriteriaRevisionId)
            .HasColumnName("inspection_criteria_revision_id");
        inspection.Property(x => x.LotNumber).HasColumnName("lot_number");
        inspection.Property(x => x.ConformancePoNumber).HasColumnName("conformance_po_number");
        inspection.Property(x => x.ManufacturerLotNumber)
            .HasColumnName("manufacturer_lot_number");
        inspection.Property(x => x.DateReceived).HasColumnName("date_received");
        inspection.Property(x => x.QuantityReceived).HasColumnName("quantity_received");
        inspection.Property(x => x.QuantityInspected).HasColumnName("quantity_inspected");
        inspection.Property(x => x.Inspector).HasColumnName("inspector");
        inspection.Property(x => x.InspectorNotes).HasColumnName("inspector_notes");
        inspection.Property(x => x.InHouseNotes).HasColumnName("in_house_notes");
        inspection.Property(x => x.InspectionDate).HasColumnName("inspection_date");
        inspection.Property(x => x.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        inspection.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        inspection.HasOne(x => x.Part)
            .WithMany(x => x.Inspections)
            .HasForeignKey(x => x.PartId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspections_parts_part_id");
        inspection.HasOne(x => x.InspectionCriteriaRevision)
            .WithMany(x => x.Inspections)
            .HasForeignKey(x => new { x.InspectionCriteriaRevisionId, x.PartId })
            .HasPrincipalKey(x => new { x.Id, x.PartId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspections_revision_id_part_id");
        inspection.HasAlternateKey(x => new { x.Id, x.InspectionCriteriaRevisionId })
            .HasName("AK_inspections_id_revision_id");
        inspection.HasIndex(x => x.PartId).HasDatabaseName("IX_inspections_part_id");
        inspection.HasIndex(x => x.InspectionCriteriaRevisionId)
            .HasDatabaseName("IX_inspections_revision_id");
        inspection.HasIndex(x => x.InspectionDate)
            .HasDatabaseName("IX_inspections_inspection_date");

        var inspectionResult = modelBuilder.Entity<InspectionResult>();

        inspectionResult.ToTable("inspection_results");
        inspectionResult.HasKey(x => x.Id).HasName("PK_inspection_results");
        inspectionResult.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        inspectionResult.Property(x => x.InspectionId).HasColumnName("inspection_id");
        inspectionResult.Property(x => x.InspectionCriteriaRevisionId)
            .HasColumnName("inspection_criteria_revision_id");
        inspectionResult.Property(x => x.InspectionCriterionId)
            .HasColumnName("inspection_criterion_id");
        inspectionResult.Property(x => x.GageId).HasColumnName("gage_id");
        inspectionResult.Property(x => x.GageNumber).HasColumnName("gage_number");
        inspectionResult.Property(x => x.ActualMin)
            .HasColumnName("actual_min");
        inspectionResult.Property(x => x.ActualMax)
            .HasColumnName("actual_max");
        inspectionResult.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        inspectionResult.HasOne(x => x.Inspection)
            .WithMany(x => x.Results)
            .HasForeignKey(x => new { x.InspectionId, x.InspectionCriteriaRevisionId })
            .HasPrincipalKey(x => new { x.Id, x.InspectionCriteriaRevisionId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_results_inspection_id_revision_id");
        inspectionResult.HasOne(x => x.InspectionCriterion)
            .WithMany(x => x.InspectionResults)
            .HasForeignKey(x => new { x.InspectionCriterionId, x.InspectionCriteriaRevisionId })
            .HasPrincipalKey(x => new { x.Id, x.InspectionCriteriaRevisionId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_results_criterion_id_revision_id");
        inspectionResult.HasOne(x => x.Gage)
            .WithMany()
            .HasForeignKey(x => x.GageId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_results_gages_gage_id");
        inspectionResult.HasIndex(x => x.GageId)
            .HasDatabaseName("IX_inspection_results_gage_id");
        inspectionResult.HasIndex(x => new { x.InspectionId, x.InspectionCriterionId })
            .IsUnique()
            .HasDatabaseName("UX_inspection_results_inspection_id_criterion_id");
        inspectionResult.HasIndex(x => new
            {
                x.InspectionCriterionId,
                x.InspectionCriteriaRevisionId
            })
            .HasDatabaseName("IX_inspection_results_criterion_id_revision_id");

        var inspectionSecondaryProcess = modelBuilder.Entity<InspectionSecondaryProcess>();

        inspectionSecondaryProcess.ToTable("inspection_secondary_processes", table =>
        {
            table.HasCheckConstraint(
                "CK_inspection_secondary_processes_process_name_not_blank",
                "btrim(process_name) <> ''");
        });
        inspectionSecondaryProcess.HasKey(x => x.Id)
            .HasName("PK_inspection_secondary_processes");
        inspectionSecondaryProcess.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        inspectionSecondaryProcess.Property(x => x.InspectionId)
            .HasColumnName("inspection_id");
        inspectionSecondaryProcess.Property(x => x.InspectionCriteriaRevisionId)
            .HasColumnName("inspection_criteria_revision_id");
        inspectionSecondaryProcess.Property(x => x.SecondaryProcessRequirementId)
            .HasColumnName("secondary_process_requirement_id");
        inspectionSecondaryProcess.Property(x => x.ProcessName)
            .HasColumnName("process_name")
            .IsRequired();
        inspectionSecondaryProcess.Property(x => x.Specification)
            .HasColumnName("specification");
        inspectionSecondaryProcess.Property(x => x.PurchaseOrderNumber)
            .HasColumnName("purchase_order_number");
        inspectionSecondaryProcess.Property(x => x.IsComplete)
            .HasColumnName("is_complete")
            .HasDefaultValue(false);
        inspectionSecondaryProcess.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        inspectionSecondaryProcess.HasOne(x => x.Inspection)
            .WithMany(x => x.SecondaryProcesses)
            .HasForeignKey(x => new { x.InspectionId, x.InspectionCriteriaRevisionId })
            .HasPrincipalKey(x => new { x.Id, x.InspectionCriteriaRevisionId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_secondary_processes_inspection_id_revision_id");
        inspectionSecondaryProcess.HasOne(x => x.SecondaryProcessRequirement)
            .WithMany(x => x.InspectionSecondaryProcesses)
            .HasForeignKey(x => new
                {
                    x.SecondaryProcessRequirementId,
                    x.InspectionCriteriaRevisionId
                })
            .HasPrincipalKey(x => new { x.Id, x.InspectionCriteriaRevisionId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_secondary_processes_requirement_id_revision_id");
        inspectionSecondaryProcess.HasIndex(x => new
            {
                x.InspectionId,
                x.SecondaryProcessRequirementId
            })
            .IsUnique()
            .HasDatabaseName("UX_inspection_secondary_processes_inspection_id_requirement_id");
        inspectionSecondaryProcess.HasIndex(x => new
            {
                x.SecondaryProcessRequirementId,
                x.InspectionCriteriaRevisionId
            })
            .HasDatabaseName("IX_inspection_secondary_processes_requirement_id_revision_id");

        var inspectionCertificationRequirement =
            modelBuilder.Entity<InspectionCertificationRequirement>();

        inspectionCertificationRequirement.ToTable("inspection_certification_requirements", table =>
        {
            table.HasCheckConstraint(
                "CK_inspection_certification_requirements_type_name_not_blank",
                "btrim(certification_type_name) <> ''");
            table.HasCheckConstraint(
                "CK_inspection_certification_requirements_level",
                "requirement_level IN (1, 2)");
        });
        inspectionCertificationRequirement.HasKey(x => x.Id)
            .HasName("PK_inspection_certification_requirements");
        inspectionCertificationRequirement.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        inspectionCertificationRequirement.Property(x => x.InspectionId)
            .HasColumnName("inspection_id");
        inspectionCertificationRequirement.Property(x => x.CertificationTypeId)
            .HasColumnName("certification_type_id");
        inspectionCertificationRequirement.Property(x => x.CertificationTypeName)
            .HasColumnName("certification_type_name")
            .IsRequired();
        inspectionCertificationRequirement.Property(x => x.RequirementLevel)
            .HasColumnName("requirement_level");
        inspectionCertificationRequirement.Property(x => x.Notes).HasColumnName("notes");
        inspectionCertificationRequirement.HasOne(x => x.Inspection)
            .WithMany(x => x.CertificationRequirements)
            .HasForeignKey(x => x.InspectionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_certification_requirements_inspection_id");
        inspectionCertificationRequirement.HasOne(x => x.CertificationType)
            .WithMany()
            .HasForeignKey(x => x.CertificationTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_certification_requirements_type_id");
        inspectionCertificationRequirement.HasIndex(x => x.CertificationTypeId)
            .HasDatabaseName("IX_inspection_certification_requirements_type_id");
        inspectionCertificationRequirement.HasIndex(x => new
            {
                x.InspectionId,
                x.CertificationTypeId
            })
            .IsUnique()
            .HasDatabaseName("UX_inspection_certification_requirements_inspection_id_type_id");

        var inspectionCertification = modelBuilder.Entity<InspectionCertification>();

        inspectionCertification.ToTable("inspection_certifications", table =>
        {
            table.HasCheckConstraint(
                "CK_inspection_certifications_type_name_not_blank",
                "btrim(certification_type_name) <> ''");
        });
        inspectionCertification.HasKey(x => x.Id).HasName("PK_inspection_certifications");
        inspectionCertification.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        inspectionCertification.Property(x => x.InspectionId).HasColumnName("inspection_id");
        inspectionCertification.Property(x => x.CertificationTypeId)
            .HasColumnName("certification_type_id");
        inspectionCertification.Property(x => x.CertificationTypeName)
            .HasColumnName("certification_type_name")
            .IsRequired();
        inspectionCertification.Property(x => x.Description).HasColumnName("description");
        inspectionCertification.Property(x => x.Notes).HasColumnName("notes");
        inspectionCertification.Property(x => x.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        inspectionCertification.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();
        inspectionCertification.HasOne(x => x.Inspection)
            .WithMany(x => x.Certifications)
            .HasForeignKey(x => x.InspectionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_certifications_inspection_id");
        inspectionCertification.HasOne(x => x.CertificationType)
            .WithMany()
            .HasForeignKey(x => x.CertificationTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_inspection_certifications_type_id");
        inspectionCertification.HasIndex(x => x.CertificationTypeId)
            .HasDatabaseName("IX_inspection_certifications_type_id");
        inspectionCertification.HasIndex(x => new
            {
                x.InspectionId,
                x.CertificationTypeId
            })
            .IsUnique()
            .HasDatabaseName("UX_inspection_certifications_inspection_id_type_id");

        var certificationDocument = modelBuilder.Entity<CertificationDocument>();

        certificationDocument.ToTable("certification_documents");
        certificationDocument.HasKey(x => x.Id).HasName("PK_certification_documents");
        certificationDocument.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();
        certificationDocument.Property(x => x.InspectionCertificationId)
            .HasColumnName("inspection_certification_id");
        certificationDocument.Property(x => x.OriginalFileName)
            .HasColumnName("original_file_name")
            .HasMaxLength(255)
            .IsRequired();
        certificationDocument.Property(x => x.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(100)
            .IsRequired();
        certificationDocument.Property(x => x.Content)
            .HasColumnName("content")
            .IsRequired();
        certificationDocument.Property(x => x.UploadedAtUtc)
            .HasColumnName("uploaded_at_utc")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        certificationDocument.Property(x => x.Version)
            .HasColumnName("xmin")
            .IsRowVersion();
        certificationDocument.HasOne(x => x.InspectionCertification)
            .WithMany(x => x.Documents)
            .HasForeignKey(x => x.InspectionCertificationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_certification_documents_certification_id");
        certificationDocument.HasIndex(x => x.InspectionCertificationId)
            .HasDatabaseName("IX_certification_documents_certification_id");
    }
}
