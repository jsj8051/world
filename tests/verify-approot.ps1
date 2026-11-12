# AppRoot 重构回归：构建 → 逻辑单测 → 配置页 → 生成加载 → 浏览加载。
# 用法：powershell -File tests/verify-approot.ps1（或 pwsh -File）
# 前置：Godot mono 可执行文件（$env:GODOT_EXE 覆盖）；首次运行会导出一次 .wmap 样本。
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $godot = if ($env:GODOT_EXE) { $env:GODOT_EXE } else { 'D:\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe' }
    if (-not (Test-Path $godot)) { throw "缺少 Godot：$godot（用 GODOT_EXE 指定）" }

    Write-Host '═══ [1/5] 构建 ═══'
    dotnet build world.csproj -nologo -v q | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }

    Write-Host '═══ [2/5] 逻辑单测 ═══'
    $unit = (dotnet run --project tests/World.Tests.Local --configuration Debug 2>&1 | Out-String)
    if ($unit -notmatch '(\d+) passed, 0 failed') { throw "单测失败：$unit" }
    [regex]::Match($unit, '\d+ passed, 0 failed').Value

    Write-Host '═══ [3/5] 配置页检查 ═══'
    $config = (& $godot --headless --path . res://tests/scenes/GenerationConfigCheck.tscn 2>&1 | Out-String)
    if ($config -notmatch '\[CONFIG-CHECK\] PASS') { throw "配置页检查失败：$config" }
    'PASS navigation, tabs, apply, named save, dirty gate, default-applied generation'

    Write-Host '═══ [4/5] 生成加载检查 ═══'
    $gen = (& $godot --headless --path . res://tests/scenes/WorldGenerationLoadCheck.tscn 2>&1 | Out-String)
    if ($gen -notmatch '\[GEN-CHECK\] ok=True') { throw "生成加载检查失败：$gen" }
    ([regex]::Match($gen, '\[GEN-CHECK\][^\r\n]*')).Value

    Write-Host '═══ [5/5] 浏览加载检查 ═══'
    $maps = Get-ChildItem (Join-Path $env:APPDATA 'Godot/app_userdata/world/maps') -Filter *.wmap -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    if ($maps) {
        $mapPath = $maps[0].FullName
    }
    else {
        Write-Host '  没有样本地图，先导出一份…'
        $sample = (& $godot --headless --path . res://tests/scenes/MapSampleExport.tscn 2>&1 | Out-String)
        $found = [regex]::Match($sample, '\[SAMPLE\] path=(.+)')
        if (-not $found.Success) { throw "样本导出失败：$sample" }
        $mapPath = $found.Groups[1].Value.Trim()
    }
    $browse = (& $godot --headless --path . res://tests/scenes/MapBrowserLoadCheck.tscn -- $mapPath 2>&1 | Out-String)
    if ($browse -notmatch '\[BROWSE-CHECK\] ok=True') { throw "浏览检查失败：$browse" }
    ([regex]::Match($browse, '\[BROWSE-CHECK\][^\r\n]*')).Value

    Write-Host "`n全部通过（样本地图：$mapPath）"
}
finally {
    Pop-Location
}
