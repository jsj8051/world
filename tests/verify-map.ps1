# 地图保存/加载一键验证：构建 → 单测 → 对最新地图做 res4 无头加载。
# 用法：pwsh -File tests/verify-map.ps1 [-GodotExe <Godot 控制台 exe>]
param(
    [string]$GodotExe = $env:GODOT_EXE
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $GodotExe) {
    $candidates = @(
        'D:\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe',
        'C:\Program Files\Godot\Godot_v4.7.1-stable_mono_win64_console.exe'
    )
    $GodotExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

Write-Host '=== [1/3] dotnet build -t:Rebuild ==='
dotnet build world.csproj -t:Rebuild -m:1 -nodeReuse:false -p:NuGetAudit=false | Select-Object -Last 4
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

Write-Host ''
Write-Host '=== [2/3] 单测（含地图包往返与损坏拒绝）==='
dotnet run --project tests/World.Tests.Local
if ($LASTEXITCODE -ne 0) { throw '单测失败' }

Write-Host ''
Write-Host '=== [3/3] 最新地图的无头加载检查 ==='
$mapsDir = Join-Path $root 'userdata\saves'
$latest = Get-ChildItem -Path $mapsDir -Filter '*.wmap' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) {
    Write-Host '（尚无 .wmap；请先在游戏里 主界面 → 地理演化 → 菜单 → 保存地图）'
    exit 0
}
if (-not $GodotExe -or -not (Test-Path $GodotExe)) {
    Write-Host "未找到 Godot 控制台 exe；已跳过加载检查（地图：$($latest.FullName)）"
    exit 0
}

$output = & $GodotExe --headless --path $root 'res://tests/scenes/MapImportCheck.tscn' -- $latest.FullName 2>&1
$output | Select-Object -Last 6
if ($LASTEXITCODE -ne 0 -or -not ($output -match '\[MAP-CHECK\]')) {
    throw "加载检查失败（地图：$($latest.FullName)）"
}
Write-Host '✓ 地图加载检查通过'
