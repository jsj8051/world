param(
    [string]$GodotExe = $env:GODOT_EXE,
    [switch]$GenerateWorld,
    [switch]$Capture
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $GodotExe -or -not (Test-Path -LiteralPath $GodotExe -PathType Leaf)) {
    throw 'Pass -GodotExe with the Godot 4.7.1 Mono console executable path.'
}
$GodotExe = (Resolve-Path -LiteralPath $GodotExe).Path
Push-Location $root
try {
    & dotnet run --project tests/World.Tests.Local
    if ($LASTEXITCODE -ne 0) { throw 'Local regression tests failed.' }
    & dotnet build world.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & $GodotExe --headless --path $root res://tests/scenes/GenerationConfigCheck.tscn
    if ($LASTEXITCODE -ne 0) { throw 'Godot config interaction check failed.' }
    & $GodotExe --headless --path $root res://tests/scenes/GenerationConfigCheck.tscn -- --persistence-write
    if ($LASTEXITCODE -ne 0) { throw 'User data persistence write failed.' }
    & $GodotExe --headless --path $root res://tests/scenes/GenerationConfigCheck.tscn -- --persistence-read
    if ($LASTEXITCODE -ne 0) { throw 'Persistence read in new process failed.' }
    if ($GenerateWorld) {
        & $GodotExe --headless --path $root res://tests/scenes/GenerationConfigCheck.tscn -- --generate
        if ($LASTEXITCODE -ne 0) { throw 'Real world generation check failed.' }
    }
    if ($Capture) {
        & $GodotExe --path $root --rendering-method gl_compatibility --resolution 1280x800 res://tests/scenes/GenerationConfigCheck.tscn
        if ($LASTEXITCODE -ne 0) { throw 'Rendered config check failed.' }
    }
    Write-Host 'CONFIG VERIFICATION PASSED (exported Windows package is a separate manual check).'
}
finally { Pop-Location }
