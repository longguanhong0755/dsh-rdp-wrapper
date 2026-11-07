# dsh-rdp-wrapper : disconnect (stop the child-session host and log off).
#
# The child host owns the RDP connection and logs the child session off when
# it closes, so the normal path is just a stop-file handshake. A hard kill is
# only a fallback for a hung host; the stale session is then logged off.
# ASCII-only source.
param([switch]$KeepWindow)
$ErrorActionPreference = 'Continue'
$PD = "$env:ProgramData\dsh-rdp-wrapper"
$status = "$PD\ports\childhost.json"
$out = @{ ok = $true }

$st = $null
try { if (Test-Path $status) { $st = Get-Content $status -Raw | ConvertFrom-Json } } catch { }

$hostPid = 0
if ($st -and $st.pid) { $hostPid = [int]$st.pid }
$hostAlive = $false
if ($hostPid) { $hostAlive = [bool](Get-Process -Id $hostPid -ErrorAction SilentlyContinue) }

if ($hostAlive) {
  try {
    Set-Content -Path "$PD\ports\childhost.stop" -Value 'stop' -Encoding ascii
  } catch { }
  $deadline = (Get-Date).AddSeconds(15)
  while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 500
  }
  if (Get-Process -Id $hostPid -ErrorAction SilentlyContinue) {
    $out.forced = $true
    $proc = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
      -ArgumentList @('-NoProfile', '-Command', "Stop-Process -Id $hostPid -Force")
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
      if (-not (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) { break }
      Start-Sleep -Milliseconds 500
    }
  }
  $out.ok = -not [bool](Get-Process -Id $hostPid -ErrorAction SilentlyContinue)
  if (-not $out.ok) { $out.error = "host process $hostPid did not exit" }
  $out.hostPid = $hostPid
} elseif ($hostPid) {
  $out.ok = $true
  $out.note = "host process $hostPid already exited"
}

# Log off a child session that survived a hard kill.
$childSid = 0
if ($st -and $st.childSessionId) { $childSid = [int]$st.childSessionId }
if ($childSid) {
  $stillThere = $false
  try {
    $qw = qwinsta 2>$null
    foreach ($line in $qw) {
      if ($line -match ('\s' + [string]$childSid + '\s')) { $stillThere = $true }
    }
  } catch { }
  if ($stillThere) {
    $r = & "$PSScriptRoot\rdp-elevate.ps1" -Action logoff -SessionId ([string]$childSid)
    $txt = ([string]($r | Out-String)).Trim()
    try { $out.logoff = $txt | ConvertFrom-Json } catch { $out.logoff = $txt }
  }
}

# Leftover bridge files are useless once the host is gone; the next host would
# clean them anyway.
try {
  foreach ($f in (Get-ChildItem "$PD\ports\bridge-session-*.json" -ErrorAction SilentlyContinue)) {
    $alive = $false
    try {
      $j = $f | Get-Content -Raw | ConvertFrom-Json
      if ($j.pid) { $alive = [bool](Get-Process -Id ([int]$j.pid) -ErrorAction SilentlyContinue) }
    } catch { }
    if (-not $alive) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
  }
} catch { }

try { Remove-Item "$PD\ports\childhost.stop" -Force -ErrorAction SilentlyContinue } catch { }
$out | ConvertTo-Json -Depth 5
