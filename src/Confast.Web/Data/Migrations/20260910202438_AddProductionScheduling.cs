using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "box_quantity",
                table: "parts",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "downtime_reasons",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downtime_reasons", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "production_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "production_holidays",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_holidays", x => x.date);
                });

            migrationBuilder.CreateTable(
                name: "production_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    po_number = table.Column<string>(type: "text", nullable: true),
                    mo_number = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_jobs", x => x.id);
                    table.CheckConstraint("ck_job_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_jobs_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    efficiency_percent = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_settings", x => x.id);
                    table.CheckConstraint("ck_production_efficiency", "id = 1 AND efficiency_percent > 0 AND efficiency_percent <= 100");
                });

            migrationBuilder.CreateTable(
                name: "sorting_machines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sorting_machines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "production_requirements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<long>(type: "bigint", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    cumulative_target = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_requirements", x => x.id);
                    table.CheckConstraint("ck_requirement_quantity", "cumulative_target > 0");
                    table.ForeignKey(
                        name: "FK_production_requirements_production_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "production_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "machine_downtime",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    machine_id = table.Column<long>(type: "bigint", nullable: false),
                    reason_id = table.Column<long>(type: "bigint", nullable: false),
                    start = table.Column<DateOnly>(type: "date", nullable: false),
                    end = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_downtime", x => x.id);
                    table.CheckConstraint("ck_downtime_dates", "\"end\" >= start");
                    table.ForeignKey(
                        name: "FK_machine_downtime_downtime_reasons_reason_id",
                        column: x => x.reason_id,
                        principalTable: "downtime_reasons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_machine_downtime_sorting_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "sorting_machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "machine_working_days",
                columns: table => new
                {
                    machine_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_working_days", x => new { x.machine_id, x.day });
                    table.CheckConstraint("ck_machine_hours", "day BETWEEN 0 AND 6 AND hours >= 0 AND hours <= 24");
                    table.ForeignKey(
                        name: "FK_machine_working_days_sorting_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "sorting_machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "part_machines",
                columns: table => new
                {
                    machine_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    target_pph = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_part_machines", x => new { x.machine_id, x.part_id });
                    table.CheckConstraint("ck_machine_pph", "target_pph > 0");
                    table.ForeignKey(
                        name: "FK_part_machines_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_part_machines_sorting_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "sorting_machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_segments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<long>(type: "bigint", nullable: false),
                    machine_id = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    completed_quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    not_before = table.Column<DateOnly>(type: "date", nullable: true),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    predecessor_id = table.Column<long>(type: "bigint", nullable: true),
                    actual_start = table.Column<DateOnly>(type: "date", nullable: true),
                    actual_completion = table.Column<DateOnly>(type: "date", nullable: true),
                    actual_elapsed_working_days = table.Column<decimal>(type: "numeric", nullable: true),
                    progress_as_of = table.Column<DateOnly>(type: "date", nullable: true),
                    original_hours = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    original_target_pph = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    original_efficiency_percent = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_segments", x => x.id);
                    table.CheckConstraint("ck_segment_quantity", "quantity >= 0 AND completed_quantity >= 0 AND completed_quantity <= quantity AND state BETWEEN 0 AND 2 AND (state <> 2 OR completed_quantity = quantity)");
                    table.ForeignKey(
                        name: "FK_production_segments_production_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "production_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_segments_production_segments_predecessor_id",
                        column: x => x.predecessor_id,
                        principalTable: "production_segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_segments_sorting_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "sorting_machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_progress",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    segment_id = table.Column<long>(type: "bigint", nullable: false),
                    machine_id = table.Column<long>(type: "bigint", nullable: false),
                    as_of = table.Column<DateOnly>(type: "date", nullable: false),
                    previous_quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    completed_quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_progress", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_progress_production_segments_segment_id",
                        column: x => x.segment_id,
                        principalTable: "production_segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_progress_sorting_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "sorting_machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "production_settings",
                columns: new[] { "id", "efficiency_percent", "revision" },
                values: new object[] { 1, 91m, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_machine_downtime_machine_id",
                table: "machine_downtime",
                column: "machine_id");

            migrationBuilder.CreateIndex(
                name: "IX_machine_downtime_reason_id",
                table: "machine_downtime",
                column: "reason_id");

            migrationBuilder.CreateIndex(
                name: "IX_part_machines_part_id",
                table: "part_machines",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_jobs_part_id",
                table: "production_jobs",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_progress_machine_id",
                table: "production_progress",
                column: "machine_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_progress_segment_id",
                table: "production_progress",
                column: "segment_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_requirements_job_id_date",
                table: "production_requirements",
                columns: new[] { "job_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_segments_job_id",
                table: "production_segments",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_segments_machine_id",
                table: "production_segments",
                column: "machine_id",
                unique: true,
                filter: "state = 1");

            migrationBuilder.CreateIndex(
                name: "IX_production_segments_predecessor_id",
                table: "production_segments",
                column: "predecessor_id");

            migrationBuilder.CreateIndex(
                name: "IX_sorting_machines_name",
                table: "sorting_machines",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "machine_downtime");

            migrationBuilder.DropTable(
                name: "machine_working_days");

            migrationBuilder.DropTable(
                name: "part_machines");

            migrationBuilder.DropTable(
                name: "production_audit");

            migrationBuilder.DropTable(
                name: "production_holidays");

            migrationBuilder.DropTable(
                name: "production_progress");

            migrationBuilder.DropTable(
                name: "production_requirements");

            migrationBuilder.DropTable(
                name: "production_settings");

            migrationBuilder.DropTable(
                name: "downtime_reasons");

            migrationBuilder.DropTable(
                name: "production_segments");

            migrationBuilder.DropTable(
                name: "production_jobs");

            migrationBuilder.DropTable(
                name: "sorting_machines");

            migrationBuilder.DropColumn(
                name: "box_quantity",
                table: "parts");
        }
    }
}
