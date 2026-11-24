using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Confast.Web.Features.Chat;
using Microsoft.Extensions.Options;

namespace Confast.Web.Features.Backups;

internal sealed class BackupHostOptions
{
    public Guid DeploymentId { get; set; }
    public string? CoordinationDirectory { get; set; }
    public Dictionary<string, BackupDestinationOptions> Destinations { get; set; } = [];
    public string? PostgresBinDirectory { get; set; }
    public string? UserName { get; set; }
    public string? PassFile { get; set; }
    public int TimeoutMinutes { get; set; } = 120;
    public int DownloadTimeoutMinutes { get; set; } = 45;
}

internal sealed class BackupDestinationOptions
{
    public string? Path { get; set; }
    public Guid Id { get; set; }
}

internal sealed record BackupLocation(string Name, string Path, Guid Id);
internal sealed record BackupFile(string Name, long Bytes, string Sha256);

internal sealed record BackupManifest(
    int Version, Guid Id, Guid DeploymentId, Guid DestinationId, Guid SettingsRevision,
    string Database, string ApplicationVersion, string PostgresVersion, string DumpVersion,
    DateTimeOffset RequestedAtUtc, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds, BackupTrigger Trigger, DateTimeOffset? ScheduledForUtc, int Attempt,
    string? RequestedBy, bool Pinned, string? Label, string? Notes, Guid MetadataVersion,
    bool FullArchiveRead, BackupFile[] Files)
{
    internal long Bytes => Files.Sum(x => x.Bytes);
    internal string DirectoryName => BackupStorage.SetName(StartedAtUtc, Id);
}

internal sealed record BackupSet(BackupLocation Location, string Path, BackupManifest Manifest);
internal sealed record BackupPolicy(Guid DeploymentId, Guid Generation, Guid Revision,
    Guid DestinationId, int Retain, bool RecoveryHold)
{
    internal const string ReviewMessage =
        "Automatic retention is paused. Review the backup inventory and save the current configuration to reauthorize retention.";

    internal bool Allows(BackupSettings settings, Guid deployment, Guid generation) =>
        settings.DestinationId is { } location &&
        this == new BackupPolicy(deployment, generation, settings.Revision, location, settings.Retain, false);
}
internal sealed record BackupDestinationTest(string Name, long? FreeBytes);

