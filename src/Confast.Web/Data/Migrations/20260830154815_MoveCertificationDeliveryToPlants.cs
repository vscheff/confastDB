using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveCertificationDeliveryToPlants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plant_certification_recipients",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    plant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    recipient_type = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_certification_recipients", x => x.id);
                    table.CheckConstraint("CK_plant_certification_recipients_email_not_blank", "btrim(email_address) <> ''");
                    table.CheckConstraint("CK_plant_certification_recipients_type", "recipient_type IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_plant_certification_recipients_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plant_certification_requirements",
                columns: table => new
                {
                    plant_id = table.Column<long>(type: "bigint", nullable: false),
                    certification_type_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_certification_requirements", x => new { x.plant_id, x.certification_type_id });
                    table.ForeignKey(
                        name: "FK_plant_certification_requirements_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plant_certification_requirements_type_id",
                        column: x => x.certification_type_id,
                        principalTable: "certification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plant_certification_settings",
                columns: table => new
                {
                    plant_id = table.Column<long>(type: "bigint", nullable: false),
                    filename_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    multi_lot_filename_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_certification_settings", x => x.plant_id);
                    table.CheckConstraint("CK_plant_certification_settings_filename_template_not_blank", "btrim(filename_template) <> ''");
                    table.CheckConstraint("CK_plant_certification_settings_multi_lot_filename_not_blank", "btrim(multi_lot_filename_template) <> ''");
                    table.ForeignKey(
                        name: "FK_plant_certification_settings_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plant_certification_recipients_plant_id",
                table: "plant_certification_recipients",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "IX_plant_certification_requirements_type_id",
                table: "plant_certification_requirements",
                column: "certification_type_id");

            migrationBuilder.Sql("""
                INSERT INTO plant_certification_recipients (plant_id, name, email_address, recipient_type)
                SELECT plants.id, recipients.name, recipients.email_address, recipients.recipient_type
                FROM plants
                INNER JOIN customer_certification_recipients AS recipients
                    ON recipients.customer_id = plants.customer_id;

                INSERT INTO plant_certification_requirements (plant_id, certification_type_id)
                SELECT plants.id, requirements.certification_type_id
                FROM plants
                INNER JOIN customer_certification_requirements AS requirements
                    ON requirements.customer_id = plants.customer_id;

                INSERT INTO plant_certification_settings (plant_id, filename_template, multi_lot_filename_template)
                SELECT plants.id,
                       settings.filename_template,
                       COALESCE(plants.certification_filename_pattern_override, settings.multi_lot_filename_template)
                FROM plants
                INNER JOIN customer_certification_settings AS settings
                    ON settings.customer_id = plants.customer_id;

                INSERT INTO plant_certification_settings (plant_id, multi_lot_filename_template)
                SELECT id, certification_filename_pattern_override
                FROM plants
                WHERE certification_filename_pattern_override IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM plant_certification_settings
                      WHERE plant_certification_settings.plant_id = plants.id);
                """);

            migrationBuilder.DropTable(name: "customer_certification_recipients");
            migrationBuilder.DropTable(name: "customer_certification_requirements");
            migrationBuilder.DropTable(name: "customer_certification_settings");
            migrationBuilder.DropColumn(name: "certification_filename_pattern_override", table: "plants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plant_certification_recipients");

            migrationBuilder.DropTable(
                name: "plant_certification_requirements");

            migrationBuilder.DropTable(
                name: "plant_certification_settings");

            migrationBuilder.AddColumn<string>(
                name: "certification_filename_pattern_override",
                table: "plants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_certification_recipients",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    filename_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    multi_lot_filename_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_certification_settings", x => x.customer_id);
                    table.CheckConstraint("CK_customer_cert_settings_multi_lot_filename_not_blank", "btrim(multi_lot_filename_template) <> ''");
                    table.CheckConstraint("CK_customer_certification_settings_filename_template_not_blank", "btrim(filename_template) <> ''");
                    table.ForeignKey(
                        name: "FK_customer_certification_settings_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_certification_recipients_customer_id",
                table: "customer_certification_recipients",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_certification_requirements_type_id",
                table: "customer_certification_requirements",
                column: "certification_type_id");
        }
    }
}
