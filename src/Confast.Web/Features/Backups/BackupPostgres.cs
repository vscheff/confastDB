using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Confast.Web.Features.Backups;

internal sealed record BackupDatabase(string Name, string Version, int Major, long Bytes);
internal sealed record BackupUtilities(string DumpPath, string RestorePath, string DumpVersion);

internal sealed partial class BackupPostgres(IConfiguration configuration, IOptions<BackupHostOptions> options)
{
    private NpgsqlConnectionStringBuilder Connection => new(configuration.GetConnectionString("Confast"));

    internal async Task<BackupDatabase> DescribeAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Connection.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT current_database(), current_setting('server_version'),
                   current_setting('server_version_num')::integer, pg_database_size(current_database())
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) / 10000, reader.GetInt64(3));
    }

    internal async Task<BackupUtilities> CheckUtilitiesAsync(int serverMajor, CancellationToken ct)
    {
        var dump = Tool("pg_dump");
        var restore = Tool("pg_restore");
        var dumpVersion = await VersionAsync(dump, ct);
        var restoreVersion = await VersionAsync(restore, ct);
        if (Major(dumpVersion) < serverMajor || Major(restoreVersion) < Major(dumpVersion))
            throw new BackupException("ClientVersion", "Use PostgreSQL client tools compatible with the database and dump format.");
        return new(dump, restore, dumpVersion);
    }

    internal async Task DumpAsync(BackupUtilities tools, string path, CancellationToken ct)
    {
        var builder = Connection;
        if (string.IsNullOrWhiteSpace(builder.Host) || builder.Host.Contains(',') ||
            string.IsNullOrWhiteSpace(builder.Database) || string.IsNullOrWhiteSpace(options.Value.UserName) ||
            !Path.IsPathFullyQualified(options.Value.PassFile ?? "") || !File.Exists(options.Value.PassFile))
            throw new BackupException("ClientAuthentication", "Configure the backup login and its private PostgreSQL password file.");

        // Export a snapshot from the application's database connection. pg_dump has
        // to join this snapshot, so a misrouted connection cannot back up an unrelated database.
        await using var source = new NpgsqlConnection(builder.ConnectionString);
        await source.OpenAsync(ct);
        await using var transaction = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", source, transaction);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_foreign_table)";
        if (await command.ExecuteScalarAsync(ct) is true)
            throw new BackupException("ForeignTables", "The database contains foreign tables whose external contents cannot be captured.");
        command.CommandText = "SELECT pg_export_snapshot()";
        var snapshot = (string)(await command.ExecuteScalarAsync(ct))!;

        var environment = new Dictionary<string, string?>
        {
            ["PGHOST"] = builder.Host,
            ["PGPORT"] = builder.Port.ToString(CultureInfo.InvariantCulture),
            ["PGDATABASE"] = builder.Database,
            ["PGUSER"] = options.Value.UserName,
            ["PGPASSFILE"] = options.Value.PassFile,
            ["PGSSLMODE"] = builder.SslMode switch
            {
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => builder.SslMode.ToString().ToLowerInvariant()
            },
            ["PGSSLROOTCERT"] = builder.RootCertificate,
            ["PGGSSENCMODE"] = "disable",
            ["PGCONNECT_TIMEOUT"] = "10"
        };
        await RunAsync(tools.DumpPath, ["--no-password", "--format=custom", "--compress=gzip:6", "--large-objects",
            "--lock-wait-timeout=30000", "--snapshot=" + snapshot, "--file=" + path], environment, captureOutput: false, ct);
        await transaction.CommitAsync(ct);

        await using var archive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (archive.Length == 0) throw new BackupException("EmptyArchive", "PostgreSQL returned an empty backup archive.");
        archive.Flush(flushToDisk: true);
    }

    internal async Task ValidateArchiveAsync(string archive, bool fullRead, CancellationToken ct, string? restoreTool = null)
    {
        var tool = restoreTool ?? Tool("pg_restore");
        await RunAsync(tool, ["--list", archive], null, captureOutput: false, ct);
        if (fullRead)
        {
            // Render to a discarded stream. No SQL is sent to any database.
            await RunAsync(tool, ["--exit-on-error", "--file=-", archive], null, captureOutput: false, ct);
        }
    }

    private string Tool(string name)
    {
        var directory = options.Value.PostgresBinDirectory;
        if (!Path.IsPathFullyQualified(directory ?? ""))
            throw new BackupException("ClientTools", "Configure an absolute path to the PostgreSQL client utilities.");
        var path = Path.Combine(directory!, OperatingSystem.IsWindows() ? name + ".exe" : name);
        if (!File.Exists(path)) throw new BackupException("ClientTools", "A required PostgreSQL client utility is missing.");
        return path;
    }

    private async Task<string> VersionAsync(string path, CancellationToken ct)
    {
        var text = await RunAsync(path, ["--version"], null, captureOutput: true, ct);
        var version = ToolVersion().Match(text);
        if (!version.Success)
            throw new BackupException("ClientVersion", "The PostgreSQL client version could not be determined.");
        return version.Groups[1].Value;
    }

    private static int Major(string version) =>
        int.Parse(version.Split('.')[0], CultureInfo.InvariantCulture);

    private async Task<string> RunAsync(string path, string[] arguments, Dictionary<string, string?>? environment,
        bool captureOutput, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(options.Value.TimeoutMinutes, 1, 1440)));
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Inherit neither the app's secrets nor ambient libpq variables that could
        // change the server, login, TLS mode, or search path.
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["LC_ALL"] = "C";
        start.Environment["PGCLIENTENCODING"] = "UTF8";
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (!string.IsNullOrEmpty(value)) start.Environment[key] = value;

        timeout.Token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new BackupException("ClientStart", "A PostgreSQL utility could not be started.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new BackupException("ClientStart", "A PostgreSQL utility could not be started.");
        }
        process.StandardInput.Close();
        var stderr = ReadBoundedAsync(process.StandardError, 8192, timeout.Token);
        Task<string>? stdout = captureOutput ? ReadBoundedAsync(process.StandardOutput, 512, timeout.Token) : null;
        var drain = stdout ?? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stderr, drain);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            try { await Task.WhenAll(stderr, drain); }
            catch (OperationCanceledException) { /* The pipe reads share this cancellation. */ }
            ct.ThrowIfCancellationRequested();
            throw new BackupException("ClientTimeout", "A PostgreSQL utility exceeded the configured timeout.");
        }
        var diagnostics = await stderr;
        if (process.ExitCode != 0)
            throw ProcessFailure(Path.GetFileNameWithoutExtension(path), process.ExitCode, diagnostics);
        return stdout is null ? "" : await stdout;
    }

    internal static BackupException ProcessFailure(string tool, int exitCode, string diagnostics)
    {
        // Store a fixed classification. Even single-line stderr can contain data,
        // target identifiers, or credentials embedded in a PostgreSQL error.
        string hint;
        if (diagnostics.Contains("row-level security", StringComparison.OrdinalIgnoreCase) ||
            diagnostics.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            hint = "The backup login cannot read all required data.";
        else if (diagnostics.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            diagnostics.Contains("no password supplied", StringComparison.OrdinalIgnoreCase))
            hint = "Check the backup login and password file.";
        else if (diagnostics.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            hint = "The destination ran out of space.";
        else if (diagnostics.Contains("snapshot identifier", StringComparison.OrdinalIgnoreCase))
            hint = "The dump connection could not join the application database snapshot.";
        else if (diagnostics.Contains("server version mismatch", StringComparison.OrdinalIgnoreCase))
            hint = "Check PostgreSQL client compatibility.";
        else hint = tool == "pg_restore" ? "The archive could not be read." :
            "Check PostgreSQL connectivity, TLS settings, tool installation, and destination access.";
        return new(tool == "pg_restore" ? "ArchiveRead" : "DumpFailed", $"{tool} exited with code {exitCode}. {hint}");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var buffer = new char[2048];
        var captured = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, ct)) != 0)
            captured.Append(buffer, 0, Math.Min(count, limit - captured.Length));
        return captured.ToString();
    }

    [GeneratedRegex(@"PostgreSQL\)\s+([0-9]{1,3}(?:\.[0-9]{1,6}){0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex ToolVersion();
}
