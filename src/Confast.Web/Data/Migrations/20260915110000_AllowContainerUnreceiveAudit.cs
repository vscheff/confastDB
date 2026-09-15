using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    public partial class AllowContainerUnreceiveAudit : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateOnly>(
                name: "received_date",
                table: "container_receipt_history",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION protect_container_receipt_status() RETURNS trigger AS $$
                BEGIN
                    IF OLD.received_date IS NOT NULL AND NEW.received_date IS NULL
                        AND EXISTS (
                            SELECT 1
                            FROM container_receipt_allocations a
                            JOIN container_group_parts p ON p.id = a.container_group_part_id
                            JOIN container_groups g ON g.id = p.container_group_id
                            WHERE g.container_id = OLD.id)
                    THEN
                        RAISE EXCEPTION 'A container with inspection receipt work cannot have receipt status cleared' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM container_receipt_history WHERE received_date IS NULL;
                CREATE OR REPLACE FUNCTION protect_container_receipt_status() RETURNS trigger AS $$
                BEGIN
                    IF OLD.received_date IS NOT NULL AND NEW.received_date IS NULL
                        AND EXISTS (
                            SELECT 1
                            FROM container_receipt_allocations a
                            JOIN container_group_parts p ON p.id = a.container_group_part_id
                            JOIN container_groups g ON g.id = p.container_group_id
                            WHERE g.container_id = OLD.id AND a.reversed_at_utc IS NULL)
                    THEN
                        RAISE EXCEPTION 'A container with active allocations cannot have receipt status cleared' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "received_date",
                table: "container_receipt_history",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
