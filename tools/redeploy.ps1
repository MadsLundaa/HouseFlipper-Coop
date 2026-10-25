param([switch]$NoLaunch)
$g = 'I:\SteamLibrary\steamapps\common\House Flipper'
Get-Process HouseFlipper -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 3
New-Item -ItemType Directory -Force "$g\BepInEx\plugins\HFCoop" | Out-Null
Copy-Item "$PSScriptRoot\..\dist\HFCoop.dll" "$g\BepInEx\plugins\HFCoop\" -Force
if (-not $NoLaunch) { Start-Process 'steam://rungameid/613100' }
