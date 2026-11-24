using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Backups;

internal sealed class BackupCatalog(BackupStorage storage, TimeProvider clock)
{
    private sealed record Observed(BackupSet Set, BackupAvailability Availability);
    private sealed class Inventory
    {
        internal Dictionary<Guid, Observed> Sets { get; } = [];
        internal HashSet<Guid> Reachable { get; } = [];
        internal HashSet<Guid> Duplicates { get; } = [];
        internal HashSet<(Guid Location, string Directory)> Unrecognized { get; } = [];
        internal List<string> Issues { get; } = [];
    }

    internal async Task ReconcileAsync(AppDbContext db, BackupSettings settings, BackupLease lease, string? actor)
    {
        var ct = lease.Token;
        var inventory = Scan(ct);
        var records = await db.BackupExecutions.Where(x => x.DeploymentId == storage.DeploymentId)
            .ToDictionaryAsync(x => x.Id, ct);
        var occurrences = records.Values.Where(x => x.ScheduledForUtc.HasValue)
            .ToDictionary(x => (x.ScheduledForUtc, x.Attempt), x => x.Id);
        var now = clock.GetUtcNow();
        var recovered = 0;
        var disagreements = 0;
        var missing = 0;
        var corrupt = 0;
        foreach (var (id, observation) in inventory.Sets)
        {
            ct.ThrowIfCancellationRequested();
            var set = observation.Set;
            var manifest = set.Manifest;
            if (observation.Availability == BackupAvailability.Corrupted &&
                (!records.TryGetValue(id, out var observedRow) || observedRow.Outcome != BackupOutcome.Completed))
                continue;
            if (manifest.ScheduledForUtc.HasValue &&
                occurrences.TryGetValue((manifest.ScheduledForUtc, manifest.Attempt), out var otherId) && otherId != id)
            {
                disagreements++;
                continue;
            }
            if (!records.TryGetValue(id, out var row))
            {
                row = new BackupExecution { Id = id, DeploymentId = manifest.DeploymentId, DestinationId = manifest.DestinationId };
                db.BackupExecutions.Add(row);
                records.Add(id, row);
                if (manifest.ScheduledForUtc.HasValue) occurrences.Add((manifest.ScheduledForUtc, manifest.Attempt), id);
            }
            else if (!MatchesIdentity(row, set) || row.IsRetired && observation.Availability != row.Availability)
            {
                disagreements++;
                if (!row.IsRetired && row.Outcome == BackupOutcome.Completed) row.Availability = BackupAvailability.Corrupted;
                continue;
            }

            if (row.Outcome != BackupOutcome.Completed)
            {
                ++recovered;
                Register(row, set);
                row.Stage = BackupStage.Finished;
                BackupMapping.Audit(db, storage.DeploymentId, now, "BackupRecovered", actor, id);
            }
            else if (!MetadataMatches(row, manifest))
            {
                ++disagreements;
                BackupMapping.Audit(db, storage.DeploymentId, now, "ManifestMetadataRecovered", actor, id,
                    Metadata(row), new { manifest.Pinned, manifest.Label, manifest.Notes });
                ApplyMetadata(row, manifest);
            }
            row.Destination = set.Location.Name;
            row.Availability = observation.Availability == BackupAvailability.Available &&
                               row.Availability == BackupAvailability.Corrupted && row.Verification == BackupVerification.Failed
                ? BackupAvailability.Corrupted : observation.Availability;
            if (row.IsRetired) row.RemovedAtUtc ??= now;
            if (row.Availability == BackupAvailability.Corrupted) ++corrupt;
        }

        foreach (var row in records.Values.Where(x => x.Outcome == BackupOutcome.Completed && !x.IsRetired))
        {
            if (inventory.Sets.ContainsKey(row.Id)) continue;
            // The absence of a store or a valid manifest is not evidence of deletion.
            if (!inventory.Reachable.Contains(row.DestinationId))
                row.Availability = BackupAvailability.Unavailable;
            else if (inventory.Duplicates.Contains(row.Id) || row.DirectoryName is { } name &&
                     inventory.Unrecognized.Contains((row.DestinationId, name)))
            {
                row.Availability = BackupAvailability.Corrupted;
                ++corrupt;
            }
            else
            {
                row.Availability = BackupAvailability.Missing;
                ++missing;
            }
        }

        if (settings.DeploymentId == storage.DeploymentId)
        {
            if (recovered + disagreements > 0)
            {
                await ProtectRecoveredInventoryAsync(lease);
                settings.RetentionIssue = BackupPolicy.ReviewMessage;
            }
            if (missing > 0) inventory.Issues.Add($"{missing} catalogued backup set(s) are missing.");
            if (corrupt > 0) inventory.Issues.Add($"{corrupt} backup set(s) have integrity or manifest problems.");
            if (recovered + disagreements > 0)
                inventory.Issues.Add($"Recovered {recovered} backup set(s) and found {disagreements} metadata or identity disagreement(s).");
        }

        var issues = inventory.Issues.Count == 0 ? null : string.Join(" ", inventory.Issues.Take(12));
        if (actor is not null || settings.LastReconciledAtUtc is null || issues != settings.CatalogIssue || recovered + disagreements > 0)
            BackupMapping.Audit(db, storage.DeploymentId, now, "CatalogReconciled", actor,
                after: new { Recovered = recovered, Missing = missing, Corrupted = corrupt, Issues = issues });
        settings.CatalogIssue = issues;
        settings.LastReconciledAtUtc = now;
        await db.SaveChangesAsync(ct);
    }

