param([string]$OutputDirectory = (Join-Path 'artifacts' ('authorization-rehearsal-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $repo
$output = [IO.Path]::GetFullPath($OutputDirectory)
$priorConnection = $env:CONFAST_TEST_CONNECTION_STRING
$priorRehearsal = $env:CONFAST_ISOLATED_REHEARSAL
$priorModelConnection = $env:ConnectionStrings__Confast
$started = $false
try {
    & (Join-Path $PSScriptRoot 'start-disposable-rehearsal.ps1') -OutputDirectory $output
    $started = $true
    $cluster = Get-Content -LiteralPath (Join-Path $output 'cluster.json') | ConvertFrom-Json
    $env:CONFAST_TEST_CONNECTION_STRING = $cluster.ConnectionString
    $env:ConnectionStrings__Confast = $cluster.ConnectionString
    $env:CONFAST_ISOLATED_REHEARSAL = '1'
    & dotnet restore Confast.sln --force-evaluate -p:NuGetAudit=true -p:NuGetAuditMode=all 2>&1 | Tee-Object (Join-Path $output 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw 'Restore/audit failed.' }
    & dotnet build Confast.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false 2>&1 | Tee-Object (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet ef migrations has-pending-model-changes --project src/Confast.Web --startup-project src/Confast.Web --configuration Release --no-build 2>&1 | Tee-Object (Join-Path $output 'model.log')
    if ($LASTEXITCODE -ne 0) { throw 'Model check failed.' }
    & dotnet list Confast.sln package --vulnerable --include-transitive --format json | Tee-Object (Join-Path $output 'dependency-audit.json')
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed.' }
    & dotnet test Confast.sln -c Release --no-build --filter 'FullyQualifiedName~DeploymentSecurityTests|FullyQualifiedName~HostSecurityTests' --logger 'trx;LogFileName=focused.trx' --results-directory (Join-Path $output 'focused') 2>&1 | Tee-Object (Join-Path $output 'focused.log')
    if ($LASTEXITCODE -ne 0) { throw 'Focused rehearsal failed; full suite not started.' }
    & dotnet test Confast.sln -c Release --no-build --logger 'trx;LogFileName=full.trx' --results-directory (Join-Path $output 'full') 2>&1 | Tee-Object (Join-Path $output 'full.log')
    if ($LASTEXITCODE -ne 0) { throw 'Full suite failed; see retained results.' }
} finally {
    $env:CONFAST_TEST_CONNECTION_STRING = $priorConnection
    $env:CONFAST_ISOLATED_REHEARSAL = $priorRehearsal
    $env:ConnectionStrings__Confast = $priorModelConnection
    if ($started) { & (Join-Path $PSScriptRoot 'stop-disposable-rehearsal.ps1') -OutputDirectory $output -RemoveClusterData }
}
