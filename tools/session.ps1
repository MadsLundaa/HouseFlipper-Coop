param([switch]$AcceptJob)
# Starts host+client (720p windows side by side), joins from the client's main menu and stops once both are in the host's house (no job is accepted
# unless -AcceptJob). Waits on log markers instead of fixed delays. Writes pids to pids.txt
$b = 'I:\SteamLibrary\steamapps\common\House Flipper\BepInEx'; $g = 'I:\SteamLibrary\steamapps\common\House Flipper'; $t = $PSScriptRoot
$hostLog = "$b\LogOutput.log"; $cliLog = "$t\inst2.log"
function WaitLog($path, $text, $max) { for ($i = 0; $i -lt $max; $i++) { if ((Test-Path $path) -and (Select-String -Path $path -Pattern $text -SimpleMatch -Quiet -EA SilentlyContinue)) { return $true }; Start-Sleep 1 }; return $false }
Get-Process HouseFlipper -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
Copy-Item "$t\..\dist\HFCoop.dll" "$b\plugins\HFCoop\HFCoop.dll" -Force
Set-Content "$b\hfcoop-dev.enabled" ''; Remove-Item "$b\hfcoop-cmd-*.txt" -EA SilentlyContinue
$c = "$b\config\dev.hfcoop.cfg"; (Get-Content $c -Raw) -replace '(?m)^(VerboseLog = ).*$', '${1}true' | Set-Content $c -Encoding utf8
Remove-Item $hostLog, $cliLog -EA SilentlyContinue
Start-Process 'steam://rungameid/613100'
[void](WaitLog $hostLog 'Chainloader startup complete' 150); Start-Sleep 22
Start-Process "$g\HouseFlipper.exe" -WorkingDirectory $g -ArgumentList '-logFile', $cliLog
[void](WaitLog $cliLog 'Chainloader startup complete' 150); Start-Sleep 22
$p = Get-Process HouseFlipper | Sort-Object StartTime; $h = $p[0].Id; $cl = $p[1].Id; "$h,$cl" | Set-Content "$t\pids.txt"
& "$t\focus.ps1" -ProcId $cl -X 1290 -Y 0; & "$t\focus.ps1" -ProcId $h -X 0 -Y 0
foreach ($x in $h, $cl) { Set-Content "$b\hfcoop-cmd-$x.txt" 'click Return' }; Start-Sleep 3
& "$t\focus.ps1" -ProcId $h; Start-Sleep 1; & "$t\input.ps1" -Action click -X 146 -Y 392; Start-Sleep 4; & "$t\input.ps1" -Action key -Arg return
# the host is in its house once the recording exists
for ($i = 0; $i -lt 40; $i++) { Start-Sleep 3; Set-Content "$b\hfcoop-cmd-$h.txt" 'reclists'; Start-Sleep 1; if (Select-String -Path $hostLog -Pattern '[dev] reclists ' -SimpleMatch -Quiet -EA SilentlyContinue) { break } }
Start-Sleep 3; & "$t\focus.ps1" -ProcId $h; Start-Sleep 1; & "$t\input.ps1" -Action key -Arg f5; Start-Sleep 2
Set-Content "$b\hfcoop-cmd-$cl.txt" 'join'
[void](WaitLog $cliLog 'house loaded from host snapshot' 120); Start-Sleep 3
if ($AcceptJob) {
    & "$t\focus.ps1" -ProcId $h; Start-Sleep 1
    & "$t\input.ps1" -Action key -Arg w -Ms 1400; & "$t\input.ps1" -Action key -Arg e -Ms 150; Start-Sleep 1; & "$t\input.ps1" -Action key -Arg w -Ms 1100
    & "$t\mouse.ps1" -Action move -X -420 -Y 60; Start-Sleep -Milliseconds 500; & "$t\input.ps1" -Action key -Arg w -Ms 1100
    & "$t\mouse.ps1" -Action move -X -60 -Y 40; Start-Sleep -Milliseconds 400; & "$t\mouse.ps1" -Action move -X 20 -Y 260; Start-Sleep -Milliseconds 500; & "$t\mouse.ps1" -Action move -X -150 -Y 30; Start-Sleep -Milliseconds 600
    & "$t\input.ps1" -Action key -Arg e -Ms 150; Start-Sleep 3; & "$t\input.ps1" -Action key -Arg space -Ms 150; Start-Sleep 3
    & "$t\input.ps1" -Action click -X 575 -Y 330; Start-Sleep 3; & "$t\input.ps1" -Action click -X 624 -Y 477; Start-Sleep 3; & "$t\input.ps1" -Action click -X 893 -Y 543; Start-Sleep 85
}
"session ready: host $h client $cl"
