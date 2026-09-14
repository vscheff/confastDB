using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowProductionJobDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION protect_production_history() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE'
                        AND TG_TABLE_NAME IN ('production_segments', 'production_progress')
                        AND current_setting('confast.allow_production_job_deletion', true) = 'on' THEN
                        RETURN OLD;
                    END IF;
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION protect_production_history() RETURNS trigger LANGUAGE plpgsql AS $$
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
                """);
        }
    }
}
