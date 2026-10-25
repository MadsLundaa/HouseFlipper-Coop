param([int]$ProcId, [int]$X = -1, [int]$Y = -1)
Add-Type -Namespace W -Name U9 -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);'
$p = Get-Process -Id $ProcId
[W.U9]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
if ($X -ge 0) { [W.U9]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001 -bor 0x0004) | Out-Null }  # SWP_NOSIZE|SWP_NOZORDER
[W.U9]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
