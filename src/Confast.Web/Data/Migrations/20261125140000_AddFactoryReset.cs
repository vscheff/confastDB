using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Confast.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125140000_AddFactoryReset")]
public sealed class AddFactoryReset : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FactoryResetSql.Install);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT FROM confast_maintenance.factory_resets WHERE finished_at IS NULL) THEN
                RAISE EXCEPTION 'Finish the in-progress Factory Reset before downgrading';
            END IF;
        END $$;
        DROP SCHEMA confast_maintenance CASCADE;
        """);
}
