using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sort_log_downtime_causes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sort_log_downtime_causes", x => x.id);
                    table.CheckConstraint("CK_sort_log_downtime_causes_display_order", "display_order >= 0");
                });

            migrationBuilder.CreateTable(
                name: "sort_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    production_date = table.Column<DateOnly>(type: "date", nullable: false),
                    machine_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    production_segment_id = table.Column<long>(type: "bigint", nullable: true),
                    target_pph_snapshot = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: false),
                    comments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sort_logs", x => x.id);
                    table.CheckConstraint("CK_sort_logs_comments", "comments IS NULL OR char_length(comments) <= 4000");
                    table.CheckConstraint("CK_sort_logs_target_pph", "target_pph_snapshot > 0");
                    table.ForeignKey(
                        name: "FK_sort_logs_identity_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sort_logs_inspections_inspection_id",
                        column: x => x.inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sort_logs_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sort_logs_production_segments_production_segment_id",
                        column: x => x.production_segment_id,
                        principalTable: "production_segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sort_logs_sorting_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "sorting_machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sort_log_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sort_log_id = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    start_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stop_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    downtime_cause_id = table.Column<long>(type: "bigint", nullable: true),
                    pass_quantity = table.Column<long>(type: "bigint", nullable: false),
                    fail_quantity = table.Column<long>(type: "bigint", nullable: false),
                    boundary_samples_ran_quantity = table.Column<long>(type: "bigint", nullable: false),
                    boundary_samples_passed_quantity = table.Column<long>(type: "bigint", nullable: false),
                    start_box_count = table.Column<long>(type: "bigint", nullable: true),
                    end_box_count = table.Column<long>(type: "bigint", nullable: true),
                    production_user_id = table.Column<string>(type: "text", nullable: true),
                    quality_user_id = table.Column<string>(type: "text", nullable: true),
                    production_initials = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    quality_initials = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sort_log_lines", x => x.id);
                    table.CheckConstraint("CK_sort_log_lines_notes", "notes IS NULL OR char_length(notes) <= 4000");
                    table.CheckConstraint("CK_sort_log_lines_quantities", "pass_quantity >= 0 AND fail_quantity >= 0 AND boundary_samples_ran_quantity >= 0 AND boundary_samples_passed_quantity >= 0 AND boundary_samples_passed_quantity <= boundary_samples_ran_quantity");
                    table.CheckConstraint("CK_sort_log_lines_time", "stop_time_utc IS NULL OR stop_time_utc >= start_time_utc");
                    table.ForeignKey(
                        name: "FK_sort_log_lines_identity_users_production_user_id",
                        column: x => x.production_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sort_log_lines_identity_users_quality_user_id",
                        column: x => x.quality_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sort_log_lines_sort_log_downtime_causes_downtime_cause_id",
                        column: x => x.downtime_cause_id,
                        principalTable: "sort_log_downtime_causes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sort_log_lines_sort_logs_sort_log_id",
                        column: x => x.sort_log_id,
                        principalTable: "sort_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "sort_log_downtime_causes",
                columns: new[] { "id", "display_order", "is_active", "name", "normalized_name" },
                values: new object[] { -1L, 1000, true, "End of Day", "END OF DAY" });

            migrationBuilder.CreateIndex(
                name: "UX_sort_log_downtime_causes_normalized_name",
                table: "sort_log_downtime_causes",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sort_log_lines_downtime_cause_id",
                table: "sort_log_lines",
                column: "downtime_cause_id");

            migrationBuilder.CreateIndex(
                name: "IX_sort_log_lines_production_user_id",
                table: "sort_log_lines",
                column: "production_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sort_log_lines_quality_user_id",
                table: "sort_log_lines",
                column: "quality_user_id");

            migrationBuilder.CreateIndex(
                name: "UX_sort_log_lines_active",
                table: "sort_log_lines",
                column: "sort_log_id",
                unique: true,
                filter: "stop_time_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_sort_log_lines_sequence",
                table: "sort_log_lines",
                columns: new[] { "sort_log_id", "sequence" },
                unique: true);

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                ALTER TABLE sort_log_lines
                ADD CONSTRAINT "EX_sort_log_lines_no_overlap"
                EXCLUDE USING gist (
                    sort_log_id WITH =,
                    tstzrange(start_time_utc, COALESCE(stop_time_utc, 'infinity'::timestamptz), '[)') WITH &&
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_sort_logs_created_by_user_id",
                table: "sort_logs",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sort_logs_inspection_id",
                table: "sort_logs",
                column: "inspection_id");

            migrationBuilder.CreateIndex(
                name: "IX_sort_logs_machine_id",
                table: "sort_logs",
                column: "machine_id");

            migrationBuilder.CreateIndex(
                name: "IX_sort_logs_part_id",
                table: "sort_logs",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_sort_logs_production_segment_id",
                table: "sort_logs",
                column: "production_segment_id");

            migrationBuilder.CreateIndex(
                name: "UX_sort_logs_daily_job",
                table: "sort_logs",
                columns: new[] { "production_date", "machine_id", "part_id", "inspection_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sort_log_lines");

            migrationBuilder.DropTable(
                name: "sort_log_downtime_causes");

            migrationBuilder.DropTable(
                name: "sort_logs");
        }
    }
}
