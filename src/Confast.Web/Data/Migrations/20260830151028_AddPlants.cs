using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plants",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    plant_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    certification_filename_pattern_override = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plants", x => x.id);
                    table.CheckConstraint("CK_plants_certification_filename_override_not_blank", "certification_filename_pattern_override IS NULL OR btrim(certification_filename_pattern_override) <> ''");
                    table.CheckConstraint("CK_plants_name_not_blank", "btrim(name) <> ''");
                    table.ForeignKey(
                        name: "FK_plants_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "part_plants",
                columns: table => new
                {
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    plant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_part_plants", x => new { x.part_id, x.plant_id });
                    table.ForeignKey(
                        name: "FK_part_plants_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_part_plants_plants_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_part_plants_plant_id",
                table: "part_plants",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "UX_plants_customer_id_name",
                table: "plants",
                columns: new[] { "customer_id", "name" },
                unique: true);

            migrationBuilder.Sql("""
                WITH default_plants AS (
                    INSERT INTO plants (customer_id, name)
                    SELECT id, 'Main' FROM customers
                    RETURNING id, customer_id
                )
                INSERT INTO part_plants (part_id, plant_id)
                SELECT parts.id, default_plants.id
                FROM parts
                INNER JOIN default_plants ON default_plants.customer_id = parts.customer_id;
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_part_plant_customer_match()
                RETURNS trigger AS $$
                BEGIN
                    IF (SELECT customer_id FROM parts WHERE id = NEW.part_id)
                       IS DISTINCT FROM (SELECT customer_id FROM plants WHERE id = NEW.plant_id) THEN
                        RAISE EXCEPTION 'Part and plant must belong to the same customer';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_part_plants_customer_match
                BEFORE INSERT OR UPDATE ON part_plants
                FOR EACH ROW EXECUTE FUNCTION enforce_part_plant_customer_match();

                CREATE FUNCTION enforce_part_customer_change_matches_plants()
                RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM part_plants
                        INNER JOIN plants ON plants.id = part_plants.plant_id
                        WHERE part_plants.part_id = NEW.id AND plants.customer_id <> NEW.customer_id
                    ) THEN
                        RAISE EXCEPTION 'Part customer cannot differ from an assigned plant customer';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_parts_customer_match_assigned_plants
                BEFORE UPDATE OF customer_id ON parts
                FOR EACH ROW EXECUTE FUNCTION enforce_part_customer_change_matches_plants();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_part_plants_customer_match\" ON part_plants;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS enforce_part_plant_customer_match();");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_parts_customer_match_assigned_plants\" ON parts;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS enforce_part_customer_change_matches_plants();");
            migrationBuilder.DropTable(
                name: "part_plants");

            migrationBuilder.DropTable(
                name: "plants");
        }
    }
}
