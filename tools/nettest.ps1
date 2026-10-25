param([string]$Mode = 'badproto', [int]$Port = 27055)
# Raw-protocol test client: exercises the host's handshake validation and timeout handling without a second game.
function Frame([byte[]]$p) { $l = [BitConverter]::GetBytes([int]$p.Length); return $l + $p }
function Hello([int]$proto, [string]$build, [string]$name) {
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ms)
    $w.Write([byte]1); $w.Write([int]$proto); $w.Write($build); $w.Write("test"); $w.Write($name); $w.Flush(); return $ms.ToArray()
}
function ReadFrame($s) {
    $h = New-Object byte[] 4; $n = 0
    while ($n -lt 4) { $r = $s.Read($h, $n, 4 - $n); if ($r -le 0) { return $null }; $n += $r }
    $len = [BitConverter]::ToInt32($h, 0); $b = New-Object byte[] $len; $n = 0
    while ($n -lt $len) { $r = $s.Read($b, $n, $len - $n); if ($r -le 0) { return $null }; $n += $r }
    return $b
}
$c = New-Object Net.Sockets.TcpClient('127.0.0.1', $Port); $s = $c.GetStream(); $s.ReadTimeout = 20000
$build = '[release] 1.26252 (ab1aa)'
switch ($Mode) {
    'badproto' { $f = Frame (Hello 99 $build 'TestBot') }
    'badbuild' { $f = Frame (Hello 2 'wrong-build' 'TestBot') }
    'silent'   { $f = Frame (Hello 2 $build 'SilentBot') }
}
$s.Write($f, 0, $f.Length)
$t0 = Get-Date
try {
    while ($true) {
        $b = ReadFrame $s
        if ($null -eq $b) { "closed by host after {0:N1}s" -f ((Get-Date) - $t0).TotalSeconds; break }
        $type = $b[0]
        if ($type -eq 3) { $br = New-Object IO.BinaryReader((New-Object IO.MemoryStream(,$b))); $br.ReadByte() | Out-Null; "REJECT: " + $br.ReadString() }
        elseif ($type -eq 2) { "WELCOME received" }
        elseif ($type -eq 4 -and $Mode -ne 'silent') { $pong = Frame ([byte[]]@(5)); $s.Write($pong, 0, $pong.Length) }
        elseif ($type -eq 10) { "got snapshot frame (type 10)" }
    }
} catch { "read ended after {0:N1}s: {1}" -f ((Get-Date) - $t0).TotalSeconds, $_.Exception.InnerException.Message }
