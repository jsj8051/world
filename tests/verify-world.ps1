param(
    [ValidateSet(2, 4)]
    [int]$Resolution = 2,
    [string]$ReportPath = "tests/World.Tests.Local/bin/regression/report.json"
)

$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
    dotnet run --configuration Release --project tests/World.Tests.Local -- --world-regression $Resolution $ReportPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
