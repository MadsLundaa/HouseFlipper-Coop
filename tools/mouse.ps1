param([string]$Action, [int]$X = 0, [int]$Y = 0)
# Relative mouse input for locked-cursor games: move dx dy | lmb | rmb | wheel N
Add-Type -Namespace M -Name N -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);'
switch ($Action) {
    'move' {
        # break into small steps so the game's raw input picks it up smoothly
        $steps = [Math]::Max(1, [int]([Math]::Max([Math]::Abs($X), [Math]::Abs($Y)) / 20))
        for ($i = 0; $i -lt $steps; $i++) { [M.N]::mouse_event(1, [int]($X / $steps), [int]($Y / $steps), 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 8 }
    }
    'lmb' { [M.N]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds ([Math]::Max(60, $X)); [M.N]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
    'rmb' { [M.N]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds ([Math]::Max(60, $X)); [M.N]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }
    'drag' {   # hold LMB 1.2s (pick up), move dx dy while held, release (put down)
        [M.N]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 1300
        for ($i = 0; $i -lt 10; $i++) { [M.N]::mouse_event(1, [int]($X / 10), [int]($Y / 10), 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
        Start-Sleep -Milliseconds 500; [M.N]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
    'wheel' { [M.N]::mouse_event(0x800, 0, 0, $X * 120, [UIntPtr]::Zero) }
}