    internal async Task ApplyRetentionAsync(AppDbContext db, BackupSettings settings, BackupExecution published, BackupLease lease)
    {
        var ct = lease.Token;
        await CheckRetentionPolicyAsync(db, settings, lease);
        var inventory = Scan(ct);
        if (inventory.Issues.Count != 0)
            throw new BackupException("RetentionInventory", "Automatic retention was skipped because one or more stores need reconciliation.");
        var known = await db.BackupExecutions.Where(x => x.DeploymentId == storage.DeploymentId &&
                x.Outcome == BackupOutcome.Completed && x.Availability != BackupAvailability.Deleted &&
                x.Availability != BackupAvailability.Expired).ToDictionaryAsync(x => x.Id, ct);
        var rolling = new List<BackupExecution>();
        foreach (var (id, observation) in inventory.Sets)
        {
            var set = observation.Set;
            if (!known.TryGetValue(id, out var row) || !MatchesIdentity(row, set) || !MetadataMatches(row, set.Manifest))
            {
                await ProtectRecoveredInventoryAsync(lease);
                throw new BackupException("RetentionCatalog", "Automatic retention is paused because the catalog differs from the manifests.");
            }
            if (observation.Availability == BackupAvailability.Available && row.Availability == BackupAvailability.Available &&
                row.Verification != BackupVerification.Failed && !row.Pinned)
                rolling.Add(row);
        }

        // Keep the backup that authorized this cleanup, even if an imported set's
        // timestamp is newer (clock differences or recovery).
        var excess = rolling.Count - settings.Retain;
        foreach (var row in rolling.Where(x => x.Id != published.Id)
                     .OrderBy(x => x.FinishedAtUtc).ThenBy(x => x.Id).Take(Math.Max(0, excess)))
        {
            var set = RequireSet(row);
            try { await VerifyChecksumsAsync(set, ct); }
            catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
            {
                row.Availability = BackupAvailability.Corrupted;
                row.Verification = BackupVerification.Failed;
                row.VerifiedAtUtc = clock.GetUtcNow();
                row.VerificationDiagnostic = "A retention candidate failed the file integrity check.";
                BackupMapping.Audit(db, storage.DeploymentId, row.VerifiedAtUtc.Value, "VerificationFailed", id: row.Id,
                    after: new { row.VerificationDiagnostic });
                await db.SaveChangesAsync(ct);
                throw new BackupException("RetentionIntegrity", "Automatic retention stopped because a backup failed verification.");
            }
            await CheckRetentionPolicyAsync(db, settings, lease);
            await RemoveAsync(db, row, lease, automatic: true, actor: null);
        }
    }

    internal BackupSet RequireSet(BackupExecution row)
    {
        if (row.Outcome != BackupOutcome.Completed || row.IsRetired || row.DirectoryName is null)
            throw new BackupException("NoBackupSet", "There is no managed backup set for this execution.");
        var location = storage.RequireLocation(row.Destination, row.DestinationId);
        var set = storage.ReadSet(location, row.DirectoryName);
        if (!MatchesIdentity(row, set))
            throw new BackupException("CatalogMismatch", "The backup set conflicts with the catalog. Reconcile the catalog before managing it.");
        return set;
    }

