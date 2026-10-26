using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Confast.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261026140000_AddRuntimeAuthorizationLock")]
public sealed class AddRuntimeAuthorizationLock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION public.confast_lock_authorization() RETURNS SETOF public.authorization_state
        LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public
        AS 'SELECT * FROM public.authorization_state WHERE id = 1 FOR UPDATE';
        REVOKE ALL ON FUNCTION public.confast_lock_authorization() FROM PUBLIC;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP FUNCTION public.confast_lock_authorization()");
}
