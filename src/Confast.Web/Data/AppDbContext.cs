using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
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
    }
}