    internal async Task RemoveAsync(AppDbContext db, BackupExecution row, BackupLease lease, bool automatic, string? actor)
    {
        await lease.CheckAsync();
        var set = RequireSet(row);
        if (set.Manifest.Pinned || row.Pinned)
            throw new BackupException("Pinned", "Unpin this backup before deleting it.");
        if (!MetadataMatches(row, set.Manifest))
            throw new BackupException("CatalogMismatch", "Reconcile the catalog before deleting this backup.");
        var retired = BackupStorage.Retire(set, automatic);
        var now = clock.GetUtcNow();
        var previous = row.Availability;
        row.Availability = automatic ? BackupAvailability.Expired : BackupAvailability.Deleted;
        row.RemovedAtUtc = now;
        BackupMapping.Audit(db, storage.DeploymentId, now, automatic ? "RetentionExpired" : "BackupDeleted", actor, row.Id,
            new { Availability = previous.ToString() }, new { Availability = row.Availability.ToString(), row.TotalBytes });
        // Commit retirement before removing any bytes. A crash leaves a recoverable,
        // correctly identified retired directory for reconciliation.
        await db.SaveChangesAsync(lease.Token);
        await lease.CheckAsync();
        try
        {
            storage.RequireLocation(set.Location.Name, set.Location.Id);
            BackupStorage.DeleteFiles(retired, set.Manifest.Files.Select(x => x.Name).Append(BackupStorage.ManifestName).ToArray());
        }
        catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
        {
            throw new BackupException("RemoveFiles", "The backup was retired, but its retired directory could not be fully removed.");
        }
    }

