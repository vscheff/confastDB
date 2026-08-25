using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionCriteriaRevisionDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "part_description",
                table: "inspection_criteria_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "print_revision_number",
                table: "inspection_criteria_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specification_used",
                table: "inspection_criteria_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE inspection_criteria_revisions AS revision
                SET print_revision_number = part.revision,
                    part_description = part.description
                FROM parts AS part
                WHERE revision.part_id = part.id
                    AND revision.published_at_utc IS NULL;

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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
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
                """);

            migrationBuilder.DropColumn(
                name: "part_description",
                table: "inspection_criteria_revisions");

            migrationBuilder.DropColumn(
                name: "print_revision_number",
                table: "inspection_criteria_revisions");

            migrationBuilder.DropColumn(
                name: "specification_used",
                table: "inspection_criteria_revisions");
        }
    }
}
