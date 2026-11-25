using System.Text.Json;
using Confast.Web.Data;
using Confast.Web.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Confast.Web.Features.Maintenance;

internal sealed record FactoryResetPlan(string[] Tables, string[] Migrations, string Seeds)
{
    internal static FactoryResetPlan Create(AppDbContext db)
    {
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        if (model.Tables.Any(t => t.Schema is not (null or "public")))
            throw new InvalidOperationException("Factory Reset requires the application tables in public.");

        // Use the same data and insertion order that EF uses to create a fresh model.
        var inserts = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model).OfType<InsertDataOperation>()
            .Concat(new CustomerCertificationDelivery().UpOperations.OfType<InsertDataOperation>())
            .Concat(new AddCertificationEmailImplicitCc().UpOperations.OfType<InsertDataOperation>())
            .Append(new InsertDataOperation
            {
                Table = "certification_email_templates", Columns = AddCertificationEmailTemplates.SeedColumns,
                Values = AddCertificationEmailTemplates.SeedValues
            });
        var seeds = inserts.Select(insert => new
        {
            insert.Table,
            insert.Columns,
            Rows = Enumerable.Range(0, insert.Values.GetLength(0)).Select(row => insert.Columns
                .Select((column, index) => (column, Value: insert.Values[row, index]))
                .ToDictionary(entry => entry.column, entry => entry.Value))
        });
        return new(model.Tables.Select(t => t.Name).Append("__EFMigrationsHistory").Order(StringComparer.Ordinal).ToArray(),
            db.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToArray(), JsonSerializer.Serialize(seeds));
    }
}
