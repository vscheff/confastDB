using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260903180000_AddPartSupplier")]
partial class AddPartSupplier
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
    }
}