    internal async Task UpdateMetadataAsync(AppDbContext db, BackupExecution row, BackupLease lease, string actor,
        Guid expectedVersion, bool? pin, string? label, string? notes)
    {
        await lease.CheckAsync();
        var set = RequireSet(row);
        if (row.MetadataVersion != expectedVersion || !MetadataMatches(row, set.Manifest)) throw MetadataConflict();
        var before = Metadata(row);
        var manifest = pin.HasValue
            ? set.Manifest with { Pinned = pin.Value, MetadataVersion = Guid.NewGuid() }
            : set.Manifest with { Label = label, Notes = notes, MetadataVersion = Guid.NewGuid() };
        BackupStorage.CheckSetFiles(set, requireComplete: false);
        BackupStorage.WriteManifest(set.Path, manifest);
        ApplyMetadata(row, manifest);
        BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(),
            pin is true ? "BackupPinned" : pin is false ? "BackupUnpinned" : "BackupMetadataEdited", actor, row.Id,
            before, Metadata(row));
        await db.SaveChangesAsync(lease.Token);
    }

    internal static void Register(BackupExecution row, BackupSet set)
    {
        var manifest = set.Manifest;
        row.Destination = set.Location.Name;
        row.DirectoryName = manifest.DirectoryName;
        row.Trigger = manifest.Trigger;
        row.ScheduledForUtc = manifest.ScheduledForUtc;
        row.Attempt = manifest.Attempt;
        row.RequestedBy = manifest.RequestedBy;
        row.RequestedAtUtc = manifest.RequestedAtUtc;
        row.StartedAtUtc = manifest.StartedAtUtc;
        row.FinishedAtUtc = manifest.CompletedAtUtc;
        row.DurationMilliseconds = manifest.DurationMilliseconds;
        row.SettingsRevision = manifest.SettingsRevision;
        row.TotalBytes = manifest.Bytes;
        row.Outcome = BackupOutcome.Completed;
        row.Availability = BackupAvailability.Available;
        row.Diagnostic = null;
        row.Verification = manifest.FullArchiveRead ? BackupVerification.Verified : BackupVerification.ArchiveListed;
        row.VerifiedAtUtc = manifest.CompletedAtUtc;
        ApplyMetadata(row, manifest);
    }

    internal static async Task VerifyChecksumsAsync(BackupSet set, CancellationToken ct)
    {
        BackupStorage.CheckSetFiles(set);
        foreach (var entry in set.Manifest.Files)
        {
            var actual = await BackupStorage.FingerprintAsync(set.Path, entry.Name, ct);
            if (entry.Bytes != actual.Bytes || !string.Equals(entry.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BackupException("Checksum", "A backup file does not match the SHA-256 checksum in its manifest.");
        }
    }

    internal static BackupException MetadataConflict() =>
        new("MetadataConflict", "Backup details changed. Refresh the details before applying your changes.");

    private async Task CheckRetentionPolicyAsync(AppDbContext db, BackupSettings settings, BackupLease lease)
    {
        await lease.CheckAsync();
        var generation = await db.AuthorizationState.Select(x => x.InstallationGeneration).SingleAsync(lease.Token);
        var current = storage.ReadPolicy();
        if (current?.Allows(settings, storage.DeploymentId, generation) != true)
            throw new BackupException("RetentionPolicy", BackupPolicy.ReviewMessage);
    }

    private async Task ProtectRecoveredInventoryAsync(BackupLease lease)
    {
        await lease.CheckAsync();
        BackupPolicy? policy;
        try { policy = storage.ReadPolicy(); }
        catch (BackupException ex) when (ex.Code == "Manifest") { return; } // An unreadable policy already blocks retention.
        if (policy is { RecoveryHold: false })
            storage.WritePolicy(policy with { RecoveryHold = true });
    }

    private Inventory Scan(CancellationToken ct)
    {
        var inventory = new Inventory();
        foreach (var name in storage.Destinations)
        {
            ct.ThrowIfCancellationRequested();
            BackupLocation location;
            string[] paths;
            try
            {
                location = storage.RequireLocation(name);
                paths = Directory.GetFileSystemEntries(location.Path);
                inventory.Reachable.Add(location.Id);
            }
            catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
            {
                inventory.Issues.Add($"Destination {name} is unavailable.");
                continue;
            }

            var staging = 0;
            var invalid = 0;
            var retired = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var entry = Path.GetFileName(path);
                if (entry.StartsWith(BackupStorage.StagePrefix, StringComparison.Ordinal))
                {
                    ++staging;
                    continue;
                }
                var prefix = entry.StartsWith(BackupStorage.ExpiredPrefix, StringComparison.Ordinal) ? BackupStorage.ExpiredPrefix :
                    entry.StartsWith(BackupStorage.DeletedPrefix, StringComparison.Ordinal) ? BackupStorage.DeletedPrefix : "";
                if (prefix.Length == 0 && !entry.StartsWith(BackupStorage.SetPrefix, StringComparison.Ordinal)) continue;
                if (prefix.Length != 0) ++retired;
                BackupSet set;
                try { set = storage.ReadSet(location, entry[prefix.Length..], prefix); }
                catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
                {
                    inventory.Unrecognized.Add((location.Id, entry));
                    ++invalid;
                    continue;
                }
                var availability = BackupAvailability.Available;
                try { BackupStorage.CheckSetFiles(set, requireComplete: prefix.Length == 0); }
                catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
                {
                    ++invalid;
                    availability = BackupAvailability.Corrupted;
                }
                if (prefix.Length != 0)
                    availability = prefix == BackupStorage.ExpiredPrefix ? BackupAvailability.Expired : BackupAvailability.Deleted;
                if (inventory.Duplicates.Contains(set.Manifest.Id)) continue;
                if (!inventory.Sets.TryAdd(set.Manifest.Id, new(set, availability)))
                {
                    inventory.Sets.Remove(set.Manifest.Id);
                    inventory.Duplicates.Add(set.Manifest.Id);
                }
            }
            if (staging > 0) inventory.Issues.Add($"Destination {name} contains {staging} incomplete staging director(ies).");
            if (invalid > 0) inventory.Issues.Add($"Destination {name} contains {invalid} invalid, incomplete, or foreign backup set(s).");
            if (retired > 0) inventory.Issues.Add($"Destination {name} contains {retired} retired director(ies) requiring operator review.");
        }
        if (inventory.Duplicates.Count > 0) inventory.Issues.Add($"Found {inventory.Duplicates.Count} duplicate backup identifier(s).");
        return inventory;
    }

    private static bool MatchesIdentity(BackupExecution row, BackupSet set) =>
        row.Id == set.Manifest.Id && row.DeploymentId == set.Manifest.DeploymentId && row.DestinationId == set.Manifest.DestinationId &&
        (row.DirectoryName is null || row.DirectoryName == set.Manifest.DirectoryName && row.Trigger == set.Manifest.Trigger &&
            row.ScheduledForUtc == set.Manifest.ScheduledForUtc && row.Attempt == set.Manifest.Attempt &&
            row.SettingsRevision == set.Manifest.SettingsRevision && row.TotalBytes == set.Manifest.Bytes);

    private static bool MetadataMatches(BackupExecution row, BackupManifest manifest) =>
        row.MetadataVersion == manifest.MetadataVersion && row.Pinned == manifest.Pinned &&
        row.Label == manifest.Label && row.Notes == manifest.Notes;

    private static void ApplyMetadata(BackupExecution row, BackupManifest manifest) =>
        (row.Pinned, row.Label, row.Notes, row.MetadataVersion) =
            (manifest.Pinned, manifest.Label, manifest.Notes, manifest.MetadataVersion);

    private static object Metadata(BackupExecution row) => new { row.Pinned, row.Label, row.Notes };
}
