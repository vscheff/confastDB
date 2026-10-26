param([Parameter(Mandatory)][string]$OutputDirectory, [switch]$RemoveClusterData)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $output 'cluster.json') | ConvertFrom-Json
$data = [IO.Path]::GetFullPath($manifest.DataDirectory)
$expected = [IO.Path]::GetFullPath((Join-Path $output 'postgres-data'))
if ($data -ne $expected -or -not $data.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Cluster path does not match the explicit output directory.'
}
& pg_ctl -D $data -m fast -w stop
if ($LASTEXITCODE -ne 0) { throw 'Cluster stop failed; data retained.' }
if ($RemoveClusterData) { Remove-Item -LiteralPath $data -Recurse -Force }
