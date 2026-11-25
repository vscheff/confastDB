using System.Diagnostics;
using System.Security.Cryptography;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Chat;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PdfSharp.Pdf;

namespace Confast.Web.Tests;

public sealed partial class DeploymentSecurityTests
{
    [IsolatedRehearsalFact]
    public async Task BackupNotificationMigrationPreservesSubscriptionsAndCooldowns()
    {
        var name = "confast_backup_notice_test_" + Guid.NewGuid().ToString("N");
        await Sql(MaintenanceConnection, $"CREATE DATABASE {name} TEMPLATE template0");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(MaintenanceConnection) { Database = name };
            await using var db = new Factory(builder.ConnectionString).CreateDbContext();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261124145642_AddBackupManager");
            var deployment = Guid.NewGuid();
            var nextDelivery = DateTimeOffset.FromUnixTimeSeconds(1800000000);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public.backup_settings SET deployment_id = {deployment},
                    notices = {(int)(BackupNotice.Failure | BackupNotice.Success)},
                    recipients = ARRAY['one@example.test', 'two@example.test'] WHERE id = 1;
                INSERT INTO public.backup_alerts (deployment_id, kind, message, last_notified_message, next_delivery_at_utc)
                    VALUES ({deployment}, {(int)BackupNotice.Failure}, 'failed', 'failed', {nextDelivery});
                """);
            await migrator.MigrateAsync();
            var settings = await db.BackupSettings.Include(x => x.Recipients).SingleAsync();
            Assert.Equal(new[] { "one@example.test", "two@example.test" }, settings.Recipients.Select(x => x.Email).Order());
            Assert.All(settings.Recipients, x => Assert.Equal(BackupNotice.Failure | BackupNotice.Success, x.Notices));
            var alerts = await db.BackupAlerts.ToListAsync();
            Assert.Equal(2, alerts.Count);
            Assert.All(alerts, x =>
            {
                Assert.Equal("failed", x.LastNotifiedMessage);
                Assert.Equal(nextDelivery, x.NextDeliveryAtUtc);
            });
        }
        finally { await Sql(MaintenanceConnection, $"DROP DATABASE {name} WITH (FORCE)"); }
    }

    [IsolatedRehearsalFact]
    public async Task BackupWorkerProcessTerminationLeavesAnInterruptedRunAndPreservesThePreviousBackup()
    {
        using var files = new BackupTestHost();
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        files.SetClientTools(builder.Username!, builder.Password);
        var (manager, processor, _, _) = files.Services(database, database.ConnectionString, session);
        await manager.SaveSettingsAsync(new(Guid.Empty, "primary", false, true, false,
            1, 0, [], null, null, files.Clock.GetUtcNow()));
        var first = await manager.RequestBackupAsync(false, "Preserved", null);
        await processor.RunOnceAsync(default);
        var completed = (await manager.GetDetailsAsync(first)).Execution;
        Assert.Equal(BackupOutcome.Completed, completed.Outcome);
        Assert.Equal(BackupVerification.ArchiveListed, completed.Verification);
        var second = await manager.RequestBackupAsync(false, "Interrupted", null);
        await using var blocker = database.CreateDbContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE public.customers IN ACCESS EXCLUSIVE MODE");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.ContentRoot,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Confast.Web.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ConnectionStrings__Confast"] = database.ConnectionString;
        start.Environment["BootstrapAdmin__Username"] = "";
        start.Environment["BootstrapAdmin__Password"] = "";
        start.Environment["BrowserTestUser__Username"] = "";
        start.Environment["BrowserTestUser__Password"] = "";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        files.ConfigureProcess(start);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var reachedDump = false;
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(45))
            {
                if (await Scalar<bool>(database.ConnectionString, """
                    SELECT EXISTS (SELECT FROM pg_stat_activity WHERE datname = current_database()
                        AND application_name = 'pg_dump' AND wait_event_type = 'Lock')
                    """))
                {
                    reachedDump = true;
                    break;
                }
                await Task.Delay(200);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            await transaction.RollbackAsync();
        }
        var interrupted = (await manager.GetDetailsAsync(second)).Execution;
        Assert.True(reachedDump, $"The native pg_dump lock was not observed. Last stage was {interrupted.Stage}. {interrupted.Diagnostic}");
        await processor.RunOnceAsync(default);
        Assert.Equal(BackupOutcome.Interrupted, (await manager.GetDetailsAsync(second)).Execution.Outcome);
        var primary = files.Storage.RequireLocation("primary");
        Assert.True(Directory.Exists(files.Storage.ReadSet(primary, completed.DirectoryName!).Path));
        Assert.Single(Directory.GetDirectories(primary.Path, ".stage_*"));
        Assert.Single(Directory.GetDirectories(primary.Path, "confastDB_*"));
    }

    [IsolatedRehearsalFact]
    public async Task BackupManagerRestoresDocumentsAndReconstructsNewerSetsFromAnOlderDatabase()
    {
        using var files = new BackupTestHost();
        var token = Guid.NewGuid().ToString("N");
        var runtimeRole = "confast_backup_runtime_" + token;
        var backupRole = "confast_backup_reader_" + token;
        var restoredName = "confast_backup_restore_test_" + token;
        var restored = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Database = restoredName, Pooling = false }.ConnectionString;
        var runtime = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Username = runtimeRole, Pooling = false }.ConnectionString;
        var tolerance = new NominalToleranceSettingsService(database, session.Guard);
        var priorTolerance = await tolerance.GetAsync();
        await Sql(database.ConnectionString, $"CREATE ROLE {runtimeRole} LOGIN NOINHERIT NOSUPERUSER; CREATE ROLE {backupRole} LOGIN NOINHERIT NOSUPERUSER");
        var targetCreated = false;
        try
        {
            await Psql(database.ConnectionString, "runtime-grants.sql", ("database", "confast_phase3g_test"), ("runtime_role", runtimeRole));
            files.SetClientTools(backupRole);
            var fixture = await SeedBackupDocumentsAsync(files);
            await Sql(database.ConnectionString, """
                CREATE TABLE public.backup_rls_fixture (id integer PRIMARY KEY);
                INSERT INTO public.backup_rls_fixture VALUES (42);
                ALTER TABLE public.backup_rls_fixture ENABLE ROW LEVEL SECURITY;
                """);
            var oid = await Scalar<uint>(database.ConnectionString, "SELECT lo_from_bytea(0, decode('c0ffee', 'hex'))");
            await Psql(database.ConnectionString, "backup-grants.sql", ("database", "confast_phase3g_test"), ("backup_role", backupRole));
            Assert.False(await Scalar<bool>(database.ConnectionString, $"SELECT rolsuper FROM pg_roles WHERE rolname = '{backupRole}'"));

            var (manager, processor, _, _) = files.Services(new Factory(runtime), runtime, session);
            await SavePolicyAsync(manager, retain: 1);
            var denied = await manager.RequestBackupAsync(false, "RLS must fail closed", null);
            await processor.RunOnceAsync(default);
            var failure = (await manager.GetDetailsAsync(denied)).Execution;
            Assert.Equal(BackupOutcome.Failed, failure.Outcome);
            Assert.Contains("cannot read all required data", failure.Diagnostic);
            Assert.Empty(Directory.GetDirectories(files.Primary.Path!));
            // The current application has no RLS tables. This artificial case
            // proves the restricted login needs explicitly reviewed RLS authority.
            await Sql(database.ConnectionString, $"ALTER ROLE {backupRole} BYPASSRLS");

            var original = await manager.RequestBackupAsync(false, "Old snapshot", "Restore this backup");
            await processor.RunOnceAsync(default);
            var originalRow = (await manager.GetDetailsAsync(original)).Execution;
            Assert.Equal(BackupOutcome.Completed, originalRow.Outcome);
            Assert.Equal(BackupVerification.Verified, originalRow.Verification);
            var originalSet = files.Storage.ReadSet(files.Storage.RequireLocation("primary"), originalRow.DirectoryName!);
            Assert.Equal(new[] { BackupStorage.ArchiveName, BackupStorage.ArtworkName }, originalSet.Manifest.Files.Select(x => x.Name));
            await BackupCatalog.VerifyChecksumsAsync(originalSet, default);
            Assert.Equal(fixture.ArtworkSha, originalSet.Manifest.Files.Single(x => x.Name == BackupStorage.ArtworkName).Sha256);

            await manager.SetPinnedAsync(original, originalRow.MetadataVersion, true, false);
            await SavePolicyAsync(manager, retain: 5);
            files.Clock.Advance(TimeSpan.FromMinutes(1));
            var newer = await manager.RequestBackupAsync(false, "Newer rolling set", null);
            await processor.RunOnceAsync(default);
            Assert.Equal(BackupOutcome.Completed, (await manager.GetDetailsAsync(newer)).Execution.Outcome);
            files.Clock.Advance(TimeSpan.FromMinutes(1));
            var incomplete = Directory.CreateDirectory(Path.Combine(files.Primary.Path!, ".stage_rehearsal"));
            var pinnedNewer = await manager.RequestBackupAsync(true, "Pinned from inception", null);
            await processor.RunOnceAsync(default);
            var pinnedResult = (await manager.GetDetailsAsync(pinnedNewer)).Execution;
            Assert.True(pinnedResult.Pinned);
            Assert.Equal(BackupOutcome.Completed, pinnedResult.Outcome);
            Assert.Contains("retention was skipped", pinnedResult.RetentionDiagnostic);
            Assert.True(incomplete.Exists);
            incomplete.Delete();
            await manager.RequestVerificationAsync(newer);
            await processor.RunOnceAsync(default);
            Assert.Contains((await manager.GetDetailsAsync(newer)).Events, x => x.Action == "VerificationPassed");

            await Sql(MaintenanceConnection, $"CREATE DATABASE {restoredName} TEMPLATE template0");
            targetCreated = true;
            await RunPostgres("pg_restore", restored, ["--exit-on-error", "--single-transaction", "--no-owner", "--no-privileges",
                "--dbname", restoredName, Path.Combine(originalSet.Path, BackupStorage.ArchiveName)], includeDatabase: false);
            var restoredApplication = files.NewDirectory("restored-app");
            var restoredAppData = Directory.CreateDirectory(Path.Combine(restoredApplication, "App_Data")).FullName;
            var restoredArtwork = Path.Combine(restoredAppData, BackupStorage.ArtworkName);
            File.Copy(Path.Combine(originalSet.Path, BackupStorage.ArtworkName), restoredArtwork);
            Assert.Equal(fixture.ArtworkSha, Hash(await File.ReadAllBytesAsync(restoredArtwork)));
            Assert.Equal("c0ffee", await Scalar<string>(restored, $"SELECT encode(lo_get({oid}), 'hex')"));
            Assert.Equal(42, await Scalar<int>(restored, "SELECT id FROM public.backup_rls_fixture"));
            var restoredFactory = new Factory(restored);
            using var recoveredIdentity = new AuthorizationTestSession(restoredFactory);
            await recoveredIdentity.SignInAsync(root);
            await VerifyRestoredLoginAsync(restored);
            await AssertRestoredDocumentsAsync(restoredFactory, recoveredIdentity, fixture);

            var (recovered, recoveryProcessor, _, _) = files.Services(restoredFactory, restored, recoveredIdentity);
            Assert.Equal(1, (await recovered.GetOverviewAsync()).Settings.Retain);
            await recoveryProcessor.RunOnceAsync(default, forceReconcile: true);
            Assert.True((await recovered.GetDetailsAsync(original)).Execution.Pinned);
            Assert.True((await recovered.GetDetailsAsync(pinnedNewer)).Execution.Pinned);
            Assert.Equal(BackupAvailability.Available, (await recovered.GetDetailsAsync(newer)).Execution.Availability);
            Assert.True(files.Storage.ReadPolicy()!.RecoveryHold);

            // A new, valid backup is possible even while stale retention policy is fenced off.
            await GrantBackupReadsAsync(restored, backupRole, oid);
            files.Clock.Advance(TimeSpan.FromMinutes(1));
            var recoveredRun = await recovered.RequestBackupAsync(false, "After restore", null);
            await recoveryProcessor.RunOnceAsync(default);
            var recoveredResult = (await recovered.GetDetailsAsync(recoveredRun)).Execution;
            Assert.Equal(BackupOutcome.Completed, recoveredResult.Outcome);
            Assert.Contains("retention is paused", recoveredResult.RetentionDiagnostic);
            Assert.True(Directory.Exists(originalSet.Path));
            Assert.Equal(4, (await recovered.GetHistoryAsync(new(Outcome: BackupOutcome.Completed))).Records.Count);
            Assert.Equal(BackupAvailability.Available, (await recovered.GetDetailsAsync(newer)).Execution.Availability);

            var invalid = await files.WriteSetAsync((await recovered.GetOverviewAsync()).Settings.Revision, false);
            await recovered.RequestReconciliationAsync();
            await recoveryProcessor.RunOnceAsync(default);
            await recovered.RequestVerificationAsync(invalid.Manifest.Id);
            await recoveryProcessor.RunOnceAsync(default);
            var invalidRow = (await recovered.GetDetailsAsync(invalid.Manifest.Id)).Execution;
            Assert.Equal(BackupVerification.Failed, invalidRow.Verification);
            Assert.Contains("pg_restore exited", invalidRow.VerificationDiagnostic);
        }
        finally
        {
            if (targetCreated) await Sql(MaintenanceConnection, $"DROP DATABASE {restoredName} WITH (FORCE)");
            await Sql(MaintenanceConnection, $"""
                DROP TABLE IF EXISTS public.backup_rls_fixture;
                SELECT lo_unlink(oid) FROM pg_largeobject_metadata
                  WHERE EXISTS (SELECT 1 FROM aclexplode(lomacl) WHERE grantee = '{backupRole}'::regrole);
                DROP OWNED BY {runtimeRole}, {backupRole};
                DROP ROLE {runtimeRole}, {backupRole};
                """);
            var currentTolerance = await tolerance.GetAsync();
            if (priorTolerance.Version is null)
                await Sql(database.ConnectionString, "DELETE FROM public.nominal_tolerance_settings WHERE id = 1");
            else
            {
                currentTolerance.ToleranceFloor = priorTolerance.ToleranceFloor;
                currentTolerance.LargeDimensionDivisor = priorTolerance.LargeDimensionDivisor;
                await tolerance.SaveAsync(currentTolerance);
            }
        }

        async Task SavePolicyAsync(BackupManager manager, int retain)
        {
            var previous = await manager.GetOverviewAsync();
            await manager.SaveSettingsAsync(new(previous.Settings.Revision, "primary", false, true, true, retain, 0,
                [], null, null, files.Clock.GetUtcNow()));
        }

        static Task GrantBackupReadsAsync(string connection, string backupRole, uint oid) =>
            Sql(connection, $"""
                GRANT USAGE ON SCHEMA public TO {backupRole};
                GRANT SELECT ON ALL TABLES IN SCHEMA public TO {backupRole};
                GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO {backupRole};
                GRANT USAGE ON SCHEMA confast_maintenance TO {backupRole};
                GRANT SELECT ON confast_maintenance.factory_resets TO {backupRole};
                GRANT SELECT ON LARGE OBJECT {oid} TO {backupRole};
                """);
    }

    private sealed record RecoveryDocuments(long PartId, long RevisionId, long InspectionId,
        string PrintSha, Dictionary<string, string> CertificationShas, long ConversationId, long MessageId,
        long AttachmentId, string AttachmentSha, string PictureSha, string ArtworkSha);

    private async Task<RecoveryDocuments> SeedBackupDocumentsAsync(BackupTestHost files)
    {
        var customers = new CustomerService(database, session.Guard);
        var customerId = await customers.CreateCustomerAsync(new() { Name = "Backup Rehearsal Customer" });
        var plantId = Assert.Single(await customers.GetPlantsAsync(customerId)).Id;
        var parts = new PartService(database, session.Guard);
        var partId = (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "BACKUP-SAMPLE", PlantIds = [plantId] })).Id!.Value;
        var gages = new GageService(database, session.Guard);
        await gages.SaveGageTypeAsync(new() { Name = "Backup Rehearsal Caliper" });
        var gage = Assert.Single(await gages.GetGageTypesAsync()).Id;
        var criteria = new InspectionCriteriaService(database, session.Guard);
        var revisionId = (await criteria.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await criteria.AddCriterionAsync(partId, revisionId, new() { Name = "Length", GageTypeId = gage, Minimum = "1", MaximumOrTolerance = "2" });
        var print = Pdf("Master print for Backup Rehearsal");
        var revision = (await criteria.GetRevisionAsync(partId, revisionId))!;
        Assert.Equal(CriteriaOperationStatus.Succeeded,
            (await criteria.UploadMasterPrintAsync(partId, revisionId, "master.pdf", print, revision.Version)).Status);
        revision = (await criteria.GetRevisionAsync(partId, revisionId))!;
        Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.PublishRevisionAsync(partId, revisionId, revision.Version)).Status);
        var inspections = session.Inspections();
        var inspectionId = (await inspections.CreateInspectionAsync(new()
        {
            PartId = partId, LotNumber = "BACKUP-LOT-1", QuantityReceived = 47, InspectionDate = new(2026, 11, 23)
        })).InspectionId!.Value;
        var certHashes = new Dictionary<string, string>();
        await using var db = database.CreateDbContext();
        foreach (var type in new[] { "Material", "Heat", "C of C" })
        {
            var original = Pdf($"{type} certificate for BACKUP-LOT-1");
            var cert = await db.CertificationTypes.SingleAsync(x => x.Name == type);
            var name = type + ".pdf";
            Assert.Equal(InspectionOperationStatus.Succeeded,
                (await inspections.UploadCertificationDocumentAsync(inspectionId, cert.Id, name, original)).Status);
            certHashes[name] = Hash(original);
        }
        var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
        var chat = new ChatService(database, session.Guard, session.Evaluator, new HttpContextAccessor(), notifications, TimeProvider.System);
        var conversation = await chat.OpenDirectAsync(root);
        var attachment = "Checked and accepted at receiving."u8.ToArray();
        var message = await chat.SendAsync(conversation, "Inspection notes attachment", attachmentFileName: "receiving.txt", attachmentContent: attachment);
        var attachmentId = await db.ChatAttachments.Where(x => x.MessageId == message).Select(x => x.Id).SingleAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/f2sAAAAASUVORK5CYII=");
        await new UserProfilePictureService(database, session.Guard, new HttpContextAccessor(), notifications).SaveAsync(png);
        var tolerance = new NominalToleranceSettingsService(database, session.Guard);
        var model = await tolerance.GetAsync();
        model.ToleranceFloor = 0.193m;
        await tolerance.SaveAsync(model);

        var appData = Directory.CreateDirectory(Path.Combine(files.ContentRoot, "App_Data")).FullName;
        var artwork = """{"Hello":"https://media.giphy.com/media/example/giphy.gif"}"""u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(appData, BackupStorage.ArtworkName), artwork);
        await File.WriteAllTextAsync(Path.Combine(appData, "infrastructure-secret.txt"), "Synthetic secret excluded from backup scope.");
        return new(partId, revisionId, inspectionId, Hash(print), certHashes, conversation, message,
            attachmentId, Hash(attachment), Hash(png), Hash(artwork));
    }

    private async Task AssertRestoredDocumentsAsync(Factory factory, AuthorizationTestSession identity, RecoveryDocuments fixture)
    {
        var criteria = new InspectionCriteriaService(factory, identity.Guard);
        Assert.Equal(fixture.PrintSha, Hash((await criteria.GetMasterPrintAsync(fixture.PartId, fixture.RevisionId))!.Content));
        var inspections = identity.Inspections();
        Assert.Equal("BACKUP-LOT-1", (await inspections.GetInspectionAsync(fixture.InspectionId))!.LotNumber);
        await using var db = factory.CreateDbContext();
        var inspection = await db.Inspections.SingleAsync(x => x.Id == fixture.InspectionId);
        Assert.Equal(fixture.RevisionId, inspection.InspectionCriteriaRevisionId);
        Assert.Equal(fixture.PartId, inspection.PartId);
        var docs = await db.CertificationDocuments.Where(x => x.InspectionCertification.InspectionId == fixture.InspectionId)
            .ToListAsync();
        Assert.Equal(fixture.CertificationShas.Count, docs.Count);
        foreach (var doc in docs)
            Assert.Equal(fixture.CertificationShas[doc.OriginalFileName],
                Hash((await inspections.GetCertificationDocumentAsync(fixture.InspectionId, doc.Id))!.Content));
        var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
        var chat = new ChatService(factory, identity.Guard, identity.Evaluator, new HttpContextAccessor(), notifications, TimeProvider.System);
        var thread = await chat.GetThreadAsync(fixture.ConversationId);
        Assert.Contains(thread!.Messages, x => x.Id == fixture.MessageId && x.Attachments.Any(a => a.Id == fixture.AttachmentId));
        Assert.Equal(fixture.AttachmentSha, Hash((await chat.GetAttachmentAsync(fixture.AttachmentId))!.Content));
        var picture = new UserProfilePictureService(factory, identity.Guard, new HttpContextAccessor(), notifications);
        Assert.Equal(fixture.PictureSha, Hash((await picture.GetAsync(root))!.Value.Data));
        Assert.Equal(0.193m, (await new NominalToleranceSettingsService(factory, identity.Guard).GetAsync()).ToleranceFloor);
        Assert.True((await identity.Evaluator.EvaluateCurrentAsync()).IsRoot);
        Assert.True(await db.AuthorizationChangeHistory.AnyAsync());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Pdf(string title)
    {
        using var document = new PdfDocument();
        document.Info.Title = title;
        document.AddPage();
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
