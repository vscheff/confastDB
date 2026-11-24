using System.Diagnostics;
using Confast.Web.Data;
using Confast.Web.Features.Backups;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confast.Web.Tests;

internal sealed class BackupTestHost : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("confast-backup-test-");
    public BackupHostOptions Options { get; }
    public BackupStorage Storage { get; }
    public BackupDestinationOptions Primary => Options.Destinations["primary"];
    public BackupDestinationOptions Archive => Options.Destinations["archive"];
    public string Root => temporary.FullName;
    public string ContentRoot { get; }
    public string Coordination { get; }
    public BackupTestClock Clock { get; } = new();

    public BackupTestHost()
    {
        ContentRoot = NewDirectory("app");
        Coordination = NewDirectory("coordination");
        Options = new()
        {
            DeploymentId = Guid.NewGuid(),
            CoordinationDirectory = Coordination,
            Destinations = new()
            {
                ["primary"] = new() { Id = Guid.NewGuid(), Path = NewDirectory("primary") },
                ["archive"] = new() { Id = Guid.NewGuid(), Path = NewDirectory("archive") }
            }
        };
        File.WriteAllText(Path.Combine(Coordination, ".confast-backup-control"), Options.DeploymentId.ToString("D"));
        File.WriteAllText(Path.Combine(Coordination, ".confast-backup-lock"), "");
        foreach (var destination in Options.Destinations.Values) WriteMarker(destination, Options.DeploymentId);
        Storage = BuildStorage();
    }

    internal BackupStorage BuildStorage() => new(Microsoft.Extensions.Options.Options.Create(Options),
        new TestEnvironment { ContentRootPath = ContentRoot, WebRootPath = Path.Combine(ContentRoot, "wwwroot") },
        new ConfigurationBuilder().Build());

    internal static void WriteMarker(BackupDestinationOptions destination, Guid deployment) =>
        File.WriteAllText(Path.Combine(destination.Path!, ".confast-backup-owner"), $"{deployment:D}\n{destination.Id:D}");

    internal void SetClientTools(string login, string? password = null)
    {
        Options.PostgresBinDirectory = Environment.GetEnvironmentVariable("CONFAST_BACKUP_TEST_PG_BIN") ??
            (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PostgreSQL", "18", "bin") : "/usr/lib/postgresql/18/bin");
        Options.UserName = login;
        Options.PassFile = Path.Combine(Root, "backup.pgpass");
        File.WriteAllText(Options.PassFile, $"*:*:*:{Escape(login)}:{Escape(password ?? "unused-for-loopback-test")}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Options.PassFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        static string Escape(string value) => value.Replace(@"\", @"\\").Replace(":", @"\:");
    }

    internal void ConfigureProcess(ProcessStartInfo start)
    {
        start.Environment["Backups__DeploymentId"] = Options.DeploymentId.ToString("D");
        start.Environment["Backups__CoordinationDirectory"] = Coordination;
        start.Environment["Backups__Destinations__primary__Id"] = Primary.Id.ToString("D");
        start.Environment["Backups__Destinations__primary__Path"] = Primary.Path!;
        start.Environment["Backups__PostgresBinDirectory"] = Options.PostgresBinDirectory!;
        start.Environment["Backups__UserName"] = Options.UserName!;
        start.Environment["Backups__PassFile"] = Options.PassFile!;
    }

    internal (BackupManager Manager, BackupProcessor Processor, BackupCoordinator Coordinator, BackupCatalog Catalog)
        Services(IDbContextFactory<AppDbContext> factory, string connectionString, AuthorizationTestSession session)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Confast"] = connectionString }).Build();
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        var coordinator = new BackupCoordinator(Storage, configuration);
        var catalog = new BackupCatalog(Storage, Clock);
        var postgres = new BackupPostgres(configuration, options);
        var health = new BackupHealth(Storage, postgres, Clock);
        return (new(factory, session.Guard, Storage, catalog, coordinator, health, Clock, options),
            new(factory, Storage, coordinator, catalog, postgres, session.Evaluator, Clock, NullLogger<BackupProcessor>.Instance),
            coordinator, catalog);
    }

    internal async Task<BackupSet> WriteSetAsync(Guid revision, bool pinned, string? destination = null,
        Guid? id = null, Guid? deploymentId = null)
    {
        var location = Storage.RequireLocation(destination ?? "primary");
        var now = Clock.GetUtcNow();
        var backupId = id ?? Guid.NewGuid();
        var staging = BackupStorage.BeginSet(location, BackupStorage.SetName(now, backupId));
        await File.WriteAllBytesAsync(Path.Combine(staging, BackupStorage.ArchiveName), "fixture archive contents"u8.ToArray());
        var file = await BackupStorage.FingerprintAsync(staging, BackupStorage.ArchiveName, default);
        var manifest = new BackupManifest(1, backupId, deploymentId ?? Options.DeploymentId, location.Id, revision,
            "confast_test", "1.0.0", "18", "18.6", now, now, now, 100, BackupTrigger.Manual, null, 0,
            null, pinned, "Fixture", null, Guid.NewGuid(), FullArchiveRead: true, [file]);
        BackupStorage.WriteManifest(staging, manifest);
        Clock.Advance(TimeSpan.FromSeconds(1));
        var path = Path.Combine(location.Path, manifest.DirectoryName);
        Directory.Move(staging, path);
        return new(location, path, manifest);
    }

    internal string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
    public void Dispose() => temporary.Delete(recursive: true);

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Confast.Web";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal sealed class BackupTestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 24, 16, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    internal void Advance(TimeSpan time) => now += time;
}
