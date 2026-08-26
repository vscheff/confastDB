using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionCertifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certification_types",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certification_types", x => x.id);
                    table.CheckConstraint("CK_certification_types_display_order", "display_order > 0");
                    table.CheckConstraint("CK_certification_types_name_not_blank", "btrim(name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "inspection_certification_requirements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_name = table.Column<string>(type: "text", nullable: false),
                    requirement_level = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_certification_requirements", x => x.id);
                    table.CheckConstraint("CK_inspection_certification_requirements_level", "requirement_level IN (1, 2)");
                    table.CheckConstraint("CK_inspection_certification_requirements_type_name_not_blank", "btrim(certification_type_name) <> ''");
                    table.ForeignKey(
                        name: "FK_inspection_certification_requirements_inspection_id",
                        column: x => x.inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inspection_certification_requirements_type_id",
                        column: x => x.certification_type_id,
                        principalTable: "certification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inspection_certifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_certifications", x => x.id);
                    table.CheckConstraint("CK_inspection_certifications_type_name_not_blank", "btrim(certification_type_name) <> ''");
                    table.ForeignKey(
                        name: "FK_inspection_certifications_inspection_id",
                        column: x => x.inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inspection_certifications_type_id",
                        column: x => x.certification_type_id,
                        principalTable: "certification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "revision_certification_requirements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_criteria_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_name = table.Column<string>(type: "text", nullable: false),
                    requirement_level = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_revision_certification_requirements", x => x.id);
                    table.CheckConstraint("CK_revision_certification_requirements_level", "requirement_level IN (1, 2)");
                    table.CheckConstraint("CK_revision_certification_requirements_type_name_not_blank", "btrim(certification_type_name) <> ''");
                    table.ForeignKey(
                        name: "FK_revision_certification_requirements_revision_id",
                        column: x => x.inspection_criteria_revision_id,
                        principalTable: "inspection_criteria_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_revision_certification_requirements_type_id",
                        column: x => x.certification_type_id,
                        principalTable: "certification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "certification_documents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_certification_id = table.Column<long>(type: "bigint", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    uploaded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certification_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_certification_documents_certification_id",
                        column: x => x.inspection_certification_id,
                        principalTable: "inspection_certifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "certification_types",
                columns: new[] { "id", "display_order", "name" },
                values: new object[,]
                {
                    { 1L, 1, "CBP" },
                    { 2L, 2, "Clean" },
                    { 3L, 3, "C of C" },
                    { 4L, 4, "Gall" },
                    { 5L, 5, "Hardness" },
                    { 6L, 6, "Heat" },
                    { 7L, 7, "Material" },
                    { 8L, 8, "Patch" },
                    { 9L, 9, "Plate" },
                    { 10L, 10, "Salt Spray" },
                    { 11L, 11, "SPC" },
                    { 12L, 12, "Supplier Inspection" },
                    { 13L, 13, "Tensile/Proof Load/Yield" },
                    { 14L, 14, "Torque" },
                    { 15L, 15, "Notes/Misc" }
                });

            migrationBuilder.Sql(
                "SELECT setval(pg_get_serial_sequence('certification_types', 'id'), (SELECT MAX(id) FROM certification_types));");

            migrationBuilder.CreateIndex(
                name: "IX_certification_documents_certification_id",
                table: "certification_documents",
                column: "inspection_certification_id");

            migrationBuilder.CreateIndex(
                name: "UX_certification_types_display_order",
                table: "certification_types",
                column: "display_order",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_certification_types_name",
                table: "certification_types",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inspection_certification_requirements_type_id",
                table: "inspection_certification_requirements",
                column: "certification_type_id");

            migrationBuilder.CreateIndex(
                name: "UX_inspection_certification_requirements_inspection_id_type_id",
                table: "inspection_certification_requirements",
                columns: new[] { "inspection_id", "certification_type_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inspection_certifications_type_id",
                table: "inspection_certifications",
                column: "certification_type_id");

            migrationBuilder.CreateIndex(
                name: "UX_inspection_certifications_inspection_id_type_id",
                table: "inspection_certifications",
                columns: new[] { "inspection_id", "certification_type_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revision_certification_requirements_type_id",
                table: "revision_certification_requirements",
                column: "certification_type_id");

            migrationBuilder.CreateIndex(
                name: "UX_revision_certification_requirements_revision_id_type_id",
                table: "revision_certification_requirements",
                columns: new[] { "inspection_criteria_revision_id", "certification_type_id" },
                unique: true);

            // Certification requirements are part of a revision's historical meaning.
            // Keep the invariant in PostgreSQL even when writes bypass the application.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION prevent_published_certification_requirement_changes()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    old_is_published boolean := false;
                    new_is_published boolean := false;
                BEGIN
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        SELECT published_at_utc IS NOT NULL INTO old_is_published
                        FROM inspection_criteria_revisions
                        WHERE id = OLD.inspection_criteria_revision_id;
                    END IF;

                    IF TG_OP IN ('INSERT', 'UPDATE') THEN
                        SELECT published_at_utc IS NOT NULL INTO new_is_published
                        FROM inspection_criteria_revisions
                        WHERE id = NEW.inspection_criteria_revision_id;
                    END IF;

                    IF old_is_published OR new_is_published THEN
                        RAISE EXCEPTION 'Certification requirements in a published revision are immutable.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_revision_certification_requirements_protect_published
                BEFORE INSERT OR UPDATE OR DELETE ON revision_certification_requirements
                FOR EACH ROW
                EXECUTE FUNCTION prevent_published_certification_requirement_changes();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_revision_certification_requirements_protect_published ON revision_certification_requirements;
                DROP FUNCTION IF EXISTS prevent_published_certification_requirement_changes();
                """);

            migrationBuilder.DropTable(
                name: "certification_documents");

            migrationBuilder.DropTable(
                name: "inspection_certification_requirements");

            migrationBuilder.DropTable(
                name: "revision_certification_requirements");

            migrationBuilder.DropTable(
                name: "inspection_certifications");

            migrationBuilder.DropTable(
                name: "certification_types");
        }
    }
}
