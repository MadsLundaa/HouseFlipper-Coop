param([string]$Version = '0.1.0')
# Assembles release\HouseFlipperCoop-<ver>.zip from the compiled plugin + bundled BepInEx + installer + docs.
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "release\HouseFlipperCoop-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\plugin", "$stage\bepinex", "$stage\docs" | Out-Null
Copy-Item "$root\dist\HFCoop.dll" "$stage\plugin\"
Copy-Item "$root\installer\*" $stage -Recurse
Copy-Item "$root\README.md", "$root\LICENSE", "$root\THIRD-PARTY.md", "$root\CHANGELOG.md" $stage
Copy-Item "$root\docs\*" "$stage\docs" -Recurse -ErrorAction SilentlyContinue
# BepInEx 5.4.23.3 x64 (LGPL-2.1, https://github.com/BepInEx/BepInEx) - verified loading in this game
# Point BEPINEX_DIR at an unmodified BepInEx 5.4.23.3 x64 folder (it is not stored in git); default: third_party\bepinex next to this repo
$bep = if ($env:BEPINEX_DIR) { $env:BEPINEX_DIR } else { Join-Path $root 'third_party\bepinex' }
if (-not (Test-Path "$bep\BepInEx")) { throw "BepInEx not found in '$bep' (set BEPINEX_DIR)" }
Copy-Item "$bep\*" "$stage\bepinex" -Recurse
$zip = Join-Path $root "release\HouseFlipperCoop-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Get-Item $zip | Select-Object FullName, Length

# Nexus Mods package: the same files in the layout the installer copies them to, ready to extract into the game folder.
# No .bat/.ps1 scripts: Nexus quarantines archives with scripts in them. The GitHub workflow uploads this zip to Nexus.
$nstage = Join-Path $root "release\nexus-$Version"
if (Test-Path $nstage) { Remove-Item $nstage -Recurse -Force }
New-Item -ItemType Directory -Force "$nstage\BepInEx\plugins\HFCoop" | Out-Null
Copy-Item "$stage\bepinex\*" $nstage -Recurse -Force
Copy-Item "$stage\plugin\HFCoop.dll" "$nstage\BepInEx\plugins\HFCoop\"
Copy-Item "$root\nexus\HFCoop-README.txt" $nstage
Copy-Item "$root\LICENSE" "$nstage\HFCoop-LICENSE.txt"; Copy-Item "$root\THIRD-PARTY.md" "$nstage\HFCoop-THIRD-PARTY.md"
$bad = Get-ChildItem $nstage -Recurse -File | Where-Object { $_.Extension -in '.bat', '.cmd', '.ps1', '.exe', '.zip', '.vbs' }
if ($bad) { throw "Nexus package contains files Nexus quarantines: $($bad.Name -join ', ')" }
$nzip = Join-Path $root "release\HouseFlipperCoop-$Version-nexus.zip"
if (Test-Path $nzip) { Remove-Item $nzip }
Compress-Archive -Path "$nstage\*" -DestinationPath $nzip
Get-Item $nzip | Select-Object FullName, Length
