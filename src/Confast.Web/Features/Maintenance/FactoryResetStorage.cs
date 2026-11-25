using System.Text;
using System.Text.Json;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Chat;

namespace Confast.Web.Features.Maintenance;

internal sealed class FactoryResetStorage(BackupStorage backups, IWebHostEnvironment environment,
    IConfiguration configuration, TimeProvider clock)
{
    private const string Marker = ".confast-managed-storage";

    internal FileStream? TryLockStorage() => TryLockDirectory(ValidateLocations());

    // A filesystem lease also fences an artwork writer that loses its PostgreSQL
    // session while a separate instance is beginning maintenance.
    internal static FileStream? TryLockDirectory(string directory)
    {
        var path = Path.Combine(directory, ".confast-managed-files-lock");
        BackupStorage.CheckPath(path, directory: false, allowMissing: true);
        Directory.CreateDirectory(directory);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33) { return null; }
    }

    internal Guid Prepare()
    {
        var directory = ValidateLocations();
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, Marker);
        if (!BackupStorage.CheckPath(marker, directory: false, allowMissing: true))
        {
            var pending = marker + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    file.Write(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D")));
                    file.Flush(flushToDisk: true);
                }
                DurableFileMoves.Move(pending, marker);
            }
            finally { File.Delete(pending); }
        }
        return ReadId(marker);
    }

    internal void Clear(FactoryResetOperation operation)
    {
        var directory = RequireStorage(operation);
        foreach (var name in FileNames)
        {
            var source = Path.Combine(directory, name);
            var quarantine = Quarantine(source, operation.Id);
            var staged = BackupStorage.CheckPath(quarantine, directory: false, allowMissing: true);
            if (BackupStorage.CheckPath(source, directory: false, allowMissing: true))
            {
                if (staged) throw new IOException("The reset storage contains conflicting active and quarantined files.");
                DurableFileMoves.Move(source, quarantine);
                staged = true;
            }
            if (staged) File.Delete(quarantine);
        }
        DurableFileMoves.FlushDirectory(directory);
    }

    internal void VerifyEmpty(FactoryResetOperation operation)
    {
        var directory = RequireStorage(operation);
        foreach (var name in FileNames)
        {
            var source = Path.Combine(directory, name);
            if (BackupStorage.CheckPath(source, directory: false, allowMissing: true) ||
                BackupStorage.CheckPath(Quarantine(source, operation.Id), directory: false, allowMissing: true))
                throw new IOException("Managed application files remain after the reset.");
        }
    }

    internal void Log(FactoryResetOperation operation, string stage, string? result = null)
    {
        var directory = backups.ControlDirectory;
        var path = Path.Combine(directory, $"factory-reset-{operation.Id:N}.jsonl");
        BackupStorage.CheckPath(path, directory: false, allowMissing: true);
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            operation.Id, operation.ActorId, operation.ActorName, operation.RequestedAt, operation.BackupId,
            Timestamp = clock.GetUtcNow(), Stage = stage, Result = result
        });
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(json);
        file.WriteByte((byte)'\n');
        file.Flush(flushToDisk: true);
        DurableFileMoves.FlushDirectory(directory);
    }

    private string RequireStorage(FactoryResetOperation operation)
    {
        var directory = ValidateLocations();
        if (operation.StorageId != ReadId(Path.Combine(directory, Marker)))
            throw new IOException("The original managed storage volume is required to finish the reset.");
        return directory;
    }

    private string ValidateLocations()
    {
        var directory = BackupStorage.CanonicalPath(ChatGifCategoryBackgrounds.StorageDirectory(environment.ContentRootPath, configuration));
        if (BackupStorage.Overlap(directory, backups.ControlDirectory) ||
            backups.Destinations.Any(name => BackupStorage.Overlap(directory, backups.Approved(name).Path)) ||
            (configuration["DataProtection:KeyRingPath"] is { Length: > 0 } ring &&
                BackupStorage.Overlap(directory, Path.GetFullPath(ring))) ||
            (environment.WebRootPath is { } webRoot && BackupStorage.Overlap(directory, Path.GetFullPath(webRoot))))
            throw new IOException("The managed storage overlaps deployment infrastructure.");

        BackupStorage.CheckPath(directory, directory: true, allowMissing: true);
        foreach (var name in FileNames)
            BackupStorage.CheckPath(Path.Combine(directory, name), directory: false, allowMissing: true);
        return directory;
    }

    private static Guid ReadId(string marker)
    {
        BackupStorage.CheckPath(marker, directory: false);
        using var file = File.OpenRead(marker);
        Span<byte> bytes = stackalloc byte[36];
        if (file.Length != bytes.Length) throw new IOException("The managed storage marker is invalid.");
        file.ReadExactly(bytes);
        if (!Guid.TryParseExact(Encoding.ASCII.GetString(bytes), "D", out var id) || id == Guid.Empty)
            throw new IOException("The managed storage marker is invalid.");
        return id;
    }

    private static string Quarantine(string path, Guid id) => path + $".reset-{id:N}";

    private static readonly string[] FileNames = [ChatGifCategoryBackgrounds.StorageFileName,
        ChatGifCategoryBackgrounds.StorageFileName + ".tmp"];
}
