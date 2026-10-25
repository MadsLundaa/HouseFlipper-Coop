<#
  House Flipper Co-op installer.
  - Finds your House Flipper (Steam) folder, or use -GameDir "D:\...\House Flipper".
  - Installs BepInEx 5 (mod loader, bundled in .\bepinex) and the co-op plugin.
  - Writes hfcoop-install-manifest.txt so uninstall.ps1 removes exactly what was added.
  - Never touches your saves (they live in %LOCALAPPDATA%Low\Empyrean\House Flipper Game).
#>
param([string]$GameDir = '', [switch]$Force)
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
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'HouseFlipper.exe'))) {
    Write-Host 'Could not find House Flipper automatically.' -ForegroundColor Yellow
    Write-Host 'Run:  powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "C:\...\steamapps\common\House Flipper"'
    exit 1
}
if (-not $Force -and (Get-Process HouseFlipper -ErrorAction SilentlyContinue)) { Write-Host 'Close House Flipper first.' -ForegroundColor Yellow; exit 1 }
Write-Host "Game folder: $GameDir"

$manifest = Join-Path $GameDir 'hfcoop-install-manifest.txt'
if (Test-Path $manifest) { Write-Host 'Co-op is already installed here; updating the plugin only.' }
$added = New-Object System.Collections.ArrayList
if (Test-Path $manifest) { Get-Content $manifest | ForEach-Object { [void]$added.Add($_) } }

function Add-File($src, $rel) {
    $dst = Join-Path $GameDir $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    $existed = Test-Path $dst
    if ($existed -and ($added -notcontains $rel)) {
        # a file that was there before us (e.g. another BepInEx install): keep a backup, do not claim ownership
        Copy-Item $dst "$dst.hfcoop-orig" -Force
    }
    Copy-Item $src $dst -Force
    if ($added -notcontains $rel) { [void]$added.Add($rel) }
}

# 1) BepInEx (skip if the game already has it)
$bepPresent = Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll')
if ($bepPresent -and -not (Test-Path $manifest)) {
    Write-Host 'BepInEx already present - leaving it as is.'
} else {
    $src = Join-Path $here 'bepinex'
    Get-ChildItem $src -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length + 1)
        Add-File $_.FullName $rel
    }
    Write-Host 'Installed BepInEx 5.4.23.3.'
}

# 2) the plugin
Add-File (Join-Path $here 'plugin\HFCoop.dll') 'BepInEx\plugins\HFCoop\HFCoop.dll'
New-Item -ItemType Directory -Force (Join-Path $GameDir 'BepInEx\config') | Out-Null
Set-Content -Path $manifest -Value ($added | Sort-Object -Unique) -Encoding ASCII
Write-Host ''
Write-Host 'Done. Start House Flipper from Steam, load a house, then:' -ForegroundColor Green
Write-Host '  F5 = host a session   F6 = join (address in BepInEx\config\dev.hfcoop.cfg)   F7 = co-op panel   F4 = disconnect'
Write-Host 'Read README.md for firewall / port forwarding and the save-safety notes.'

