using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionCriteriaMasterPrint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "master_print_content",
                table: "inspection_criteria_revisions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "master_print_file_name",
                table: "inspection_criteria_revisions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "master_print_uploaded_at_utc",
                table: "inspection_criteria_revisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(CreateRevisionProtectionFunction(includeMasterPrint: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateRevisionProtectionFunction(includeMasterPrint: false));

            migrationBuilder.DropColumn(
                name: "master_print_content",
                table: "inspection_criteria_revisions");

            migrationBuilder.DropColumn(
                name: "master_print_file_name",
                table: "inspection_criteria_revisions");

            migrationBuilder.DropColumn(
                name: "master_print_uploaded_at_utc",
                table: "inspection_criteria_revisions");
        }

        private static string CreateRevisionProtectionFunction(bool includeMasterPrint)
        {
            var masterPrintComparison = includeMasterPrint
                ? """
                    AND NEW.master_print_file_name IS NOT DISTINCT FROM OLD.master_print_file_name
                    AND NEW.master_print_content IS NOT DISTINCT FROM OLD.master_print_content
                    AND NEW.master_print_uploaded_at_utc IS NOT DISTINCT FROM OLD.master_print_uploaded_at_utc
                    """
                : string.Empty;

            return $$"""
                CREATE OR REPLACE FUNCTION prevent_published_criteria_revision_changes()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF OLD.published_at_utc IS NOT NULL THEN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'Published inspection criteria revisions cannot be deleted.'
                                USING ERRCODE = '55000';
                        END IF;

                        IF NOT (
                            OLD.superseded_at_utc IS NULL
                            AND NEW.superseded_at_utc IS NOT NULL
                            AND NEW.id IS NOT DISTINCT FROM OLD.id
                            AND NEW.part_id IS NOT DISTINCT FROM OLD.part_id
                            AND NEW.revision_number IS NOT DISTINCT FROM OLD.revision_number
                            AND NEW.print_revision_number IS NOT DISTINCT FROM OLD.print_revision_number
                            AND NEW.part_description IS NOT DISTINCT FROM OLD.part_description
                            AND NEW.specification_used IS NOT DISTINCT FROM OLD.specification_used
                            AND NEW.notes IS NOT DISTINCT FROM OLD.notes
                            {{masterPrintComparison}}
                            AND NEW.created_at_utc IS NOT DISTINCT FROM OLD.created_at_utc
                            AND NEW.published_at_utc IS NOT DISTINCT FROM OLD.published_at_utc
                            AND NEW.change_note IS NOT DISTINCT FROM OLD.change_note
                        ) THEN
                            RAISE EXCEPTION 'Published inspection criteria revisions are immutable.'
                                USING ERRCODE = '55000';
                        END IF;
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """;
        }
    }
}
