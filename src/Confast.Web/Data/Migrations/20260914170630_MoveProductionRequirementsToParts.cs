using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveProductionRequirementsToParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "part_id",
                table: "production_requirements",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE production_requirements requirement
                SET part_id = job.part_id
                FROM production_jobs job
                WHERE job.id = requirement.job_id;
                """);

            // The update queues the existing deferred integrity trigger. PostgreSQL
            // will not permit a table alteration until that queue has been drained.
            migrationBuilder.Sql("SET CONSTRAINTS production_requirement_quantity IMMEDIATE;");

            migrationBuilder.DropForeignKey(
                name: "FK_production_requirements_production_jobs_job_id",
                table: "production_requirements");

            migrationBuilder.DropIndex(
                name: "IX_production_requirements_job_id_date",
                table: "production_requirements");

            migrationBuilder.DropColumn(
                name: "job_id",
                table: "production_requirements");

            migrationBuilder.AlterColumn<long>(
                name: "part_id",
                table: "production_requirements",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_requirements_part_id_date",
                table: "production_requirements",
                columns: new[] { "part_id", "date" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_production_requirements_parts_part_id",
                table: "production_requirements",
                column: "part_id",
                principalTable: "parts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION validate_production_part(affected_part bigint) RETURNS void LANGUAGE plpgsql AS $$
                DECLARE part_quantity numeric;
                BEGIN
                    IF affected_part IS NULL THEN RETURN; END IF;
                    SELECT COALESCE(SUM(quantity), 0) INTO part_quantity FROM production_jobs WHERE part_id = affected_part;
                    IF EXISTS (SELECT 1 FROM production_requirements WHERE part_id = affected_part AND cumulative_target > part_quantity)
                        OR EXISTS (SELECT 1 FROM production_requirements a JOIN production_requirements b
                            ON a.part_id = b.part_id AND a.date < b.date
                            WHERE a.part_id = affected_part AND a.cumulative_target >= b.cumulative_target) THEN
                        RAISE EXCEPTION 'Production requirements must increase and fit the scheduled quantity for the part' USING ERRCODE = '23514';
                    END IF;
                END $$;

                CREATE OR REPLACE FUNCTION validate_production_job() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE affected_id bigint; affected_part bigint; job_quantity numeric; allocated numeric;
                BEGIN
                    IF TG_TABLE_NAME = 'production_requirements' THEN
                        PERFORM validate_production_part(NEW.part_id);
                        IF OLD.part_id IS DISTINCT FROM NEW.part_id THEN PERFORM validate_production_part(OLD.part_id); END IF;
                    ELSIF TG_TABLE_NAME = 'production_jobs' THEN
                        affected_id := COALESCE(NEW.id, OLD.id);
                        SELECT quantity INTO job_quantity FROM production_jobs WHERE id = affected_id FOR UPDATE;
                        IF FOUND THEN
                            SELECT COALESCE(SUM(quantity), 0) INTO allocated FROM production_segments WHERE job_id = affected_id;
                            IF job_quantity <> allocated THEN
                                RAISE EXCEPTION 'Production segment allocations must equal the job quantity' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                        PERFORM validate_production_part(NEW.part_id);
                        IF OLD.part_id IS DISTINCT FROM NEW.part_id THEN PERFORM validate_production_part(OLD.part_id); END IF;
                    ELSE
                        affected_id := COALESCE(NEW.job_id, OLD.job_id);
                        SELECT quantity, part_id INTO job_quantity, affected_part FROM production_jobs WHERE id = affected_id FOR UPDATE;
                        IF FOUND THEN
                            SELECT COALESCE(SUM(quantity), 0) INTO allocated FROM production_segments WHERE job_id = affected_id;
                            IF job_quantity <> allocated THEN
                                RAISE EXCEPTION 'Production segment allocations must equal the job quantity' USING ERRCODE = '23514';
                            END IF;
                            PERFORM validate_production_part(affected_part);
                        END IF;
                    END IF;
                    RETURN NULL;
                END $$;
                DROP TRIGGER production_job_quantity ON production_jobs;
                CREATE CONSTRAINT TRIGGER production_job_quantity AFTER INSERT OR UPDATE OR DELETE ON production_jobs
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION validate_production_job();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Part-wide production requirements cannot be safely converted back to job-wide requirements.");
        }
    }
}
