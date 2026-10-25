<#
  House Flipper Co-op uninstaller. Removes exactly the files the installer added (see hfcoop-install-manifest.txt).
  Your saves, your profile and other BepInEx plugins are never deleted.
  Use -KeepConfig to keep BepInEx\config\dev.hfcoop.cfg.
#>
param([string]$GameDir = '', [switch]$KeepConfig, [switch]$Force)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
function Find-GameDir {
    $cands = New-Object System.Collections.ArrayList
    $steam = $null
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try { $v = (Get-ItemProperty $k -ErrorAction Stop); if ($v.SteamPath) { $steam = $v.SteamPath; break } if ($v.InstallPath) { $steam = $v.InstallPath; break } } catch {}
    }
    if ($steam) {
        [void]$cands.Add((Join-Path $steam 'steamapps'))
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { [void]$cands.Add((Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps')) } }
    }
    foreach ($c in $cands) { $p = Join-Path $c 'common\House Flipper'; if (Test-Path (Join-Path $p 'HouseFlipper.exe')) { return $p } }
    return $null
}
if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'HouseFlipper.exe'))) { Write-Host 'Game folder not found. Use -GameDir.' -ForegroundColor Yellow; exit 1 }
if (-not $Force -and (Get-Process HouseFlipper -ErrorAction SilentlyContinue)) { Write-Host 'Close House Flipper first.' -ForegroundColor Yellow; exit 1 }

$manifest = Join-Path $GameDir 'hfcoop-install-manifest.txt'
if (-not (Test-Path $manifest)) { Write-Host 'No install manifest found - nothing to remove.'; exit 0 }
$files = Get-Content $manifest
foreach ($rel in $files) {
    $p = Join-Path $GameDir $rel
    if (Test-Path $p) {
        Remove-Item $p -Force
        $orig = "$p.hfcoop-orig"
        if (Test-Path $orig) { Move-Item $orig $p -Force }   # restore a file we had replaced
    }
}
# runtime files created by the plugin
foreach ($rel in 'BepInEx\hfcoop-dev.enabled', 'BepInEx\hfcoop-balance-backup.txt') { $p = Join-Path $GameDir $rel; if (Test-Path $p) { Remove-Item $p -Force } }
if (-not $KeepConfig) { $c = Join-Path $GameDir 'BepInEx\config\dev.hfcoop.cfg'; if (Test-Path $c) { Remove-Item $c -Force } }
# remove now-empty directories we created (never non-empty ones: other mods keep working)
Get-ChildItem (Join-Path $GameDir 'BepInEx') -Recurse -Directory -ErrorAction SilentlyContinue | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { if (-not (Get-ChildItem $_.FullName -Force)) { Remove-Item $_.FullName -Force } }
$be = Join-Path $GameDir 'BepInEx'
if ((Test-Path $be) -and -not (Get-ChildItem $be -Force)) { Remove-Item $be -Force }
Remove-Item $manifest -Force
Write-Host 'Co-op mod removed. Saves and profile were not touched.' -ForegroundColor Green

