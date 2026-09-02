using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Parts;

public sealed class PartFlipService(IDbContextFactory<AppDbContext> contextFactory)
{
    private const string DuplicateConstraint = "UX_part_flip_definitions_source_target";

    public async Task<PartFlipConfiguration?> GetConfigurationAsync(long sourcePartId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Parts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourcePartId, cancellationToken);
        if (source is null) return null;
        var sourceCriteria = await CurrentCriteriaAsync(db, sourcePartId, cancellationToken);
        var definitions = await db.PartFlipDefinitions.AsNoTracking()
            .Where(x => x.SourcePartId == sourcePartId)
            .Include(x => x.TargetPart).ThenInclude(x => x.Customer)
            .Include(x => x.CriterionMappings).ThenInclude(x => x.SourceCriterion)
            .Include(x => x.CriterionMappings).ThenInclude(x => x.TargetCriterion)
            .OrderBy(x => x.TargetPart.PartNumber).ToListAsync(cancellationToken);
        var items = new List<PartFlipDefinitionItem>();
        foreach (var definition in definitions)
        {
            var targetCriteria = await CurrentCriteriaAsync(db, definition.TargetPartId, cancellationToken);
            var validation = ValidateMappings(sourceCriteria, targetCriteria, definition.CriterionMappings.Select(x => new PartFlipMappingInput(x.SourceCriterionId, x.TargetCriterionId)));
            items.Add(new PartFlipDefinitionItem(definition.Id, definition.TargetPartId, definition.TargetPart.PartNumber, definition.TargetPart.Customer.Name, validation, validation ? null : "Mappings no longer match the current criteria revisions.", definition.CriterionMappings.Select(x => new PartFlipMappingInput(x.SourceCriterionId, x.TargetCriterionId)).ToList()));
        }
        var targets = await db.Parts.AsNoTracking().Where(x => x.Id != sourcePartId && x.IsActive).OrderBy(x => x.PartNumber).Select(x => new PartFlipTargetOption(x.Id, x.PartNumber)).ToListAsync(cancellationToken);
        return new PartFlipConfiguration(sourcePartId, sourceCriteria.Select(ToOption).ToList(), items, targets);
    }

    public async Task<IReadOnlyList<PartFlipCriterionOption>> GetCurrentCriterionOptionsAsync(long partId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await CurrentCriteriaAsync(db, partId, cancellationToken)).Select(ToOption).ToList();
    }

    public async Task<SavePartFlipResult> SaveDefinitionAsync(long sourcePartId, long targetPartId, IReadOnlyList<PartFlipMappingInput>? mappings = null, CancellationToken cancellationToken = default)
    {
        if (sourcePartId == targetPartId) return new(SavePartFlipStatus.Invalid, Message: "A part cannot flip to itself.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Parts.AnyAsync(x => x.Id == sourcePartId, cancellationToken) || !await db.Parts.AnyAsync(x => x.Id == targetPartId && x.IsActive, cancellationToken)) return new(SavePartFlipStatus.NotFound);
        var sourceCriteria = await CurrentCriteriaAsync(db, sourcePartId, cancellationToken);
        var targetCriteria = await CurrentCriteriaAsync(db, targetPartId, cancellationToken);
        var effectiveMappings = mappings?.ToList() ?? InferMappings(sourceCriteria, targetCriteria);
        if (!ValidateMappings(sourceCriteria, targetCriteria, effectiveMappings)) return new(SavePartFlipStatus.Invalid, Message: "Every current criterion must have one compatible, one-to-one mapping. Criterion names are only matched automatically when unambiguous.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var definition = new PartFlipDefinition { SourcePartId = sourcePartId, TargetPartId = targetPartId };
        foreach (var mapping in effectiveMappings) definition.CriterionMappings.Add(new PartFlipCriterionMapping { SourceCriterionId = mapping.SourceCriterionId, TargetCriterionId = mapping.TargetCriterionId });
        db.PartFlipDefinitions.Add(definition);
        var reverseAlreadyExists = await db.PartFlipDefinitions.AnyAsync(
            x => x.SourcePartId == targetPartId && x.TargetPartId == sourcePartId,
            cancellationToken);
        if (!reverseAlreadyExists)
        {
            var reverse = new PartFlipDefinition { SourcePartId = targetPartId, TargetPartId = sourcePartId };
            foreach (var mapping in effectiveMappings)
            {
                reverse.CriterionMappings.Add(new PartFlipCriterionMapping
                {
                    SourceCriterionId = mapping.TargetCriterionId,
                    TargetCriterionId = mapping.SourceCriterionId
                });
            }
            db.PartFlipDefinitions.Add(reverse);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(SavePartFlipStatus.Saved, definition.Id);
        }
        catch (DbUpdateException exception) when ((exception.GetBaseException() as PostgresException)?.ConstraintName == DuplicateConstraint) { return new(SavePartFlipStatus.Duplicate, Message: "That flip destination is already configured."); }
    }

    public async Task<SavePartFlipResult> ReplaceMappingsAsync(long definitionId, IReadOnlyList<PartFlipMappingInput> mappings, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definition = await db.PartFlipDefinitions.Include(x => x.CriterionMappings).SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken);
        if (definition is null) return new(SavePartFlipStatus.NotFound);
        var sourceCriteria = await CurrentCriteriaAsync(db, definition.SourcePartId, cancellationToken);
        var targetCriteria = await CurrentCriteriaAsync(db, definition.TargetPartId, cancellationToken);
        if (!ValidateMappings(sourceCriteria, targetCriteria, mappings)) return new(SavePartFlipStatus.Invalid, Message: "Mappings must cover both current criterion sets exactly once and use matching units.");
        db.PartFlipCriterionMappings.RemoveRange(definition.CriterionMappings);
        db.PartFlipCriterionMappings.AddRange(mappings.Select(x => new PartFlipCriterionMapping { PartFlipDefinitionId = definition.Id, SourceCriterionId = x.SourceCriterionId, TargetCriterionId = x.TargetCriterionId }));
        await db.SaveChangesAsync(cancellationToken); return new(SavePartFlipStatus.Saved, definition.Id);
    }

    public async Task<bool> DeleteDefinitionAsync(long definitionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definition = await db.PartFlipDefinitions.SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken);
        if (definition is null) return false;
        db.PartFlipDefinitions.Remove(definition);
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) { return false; } // lineage restricts deletion after a flip.
    }

    internal static List<PartFlipMappingInput> InferMappings(IReadOnlyList<InspectionCriteria.InspectionCriterion> source, IReadOnlyList<InspectionCriteria.InspectionCriterion> target) =>
        source.Select(s => (Source: s, Targets: target.Where(t => string.Equals(t.Name.Trim(), s.Name.Trim(), StringComparison.OrdinalIgnoreCase) && UnitsMatch(s.Unit, t.Unit)).ToList()))
            .Where(x => x.Targets.Count == 1).Select(x => new PartFlipMappingInput(x.Source.Id, x.Targets[0].Id)).ToList();

    internal static bool ValidateMappings(IReadOnlyList<InspectionCriteria.InspectionCriterion> source, IReadOnlyList<InspectionCriteria.InspectionCriterion> target, IEnumerable<PartFlipMappingInput> mappings)
    {
        var all = mappings.ToList();
        return source.Count > 0 && source.Count == target.Count && all.Count == source.Count
            && all.Select(x => x.SourceCriterionId).Distinct().Count() == source.Count
            && all.Select(x => x.TargetCriterionId).Distinct().Count() == target.Count
            && all.All(x => source.SingleOrDefault(c => c.Id == x.SourceCriterionId) is { } s && target.SingleOrDefault(c => c.Id == x.TargetCriterionId) is { } t && UnitsMatch(s.Unit, t.Unit));
    }

    internal static bool UnitsMatch(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static PartFlipCriterionOption ToOption(InspectionCriteria.InspectionCriterion c) =>
        new(c.Id, c.Name, c.Unit, c.Minimum, c.MaximumOrTolerance);
    internal static Task<List<InspectionCriteria.InspectionCriterion>> CurrentCriteriaAsync(AppDbContext db, long partId, CancellationToken ct) => db.InspectionCriteria.AsNoTracking().Where(x => x.Revision.PartId == partId && x.Revision.PublishedAtUtc != null && x.Revision.SupersededAtUtc == null).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
}
