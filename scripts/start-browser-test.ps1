param(
    [int]$PreferredPort = 5105,
    [int]$FallbackPort = 5167,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\Confast.Web'

function Test-ConfastEndpoint([int]$Port) {
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -ge 200
            -and $response.StatusCode -lt 500
            -and $response.Content -match 'ConFastDB|Confast DB company account'
    }
    catch {
        return $false
    }
}

function Test-PortInUse([int]$Port) {
    return $null -ne (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
}

$port = $PreferredPort
if (Test-ConfastEndpoint $port) {
    Write-Host "Reusing the healthy ConfastDB instance at http://127.0.0.1:$port/"
    exit 0
}

if (Test-PortInUse $port) {
    Write-Host "Port $port is occupied by an unresponsive process; selecting an alternate port."
    $port = $FallbackPort
    if (Test-ConfastEndpoint $port) {
        Write-Host "Reusing the healthy ConfastDB instance at http://127.0.0.1:$port/"
        exit 0
    }
}

while (Test-PortInUse $port) {
    $port++
}

$url = "http://127.0.0.1:$port"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $url

Write-Host "Starting ConfastDB in Development at $url"
Write-Host "Press Ctrl+C to stop this instance."

$dotnetArguments = @(
    'run',
    '--project', $projectPath,
    '--no-launch-profile',
    '--urls', $url,
    "-p:BaseOutputPath=$projectPath\.codex\browser-build-output\"
)

if ($NoBuild) {
    $dotnetArguments = @(
        'run',
        '--project', $projectPath,
        '--no-launch-profile',
        '--no-build',
        '--urls', $url
    )
}

& dotnet @dotnetArguments
exit $LASTEXITCODE
