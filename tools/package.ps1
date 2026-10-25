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
