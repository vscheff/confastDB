using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerCertificationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_certification_recipients",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    recipient_type = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_certification_recipients", x => x.id);
                    table.CheckConstraint("CK_customer_certification_recipients_email_not_blank", "btrim(email_address) <> ''");
                    table.CheckConstraint("CK_customer_certification_recipients_type", "recipient_type IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_customer_certification_recipients_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_certification_requirements",
                columns: table => new
                {
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_certification_requirements", x => new { x.customer_id, x.certification_type_id });
                    table.ForeignKey(
                        name: "FK_customer_certification_requirements_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_customer_certification_requirements_type_id",
                        column: x => x.certification_type_id,
                        principalTable: "certification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_certification_settings",
                columns: table => new
                {
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    filename_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_certification_settings", x => x.customer_id);
                    table.CheckConstraint("CK_customer_certification_settings_filename_template_not_blank", "btrim(filename_template) <> ''");
                    table.ForeignKey(
                        name: "FK_customer_certification_settings_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "certification_types",
                columns: new[] { "id", "display_order", "name" },
                values: new object[] { 16L, 16, "Inspection Sheet" });

            migrationBuilder.Sql(
                "SELECT setval(pg_get_serial_sequence('certification_types', 'id'), (SELECT MAX(id) FROM certification_types));");

            migrationBuilder.CreateIndex(
                name: "IX_customer_certification_recipients_customer_id",
                table: "customer_certification_recipients",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_certification_requirements_type_id",
                table: "customer_certification_requirements",
                column: "certification_type_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_certification_recipients");

            migrationBuilder.DropTable(
                name: "customer_certification_requirements");

            migrationBuilder.DropTable(
                name: "customer_certification_settings");

            migrationBuilder.DeleteData(
                table: "certification_types",
                keyColumn: "id",
                keyValue: 16L);

            migrationBuilder.Sql(
                "SELECT setval(pg_get_serial_sequence('certification_types', 'id'), (SELECT MAX(id) FROM certification_types));");
        }
    }
}
