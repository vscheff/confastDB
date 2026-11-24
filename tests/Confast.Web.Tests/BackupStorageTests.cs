using System.Diagnostics;
using Confast.Web.Features.Backups;

namespace Confast.Web.Tests;

public sealed class BackupStorageTests
{
    [Fact]
    public void DestinationTestsDoNotRecreateARemovedMarkerOrTouchExistingFiles()
    {
        using var host = new BackupTestHost();
        var unrelated = Path.Combine(host.Primary.Path!, "operator-owned.txt");
        File.WriteAllText(unrelated, "keep");
        Assert.Equal("primary", host.Storage.Test("primary").Name);
        Assert.False(Directory.GetDirectories(host.Primary.Path!).Any());
        var marker = Path.Combine(host.Primary.Path!, ".confast-backup-owner");
        File.Delete(marker);
        Assert.Equal("StorageIdentity", Assert.Throws<BackupException>(() => host.Storage.Test("primary")).Code);
        Assert.False(File.Exists(marker));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Theory]
    [InlineData("web")]
    [InlineData("recursive")]
    [InlineData("traversal")]
    public void RejectsUnsafeDeploymentPaths(string placement)
    {
        using var host = new BackupTestHost();
        host.Primary.Path = placement switch
        {
            "web" => Path.Combine(host.ContentRoot, "wwwroot", "backups"),
            "recursive" => host.Root,
            _ => Path.Combine(host.Root, "archive", "..", "primary")
        };
        Assert.False(host.BuildStorage().Configured);
        Assert.Equal("UnapprovedDestination",
            Assert.Throws<BackupException>(() => host.Storage.RequireLocation("../primary")).Code);
    }

    [Fact]
    public async Task RejectsForeignManifestsUnmanagedFilesAndArchiveTraversal()
    {
        using var host = new BackupTestHost();
        var foreign = await host.WriteSetAsync(Guid.NewGuid(), true, deploymentId: Guid.NewGuid());
        Assert.Throws<BackupException>(() => host.Storage.ReadSet(foreign.Location, foreign.Manifest.DirectoryName));
        var set = await host.WriteSetAsync(Guid.NewGuid(), false);
        var unrelated = Path.Combine(set.Path, "do-not-touch.txt");
        File.WriteAllText(unrelated, "operator");
        Assert.Throws<BackupException>(() => BackupStorage.CheckSetFiles(set, requireComplete: false));
        Assert.Throws<BackupException>(() => BackupStorage.DeleteFiles(set.Path, [BackupStorage.ArchiveName, BackupStorage.ManifestName]));
        Assert.Equal("operator", File.ReadAllText(unrelated));
        Assert.Throws<BackupException>(() => host.Storage.ReadSet(set.Location, "../" + set.Manifest.DirectoryName));
    }

    [Fact]
    public void ReparsePointCannotRedirectAnApprovedDestination()
    {
        using var host = new BackupTestHost();
        var link = Path.Combine(host.Root, "redirected");
        if (OperatingSystem.IsWindows())
        {
            // A junction does not require Windows developer mode or symlink privileges.
            using var command = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/d /c mklink /J \"{link}\" \"{host.Primary.Path}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            command.WaitForExit();
            Assert.Equal(0, command.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, host.Primary.Path!);
        try
        {
            host.Primary.Path = link;
            var storage = host.BuildStorage();
            Assert.Equal("StorageIdentity", Assert.Throws<BackupException>(() => storage.RequireLocation("primary")).Code);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ClientDiagnosticsKeepOnlyAClassificationAndExitCode()
    {
        var message = BackupPostgres.ProcessFailure("pg_dump", 18,
            """permission denied for relation customers. postgres://user:secret@example.test/private_document""");
        Assert.Contains("18", message.Message);
        Assert.Contains("cannot read", message.Message);
        Assert.DoesNotContain("secret", message.Message);
        Assert.DoesNotContain("customers", message.Message);
    }
}