internal sealed class BackupStorage
{
    internal const string ArchiveName = "database.dump";
    internal const string ManifestName = "manifest.json";
    internal const string ArtworkName = ChatGifCategoryBackgrounds.StorageFileName;
    internal const string SetPrefix = "confastDB_";
    internal const string StagePrefix = ".stage_";
    internal const string ExpiredPrefix = ".expired_";
    internal const string DeletedPrefix = ".deleted_";
    internal const long MaxArtworkBytes = 256 * 1024;
    private const string StoreMarker = ".confast-backup-owner";
    private const string ControlMarker = ".confast-backup-control";
    private const string ControlLock = ".confast-backup-lock";
    private const string PolicyFile = "retention-policy.json";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 12 };
    private readonly Dictionary<string, BackupLocation> locations = new(StringComparer.Ordinal);
    private readonly string? control;
    private readonly string artwork;

    public BackupStorage(IOptions<BackupHostOptions> options, IWebHostEnvironment environment, IConfiguration configuration)
    {
        DeploymentId = options.Value.DeploymentId;
        artwork = ChatGifCategoryBackgrounds.StoragePath(environment.ContentRootPath);
        if (DeploymentId == Guid.Empty) return;
        try
        {
            string ApprovedPath(string? path)
            {
                var full = CanonicalPath(path);
                if (Overlap(full, Path.GetFullPath(environment.ContentRootPath)) ||
                    environment.WebRootPath is { } web && Overlap(full, Path.GetFullPath(web)) ||
                    configuration["DataProtection:KeyRingPath"] is { Length: > 0 } keys && Overlap(full, Path.GetFullPath(keys)))
                    throw InvalidConfiguration();
                return full;
            }

            control = ApprovedPath(options.Value.CoordinationDirectory);
            foreach (var (name, destination) in options.Value.Destinations)
            {
                if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                    destination.Id == Guid.Empty || locations.Values.Any(x => x.Id == destination.Id))
                    throw InvalidConfiguration();
                var path = ApprovedPath(destination.Path);
                if (Overlap(path, control) || locations.Values.Any(x => Overlap(path, x.Path)))
                    throw InvalidConfiguration();
                locations.Add(name, new(name, path, destination.Id));
            }
            if (locations.Count == 0) throw InvalidConfiguration();
        }
        catch (Exception ex) when (ex is BackupException or ArgumentException or NotSupportedException)
        {
            locations.Clear();
            ConfigurationIssue = "Backup deployment configuration needs a private coordination directory and distinct approved destination directories.";
        }
    }

    internal Guid DeploymentId { get; }
    internal string? ConfigurationIssue { get; }
    internal bool Configured => DeploymentId != Guid.Empty && ConfigurationIssue is null;
    internal IReadOnlyList<string> Destinations => locations.Keys.Order(StringComparer.Ordinal).ToArray();

    internal BackupLocation Approved(string? name)
    {
        if (name is null || !locations.TryGetValue(name, out var location))
            throw new BackupException("UnapprovedDestination", "Select an approved server backup destination.");
        return location;
    }

    internal BackupLocation RequireLocation(string? name, Guid? expected = null)
    {
        var location = Approved(name);
        if (expected is { } id && id != location.Id)
            throw new BackupException("DestinationChanged", "The approved destination ID differs from the catalog record.");
        CheckMarker(location.Path, StoreMarker, $"{DeploymentId:D}\n{location.Id:D}");
        return location;
    }

    internal void CheckControl()
    {
        if (!Configured || control is null) throw InvalidConfiguration();
        CheckMarker(control, ControlMarker, DeploymentId.ToString("D"));
    }

    internal FileStream? TryLockControl()
    {
        CheckControl();
        var path = Path.Combine(control!, ControlLock);
        CheckPath(path, directory: false);
        try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33) { return null; }
    }

    internal BackupPolicy? ReadPolicy()
    {
        CheckControl();
        var path = Path.Combine(control!, PolicyFile);
        if (!CheckPath(path, directory: false, allowMissing: true)) return null;
        return ReadJson<BackupPolicy>(path, 4096);
    }

    internal void WritePolicy(BackupPolicy policy)
    {
        CheckControl();
        WriteJson(Path.Combine(control!, PolicyFile), policy);
    }

    internal BackupDestinationTest Test(string name)
    {
        var location = RequireLocation(name);
        var id = Guid.NewGuid().ToString("N");
        var from = Path.Combine(location.Path, $".probe_{id}");
        var to = Path.Combine(location.Path, $".probe-moved_{id}");
        var active = from;
        try
        {
            CreateDirectory(from);
            using (var file = new FileStream(Path.Combine(from, "test"), FileMode.CreateNew, FileAccess.Write))
            {
                file.WriteByte(31);
                file.Flush(flushToDisk: true);
            }
            Directory.Move(from, to);
            active = to;
            using (var read = File.OpenRead(Path.Combine(to, "test")))
                if (read.ReadByte() != 31) throw new IOException();
            File.Delete(Path.Combine(to, "test"));
            Directory.Delete(to);
            return new(location.Name, FreeBytes(location));
        }
        catch (Exception ex) when (IsFilesystemError(ex))
        {
            throw new BackupException("DestinationTest", "The destination did not pass the read, write, rename, and delete test.");
        }
        finally
        {
            try
            {
                RequireLocation(name, location.Id);
                if (Directory.Exists(active)) DeleteFiles(active, ["test"]);
            }
            catch (Exception ex) when (IsFilesystemError(ex)) { /* A failed probe is isolated under its unique name. */ }
        }
    }

    internal static long? FreeBytes(BackupLocation location)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetDiskFreeSpaceEx(location.Path + Path.DirectorySeparatorChar, out var bytes, out _, out _)
                    ? (long)Math.Min(bytes, (ulong)long.MaxValue) : null;
            var mount = DriveInfo.GetDrives().Where(drive => Within(location.Path, drive.Name))
                .MaxBy(drive => drive.Name.Length);
            return mount?.AvailableFreeSpace;
        }
        catch (Exception ex) when (IsFilesystemError(ex)) { return null; }
    }

    internal static string SetName(DateTimeOffset started, Guid id) =>
        FormattableString.Invariant($"{SetPrefix}{started.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{id:N}");

    internal static bool IsSetName(string name) =>
        name.Length == SetPrefix.Length + 16 + 1 + 32 && name.StartsWith(SetPrefix, StringComparison.Ordinal) &&
        name[SetPrefix.Length + 16] == '_' &&
        DateTime.TryParseExact(name.AsSpan(SetPrefix.Length, 16), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) && Guid.TryParseExact(name.AsSpan(SetPrefix.Length + 17), "N", out _);

    internal static string BeginSet(BackupLocation location, string name)
    {
        if (!IsSetName(name)) throw BadManifest();
        var path = Path.Combine(location.Path, StagePrefix + name);
        if (CheckPath(path, directory: true, allowMissing: true))
            throw new BackupException("StagingExists", "A staging directory already exists for this backup.");
        CreateDirectory(path);
        return path;
    }

    internal static void WriteManifest(string directory, BackupManifest manifest)
    {
        ValidateManifest(manifest, manifest.DeploymentId, manifest.DestinationId, manifest.DirectoryName);
        CheckPath(directory, directory: true);
        WriteJson(Path.Combine(directory, ManifestName), manifest);
    }

    internal BackupSet ReadSet(BackupLocation location, string name, string prefix = "")
    {
        if (!IsSetName(name)) throw BadManifest();
        var path = Path.Combine(location.Path, prefix + name);
        if (!CheckPath(path, directory: true, allowMissing: true))
            throw new BackupException("MissingSet", "The backup directory is missing from the destination.");
        var manifestPath = Path.Combine(path, ManifestName);
        if (!CheckPath(manifestPath, directory: false, allowMissing: true)) throw BadManifest();
        var manifest = ReadJson<BackupManifest>(manifestPath, 64 * 1024);
        ValidateManifest(manifest, DeploymentId, location.Id, name);
        return new(location, path, manifest);
    }

    internal static void CheckSetFiles(BackupSet set, bool requireComplete = true)
    {
        var expected = set.Manifest.Files.Select(x => x.Name).Append(ManifestName).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Directory.EnumerateFileSystemEntries(set.Path))
        {
            if (!expected.Remove(Path.GetFileName(item))) throw BadManifest();
            CheckPath(item, directory: false);
        }
        if (!requireComplete) return;
        if (expected.Count != 0 || set.Manifest.Files.Any(x => new FileInfo(Path.Combine(set.Path, x.Name)).Length != x.Bytes))
            throw new BackupException("FileSize", "A required backup file is missing or its size differs from the manifest.");
    }

    internal static async Task<BackupFile> FingerprintAsync(string directory, string name, CancellationToken ct)
    {
        var path = Path.Combine(directory, name);
        CheckPath(path, directory: false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new BackupException("EmptyFile", "A required backup file is empty.");
        return new(name, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct)));
    }

    internal async Task<bool> CopyArtworkAsync(string directory, CancellationToken ct)
    {
        if (!CheckPath(artwork, directory: false, allowMissing: true)) return false;
        await using var source = new FileStream(artwork, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is <= 0 or > MaxArtworkBytes)
            throw new BackupException("Artwork", "The application artwork map has an unexpected size.");
        await using var target = new FileStream(Path.Combine(directory, ArtworkName), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize: 8192, FileOptions.Asynchronous);
        await source.CopyToAsync(target, ct);
        await target.FlushAsync(ct);
        target.Flush(flushToDisk: true);
        return true;
    }

    internal BackupSet Publish(BackupLocation location, string name)
    {
        RequireLocation(location.Name, location.Id);
        var staging = ReadSet(location, name, StagePrefix);
        CheckSetFiles(staging);
        var final = Path.Combine(location.Path, name);
        Directory.Move(staging.Path, final);
        return staging with { Path = final };
    }

    internal static string Retire(BackupSet set, bool automatic)
    {
        CheckSetFiles(set, requireComplete: automatic);
        var retired = Path.Combine(set.Location.Path, (automatic ? ExpiredPrefix : DeletedPrefix) + set.Manifest.DirectoryName);
        Directory.Move(set.Path, retired);
        return retired;
    }

    internal static void DeleteFiles(string path, IReadOnlyCollection<string> allowed)
    {
        CheckPath(path, directory: true);
        var files = Directory.GetFileSystemEntries(path);
        foreach (var file in files)
        {
            if (!allowed.Contains(Path.GetFileName(file))) throw BadManifest();
            CheckPath(file, directory: false);
        }
        foreach (var file in files.OrderBy(x => Path.GetFileName(x) == ManifestName)) File.Delete(file);
        Directory.Delete(path);
    }

    internal static bool IsFilesystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or BackupException;

    private static void CheckMarker(string root, string marker, string contents)
    {
        try
        {
            var path = Path.Combine(root, marker);
            CheckPath(path, directory: false);
            if (new FileInfo(path).Length > 200 ||
                !string.Equals(File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n'), contents, StringComparison.Ordinal))
                throw new IOException();
        }
        catch (Exception ex) when (IsFilesystemError(ex))
        {
            throw new BackupException("StorageIdentity", "Storage is unavailable or its provisioned identity does not match.");
        }
    }

    private static void ValidateManifest(BackupManifest manifest, Guid deployment, Guid destination, string name)
    {
        if (manifest.Version != 1 || manifest.Id == Guid.Empty || manifest.MetadataVersion == Guid.Empty ||
            manifest.DeploymentId != deployment || manifest.DestinationId != destination || manifest.DirectoryName != name ||
            manifest.RequestedAtUtc == default || manifest.RequestedAtUtc.Offset != TimeSpan.Zero ||
            manifest.StartedAtUtc == default || manifest.StartedAtUtc.Offset != TimeSpan.Zero ||
            manifest.CompletedAtUtc < manifest.StartedAtUtc || manifest.CompletedAtUtc.Offset != TimeSpan.Zero ||
            manifest.DurationMilliseconds < 0 || manifest.Label?.Length > 160 || manifest.Notes?.Length > 2000 ||
            manifest.RequestedBy?.Length > 450 || manifest.Database is not { Length: > 0 and <= 128 } ||
            manifest.ApplicationVersion is not { Length: > 0 and <= 128 } ||
            manifest.PostgresVersion is not { Length: > 0 and <= 128 } ||
            manifest.DumpVersion is not { Length: > 0 and <= 32 } ||
            !Enum.IsDefined(manifest.Trigger) ||
            (manifest.Trigger == BackupTrigger.Manual
                ? manifest.Attempt != 0 || manifest.ScheduledForUtc is not null
                : manifest.Attempt is < 1 or > 2 || manifest.ScheduledForUtc is null || manifest.ScheduledForUtc.Value.Offset != TimeSpan.Zero) ||
            manifest.Files is not { Length: >= 1 and <= 2 } ||
            manifest.Files.Any(x => x is null || x.Name is not (ArchiveName or ArtworkName) ||
                x.Bytes <= 0 || x.Bytes > long.MaxValue / 2 || x.Name == ArtworkName && x.Bytes > MaxArtworkBytes ||
                x.Sha256 is not { Length: 64 } || !x.Sha256.All(char.IsAsciiHexDigit)) ||
            manifest.Files.Count(x => x.Name == ArchiveName) != 1)
            throw BadManifest();
    }

    private static T ReadJson<T>(string path, long maxBytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is 0 || file.Length > maxBytes) throw BadManifest();
        try { return JsonSerializer.Deserialize<T>(file, Json) ?? throw BadManifest(); }
        catch (JsonException) { throw BadManifest(); }
    }

    private static void WriteJson<T>(string path, T value)
    {
        CheckPath(path, directory: false, allowMissing: true);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // The application service identity and the storage operator are the only writer
    // principals. Reject links at every component, including the provisioned roots.
    private static bool CheckPath(string path, bool directory, bool allowMissing = false)
    {
        var root = Path.GetPathRoot(path)!;
        var components = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        // Windows does not expose attributes for the bare UNC share. Check its
        // first real directory and each descendant; the share itself is provisioned.
        for (var i = OperatingSystem.IsWindows() && root.StartsWith(@"\\", StringComparison.Ordinal) ? 0 : -1;
             i < components.Length; i++)
        {
            if (i >= 0) current = Path.Combine(current, components[i]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception ex) when (allowMissing && ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
            var expectDirectory = i < components.Length - 1 || directory;
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != expectDirectory)
                throw new BackupException("UnsafePath", "A backup path has an unsafe filesystem entry.");
        }
        return true;
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string CanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw InvalidConfiguration();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        if (full.Equals(root, PathComparison) || OperatingSystem.IsWindows() &&
            (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ||
             full[root.Length..].Split('\\').Any(c => c.Contains(':') || c.EndsWith(' ') || c.EndsWith('.'))))
            throw InvalidConfiguration();
        return full;
    }

    private static bool Overlap(string left, string right) => Within(left, right) || Within(right, left);

    private static bool Within(string path, string parent) =>
        path.Equals(parent, PathComparison) || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent :
            parent + Path.DirectorySeparatorChar, PathComparison);

    private static BackupException InvalidConfiguration() =>
        new("StorageConfiguration", "Backup storage needs deployment configuration before it can be used.");

    private static BackupException BadManifest() =>
        new("Manifest", "The backup manifest or directory structure is invalid or unrecognized.");

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
