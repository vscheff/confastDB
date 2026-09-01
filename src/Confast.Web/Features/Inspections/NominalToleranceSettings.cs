using System.ComponentModel.DataAnnotations;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Inspections;

public sealed class NominalToleranceSettings
{
    public int Id { get; set; }

    public decimal ToleranceFloor { get; set; } = InspectionResultEvaluator.DefaultNominalToleranceFloor;

    public decimal LargeDimensionDivisor { get; set; } = InspectionResultEvaluator.DefaultNominalToleranceDivisor;

    public uint Version { get; set; }
}

public sealed class NominalToleranceSettingsEditModel
{
    [Display(Name = "Tolerance floor")]
    [Range(typeof(decimal), "0.000001", "999999999999.999999", ErrorMessage = "Tolerance floor must be greater than zero.")]
    public decimal ToleranceFloor { get; set; } = InspectionResultEvaluator.DefaultNominalToleranceFloor;

    [Display(Name = "Large-dimension divisor")]
    [Range(typeof(decimal), "0.000001", "999999999999.999999", ErrorMessage = "Large-dimension divisor must be greater than zero.")]
    public decimal LargeDimensionDivisor { get; set; } = InspectionResultEvaluator.DefaultNominalToleranceDivisor;

    public uint? Version { get; set; }
}

public sealed class NominalToleranceSettingsService(IDbContextFactory<AppDbContext> contextFactory)
{
    public async Task<NominalToleranceSettingsEditModel> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.NominalToleranceSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return settings is null
            ? new NominalToleranceSettingsEditModel()
            : new NominalToleranceSettingsEditModel
            {
                ToleranceFloor = settings.ToleranceFloor,
                LargeDimensionDivisor = settings.LargeDimensionDivisor,
                Version = settings.Version
            };
    }

    public async Task SaveAsync(NominalToleranceSettingsEditModel model, CancellationToken cancellationToken = default)
    {
        if (model.ToleranceFloor <= 0) throw new NominalToleranceSettingsException("Tolerance floor must be greater than zero.");
        if (model.LargeDimensionDivisor <= 0) throw new NominalToleranceSettingsException("Large-dimension divisor must be greater than zero.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.NominalToleranceSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            if (model.Version is not null) throw new NominalToleranceSettingsException("These settings were changed elsewhere. Reload before saving.");
            settings = new NominalToleranceSettings { Id = 1 };
            db.NominalToleranceSettings.Add(settings);
        }
        else
        {
            if (model.Version is null) throw new NominalToleranceSettingsException("These settings were changed elsewhere. Reload before saving.");
            db.Entry(settings).Property(x => x.Version).OriginalValue = model.Version.Value;
        }

        settings.ToleranceFloor = model.ToleranceFloor;
        settings.LargeDimensionDivisor = model.LargeDimensionDivisor;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            model.Version = settings.Version;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NominalToleranceSettingsException("These settings were changed elsewhere. Reload before saving.");
        }
    }
}

public sealed class NominalToleranceSettingsException(string message) : InvalidOperationException(message);
