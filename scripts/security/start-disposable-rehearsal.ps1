param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# This creates a new private cluster, never connects to the developer's PostgreSQL service.
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory for each rehearsal.' }
New-Item -ItemType Directory -Path $output | Out-Null
$data = Join-Path $output 'postgres-data'
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start(); $port = $portProbe.LocalEndpoint.Port; $portProbe.Stop()
& initdb -D $data -U confast_rehearsal_operator --auth-local=trust --auth-host=trust --encoding=UTF8 --locale=C
if ($LASTEXITCODE -ne 0) { throw 'initdb failed.' }
# Loopback trust is solely for this temporary empty cluster and synthetic identities.
& pg_ctl -D $data -l (Join-Path $output 'postgres.log') -o "-h 127.0.0.1 -p $port" -w start
if ($LASTEXITCODE -ne 0) { throw 'Private PostgreSQL startup failed.' }
try {
    & createdb -h 127.0.0.1 -p $port -U confast_rehearsal_operator confast_phase3g_test
    if ($LASTEXITCODE -ne 0) { throw 'Disposable database creation failed.' }
    @{
        DataDirectory = $data
        Port = $port
        ConnectionString = "Host=127.0.0.1;Port=$port;Database=confast_phase3g_test;Username=confast_rehearsal_operator;Pooling=false"
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'cluster.json') -Encoding utf8
    Write-Output "Disposable rehearsal cluster listening on loopback port $port. Stop with pg_ctl -D `"$data`" -m fast -w stop."
} catch {
    & pg_ctl -D $data -m fast -w stop
    throw
}
