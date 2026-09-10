using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionSchedulingIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE parts ADD CONSTRAINT ck_part_box_quantity CHECK (box_quantity IS NULL OR box_quantity > 0);

                CREATE FUNCTION validate_production_job() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE affected_id bigint; required numeric; allocated numeric;
                BEGIN
                    IF TG_TABLE_NAME = 'production_jobs' THEN affected_id := COALESCE(NEW.id, OLD.id);
                    ELSE affected_id := COALESCE(NEW.job_id, OLD.job_id); END IF;
                    SELECT quantity INTO required FROM production_jobs WHERE id = affected_id FOR UPDATE;
                    IF NOT FOUND THEN RETURN NULL; END IF;
                    SELECT COALESCE(SUM(quantity), 0) INTO allocated FROM production_segments WHERE job_id = affected_id;
                    IF required <> allocated THEN
                        RAISE EXCEPTION 'Production segment allocations must equal the job quantity' USING ERRCODE = '23514';
                    END IF;
                    IF EXISTS (SELECT 1 FROM production_requirements WHERE job_id = affected_id AND cumulative_target > required)
                        OR EXISTS (SELECT 1 FROM production_requirements a JOIN production_requirements b
                            ON a.job_id = b.job_id AND a.date < b.date
                            WHERE a.job_id = affected_id AND a.cumulative_target >= b.cumulative_target) THEN
                        RAISE EXCEPTION 'Production requirements must increase and fit the job quantity' USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER production_job_quantity AFTER INSERT OR UPDATE ON production_jobs
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION validate_production_job();
                CREATE CONSTRAINT TRIGGER production_segment_quantity AFTER INSERT OR UPDATE OR DELETE ON production_segments
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION validate_production_job();
                CREATE CONSTRAINT TRIGGER production_requirement_quantity AFTER INSERT OR UPDATE OR DELETE ON production_requirements
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION validate_production_job();

                CREATE FUNCTION protect_production_history() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_TABLE_NAME IN ('production_progress', 'production_audit') THEN
                        RAISE EXCEPTION 'Production history is append-only' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        IF OLD.state <> 0 THEN RAISE EXCEPTION 'Started production history cannot be deleted' USING ERRCODE = '23514'; END IF;
                        RETURN OLD;
                    END IF;
                    IF (NEW.original_hours, NEW.original_target_pph, NEW.original_efficiency_percent)
                        IS DISTINCT FROM (OLD.original_hours, OLD.original_target_pph, OLD.original_efficiency_percent) THEN
                        RAISE EXCEPTION 'Original production estimates are immutable' USING ERRCODE = '23514';
                    END IF;
                    IF OLD.state = 2 AND (NEW.job_id, NEW.machine_id, NEW.quantity, NEW.completed_quantity, NEW.state,
                        NEW.actual_start, NEW.actual_completion, NEW.progress_as_of, NEW.actual_elapsed_working_days)
                        IS DISTINCT FROM (OLD.job_id, OLD.machine_id, OLD.quantity, OLD.completed_quantity, OLD.state,
                        OLD.actual_start, OLD.actual_completion, OLD.progress_as_of, OLD.actual_elapsed_working_days) THEN
                        RAISE EXCEPTION 'Completed production history is immutable' USING ERRCODE = '23514';
                    END IF;
                    IF OLD.state = 1 AND NEW.machine_id <> OLD.machine_id THEN
                        RAISE EXCEPTION 'Preserve the machine for performed production; split the remaining allocation' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER production_history_protection BEFORE UPDATE OR DELETE ON production_segments
                    FOR EACH ROW EXECUTE FUNCTION protect_production_history();
                CREATE TRIGGER production_progress_protection BEFORE UPDATE OR DELETE ON production_progress
                    FOR EACH ROW EXECUTE FUNCTION protect_production_history();
                CREATE TRIGGER production_audit_protection BEFORE UPDATE OR DELETE ON production_audit
                    FOR EACH ROW EXECUTE FUNCTION protect_production_history();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER production_audit_protection ON production_audit;
                DROP TRIGGER production_progress_protection ON production_progress;
                DROP TRIGGER production_history_protection ON production_segments;
                DROP FUNCTION protect_production_history();
                DROP TRIGGER production_requirement_quantity ON production_requirements;
                DROP TRIGGER production_segment_quantity ON production_segments;
                DROP TRIGGER production_job_quantity ON production_jobs;
                DROP FUNCTION validate_production_job();
                ALTER TABLE parts DROP CONSTRAINT ck_part_box_quantity;
                """);

        }
    }
}

