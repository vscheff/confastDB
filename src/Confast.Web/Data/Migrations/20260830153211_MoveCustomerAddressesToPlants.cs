using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveCustomerAddressesToPlants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address_line_1",
                table: "plants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_line_2",
                table: "plants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "plants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                table: "plants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "plants",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE plants
                SET address_line_1 = customers.address_line_1,
                    address_line_2 = customers.address_line_2,
                    city = customers.city,
                    state = customers.state,
                    postal_code = customers.postal_code
                FROM customers
                WHERE plants.customer_id = customers.id
                  AND plants.name = 'Main';
                """);

            migrationBuilder.DropColumn(name: "address_line_1", table: "customers");
            migrationBuilder.DropColumn(name: "address_line_2", table: "customers");
            migrationBuilder.DropColumn(name: "city", table: "customers");
            migrationBuilder.DropColumn(name: "postal_code", table: "customers");
            migrationBuilder.DropColumn(name: "state", table: "customers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address_line_1",
                table: "customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_line_2",
                table: "customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                table: "customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "customers",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE customers
                SET address_line_1 = plants.address_line_1,
                    address_line_2 = plants.address_line_2,
                    city = plants.city,
                    state = plants.state,
                    postal_code = plants.postal_code
                FROM plants
                WHERE plants.customer_id = customers.id
                  AND plants.name = 'Main';
                """);

            migrationBuilder.DropColumn(name: "address_line_1", table: "plants");
            migrationBuilder.DropColumn(name: "address_line_2", table: "plants");
            migrationBuilder.DropColumn(name: "city", table: "plants");
            migrationBuilder.DropColumn(name: "postal_code", table: "plants");
            migrationBuilder.DropColumn(name: "state", table: "plants");
        }
    }
}
