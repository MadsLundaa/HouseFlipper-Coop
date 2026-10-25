param([string]$Action, [string]$Arg = '', [int]$X = 0, [int]$Y = 0, [int]$Ms = 100)
# Win32 input without the computer-use tool: key <name> [Ms hold] | click X Y (absolute screen px)
Add-Type -Namespace I -Name W -MemberDefinition '
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);'
$map = @{ 'return' = 0x0D; 'escape' = 0x1B; 'tab' = 0x09; 'space' = 0x20; 'w' = 0x57; 'a' = 0x41; 's' = 0x53; 'd' = 0x44; 'e' = 0x45; 'f4' = 0x73; 'f5' = 0x74; 'f6' = 0x75; 'f7' = 0x76 }
switch ($Action) {
    'key' { $vk = $map[$Arg.ToLower()]; [I.W]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds $Ms; [I.W]::keybd_event($vk, 0, 2, [UIntPtr]::Zero) }
    'click' { [I.W]::SetCursorPos($X, $Y); Start-Sleep -Milliseconds 150; [I.W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [I.W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
}
