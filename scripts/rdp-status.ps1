# dsh-rdp-wrapper : status probe. ASCII-only source.
$ErrorActionPreference = 'Continue'
$PD = "$env:ProgramData\dsh-rdp-wrapper"
$out = @{ ok = $true }
try { $out.config = Get-Content "$PD\config.json" -Raw | ConvertFrom-Json } catch { $out.config = $null }

$sess = @()
try {
  $quser = quser 2>$null
  for ($i = 1; $i -lt $quser.Count; $i++) {
    $t = @(($quser[$i] -split '\s{2,}') | Where-Object { $_.Trim() })
    $numIdx = -1
    for ($j = 0; $j -lt $t.Count; $j++) { if ($t[$j] -match '^\d+$') { $numIdx = $j; break } }
    if ($numIdx -lt 0) { continue }
    $s = @{ user = ($t[0] -replace '^>', ''); id = [int]$t[$numIdx]; state = $t[$numIdx + 1] }
    if ($numIdx -ge 1) { $s.sessionName = $t[$numIdx - 1] } else { $s.sessionName = '' }
    $sess += $s
  }
} catch { }
$out.sessions = $sess

$bridges = @()
try {
  foreach ($f in (Get-ChildItem "$PD\ports\bridge-session-*.json" -ErrorAction SilentlyContinue)) {
    try {
      $j = $f | Get-Content -Raw | ConvertFrom-Json
      $alive = $false
      $c = New-Object Net.Sockets.TcpClient
      try { $c.Connect('127.0.0.1', [int]$j.port); $alive = $c.Connected } catch { }
      $c.Close()
      $bridges += @{ session = $j.session; user = $j.user; port = $j.port; pid = $j.pid; alive = $alive }
    } catch { }
  }
} catch { }
$out.bridges = $bridges

$out.childhost = $null
try {
  if (Test-Path "$PD\ports\childhost.json") {
    $h = Get-Content "$PD\ports\childhost.json" -Raw | ConvertFrom-Json
    $alive = $false
    if ($h.pid) { $alive = [bool](Get-Process -Id ([int]$h.pid) -ErrorAction SilentlyContinue) }
    $out.childhost = @{
      pid = $h.pid; childSessionId = $h.childSessionId; message = $h.message
      ok = $h.ok; at = $h.at; alive = $alive
    }
  }
} catch { }

$out | ConvertTo-Json -Depth 5
