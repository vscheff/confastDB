using Confast.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828130000_AllowDeletingUnusedCriteriaRevisions")]
public sealed class AllowDeletingUnusedCriteriaRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION prevent_published_criteria_revision_changes()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                is_used boolean := false;
            BEGIN
                IF OLD.published_at_utc IS NOT NULL THEN
                    SELECT EXISTS (
                        SELECT 1
                        FROM inspections
                        WHERE inspection_criteria_revision_id = OLD.id
                    ) INTO is_used;

                    IF TG_OP = 'DELETE' AND is_used THEN
                        RAISE EXCEPTION 'Inspection criteria revisions used by inspections cannot be deleted.'
                            USING ERRCODE = '55000';
                    ELSIF TG_OP <> 'DELETE' AND is_used AND NOT (
                        OLD.superseded_at_utc IS NULL
                        AND NEW.superseded_at_utc IS NOT NULL
                        AND NEW.id IS NOT DISTINCT FROM OLD.id
                        AND NEW.part_id IS NOT DISTINCT FROM OLD.part_id
                        AND NEW.revision_number IS NOT DISTINCT FROM OLD.revision_number
                        AND NEW.print_revision_number IS NOT DISTINCT FROM OLD.print_revision_number
                        AND NEW.part_description IS NOT DISTINCT FROM OLD.part_description
                        AND NEW.specification_used IS NOT DISTINCT FROM OLD.specification_used
                        AND NEW.notes IS NOT DISTINCT FROM OLD.notes
                        AND NEW.master_print_file_name IS NOT DISTINCT FROM OLD.master_print_file_name
                        AND NEW.master_print_content IS NOT DISTINCT FROM OLD.master_print_content
                        AND NEW.master_print_uploaded_at_utc IS NOT DISTINCT FROM OLD.master_print_uploaded_at_utc
                        AND NEW.created_at_utc IS NOT DISTINCT FROM OLD.created_at_utc
                        AND NEW.published_at_utc IS NOT DISTINCT FROM OLD.published_at_utc
                        AND NEW.change_note IS NOT DISTINCT FROM OLD.change_note
                    ) THEN
                        RAISE EXCEPTION 'Inspection criteria revisions used by inspections are immutable.'
                            USING ERRCODE = '55000';
                    END IF;
                END IF;

                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;

                RETURN NEW;
            END;
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION prevent_published_criteria_revision_changes()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                is_used boolean := false;
            BEGIN
                IF OLD.published_at_utc IS NOT NULL THEN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Published inspection criteria revisions cannot be deleted.'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT EXISTS (
                        SELECT 1
                        FROM inspections
                        WHERE inspection_criteria_revision_id = OLD.id
                    ) INTO is_used;

                    IF is_used AND NOT (
                        OLD.superseded_at_utc IS NULL
                        AND NEW.superseded_at_utc IS NOT NULL
                        AND NEW.id IS NOT DISTINCT FROM OLD.id
                        AND NEW.part_id IS NOT DISTINCT FROM OLD.part_id
                        AND NEW.revision_number IS NOT DISTINCT FROM OLD.revision_number
                        AND NEW.print_revision_number IS NOT DISTINCT FROM OLD.print_revision_number
                        AND NEW.part_description IS NOT DISTINCT FROM OLD.part_description
                        AND NEW.specification_used IS NOT DISTINCT FROM OLD.specification_used
                        AND NEW.notes IS NOT DISTINCT FROM OLD.notes
                        AND NEW.master_print_file_name IS NOT DISTINCT FROM OLD.master_print_file_name
                        AND NEW.master_print_content IS NOT DISTINCT FROM OLD.master_print_content
                        AND NEW.master_print_uploaded_at_utc IS NOT DISTINCT FROM OLD.master_print_uploaded_at_utc
                        AND NEW.created_at_utc IS NOT DISTINCT FROM OLD.created_at_utc
                        AND NEW.published_at_utc IS NOT DISTINCT FROM OLD.published_at_utc
                        AND NEW.change_note IS NOT DISTINCT FROM OLD.change_note
                    ) THEN
                        RAISE EXCEPTION 'Inspection criteria revisions used by inspections are immutable.'
                            USING ERRCODE = '55000';
                    END IF;
                END IF;

                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;

                RETURN NEW;
            END;
            $$;
            """);
    }
}
