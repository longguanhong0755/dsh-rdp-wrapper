# dsh-rdp-wrapper : connect the agent into a real RDP child session.
#
# The child session is created by scripts\childhost\rdp-child-host.exe, which
# hosts the built-in MsRdpClient10 ActiveX control and sets the extended
# property ConnectToChildSession=true. This is the same mechanism BetterGI
# uses for its desktop clone: Windows clones the current interactive logon
# into an isolated child session (no password, console session untouched).
# The host then starts the in-session bridge through a temporary scheduled
# task and TASK_RUN_USE_SESSION_ID, and stays alive to keep the session.
# ASCII-only source.
param([int]$WaitSec = 120, [int]$Width = 0, [int]$Height = 0)
$ErrorActionPreference = 'Stop'
$PD = "$env:ProgramData\dsh-rdp-wrapper"
$status = "$PD\ports\childhost.json"
$out = @{ ok = $false }

function Test-BridgeAlive($j) {
  try {
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', [int]$j.port); return [bool]$c.Connected } catch { return $false } finally { $c.Close() }
  } catch { return $false }
}

function Get-ReadyBridge($hostStatus) {
  if (-not $hostStatus.childSessionId) { return $null }
  $pf = "$PD\ports\bridge-session-$($hostStatus.childSessionId).json"
  if (-not (Test-Path $pf)) { return $null }
  try {
    $j = Get-Content $pf -Raw | ConvertFrom-Json
    if (Test-BridgeAlive $j) { return $j }
  } catch { }
  return $null
}

try {
  if (-not (Test-Path "$PD\config.json")) { throw "not provisioned: run rdp_provision first (see README.md)" }
  $cfg = Get-Content "$PD\config.json" -Raw | ConvertFrom-Json

  # Reuse the bridge of a live child host (the child session is a clone of the
  # interactive logon, so it is matched by session id, not by account name).
  if (Test-Path $status) {
    try {
      $st0 = Get-Content $status -Raw | ConvertFrom-Json
      if ($st0.pid -and (Get-Process -Id ([int]$st0.pid) -ErrorAction SilentlyContinue) -and
          $st0.message -eq 'bridge-ready') {
        $bj0 = Get-ReadyBridge $st0
        if ($bj0) {
          $out.ok = $true
          $out.alreadyConnected = $true
          $out.session = $bj0.session
          $out.port = $bj0.port
          $out.user = $bj0.user
          $out.childSessionId = $st0.childSessionId
          $out.hostPid = $st0.pid
          $out | ConvertTo-Json -Compress -Depth 5
          exit 0
        }
      }
    } catch { }
  }

  $width = 1600
  $height = 900
  if ($cfg.screenW) { $width = [int]$cfg.screenW }
  if ($cfg.screenH) { $height = [int]$cfg.screenH }
  if ($Width -gt 0) { $width = $Width }
  if ($Height -gt 0) { $height = $Height }

  # A host left over from an earlier attempt? Reuse it and just wait.
  $hostPid = 0
  try {
    if (Test-Path $status) {
      $st0 = Get-Content $status -Raw | ConvertFrom-Json
      if ($st0.pid -and (Get-Process -Id ([int]$st0.pid) -ErrorAction SilentlyContinue)) {
        $hostPid = [int]$st0.pid
      }
    }
  } catch { }

  if (-not $hostPid) {
    Remove-Item $status -Force -ErrorAction SilentlyContinue
    $startRaw = & "$PSScriptRoot\childhost\start-childhost.ps1" -Width $width -Height $height
    $start = $startRaw | ConvertFrom-Json
    $hostPid = [int]$start.pid
    $out.hostPid = $hostPid
  }

  $deadline = (Get-Date).AddSeconds($WaitSec)
  $ready = $false
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 800
    $st = $null
    try { $st = Get-Content $status -Raw | ConvertFrom-Json } catch { continue }
    if (-not $st) { continue }
    # Ignore a status file left over from a previous host (different pid).
    if ($st.pid -ne $hostPid) { continue }

    if ($st.message -eq 'bridge-ready' -and $st.childSessionId) {
      $pf = "$PD\ports\bridge-session-$($st.childSessionId).json"
      if (Test-Path $pf) {
        $bj = Get-Content $pf -Raw | ConvertFrom-Json
        if (Test-BridgeAlive $bj) {
          $out.ok = $true
          $out.session = $bj.session
          $out.port = $bj.port
          $out.user = $bj.user
          $out.childSessionId = $st.childSessionId
          $out.hostPid = $st.pid
          $ready = $true
          break
        }
      }
      continue
    }

    if ($st.ok -eq $false -and $st.message) {
      $out.error = "child host failed: $($st.message)"
      break
    }
    if (-not (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) {
      $out.error = "child host exited before the bridge was ready; see $PD\logs\childhost.log and launcher.log"
      break
    }
  }

  if (-not $ready -and -not $out.error) {
    $out.error = "bridge did not come up within ${WaitSec}s; see $PD\logs\childhost.log and launcher.log"
  }
  $out | ConvertTo-Json -Compress -Depth 5
} catch {
  $out.error = $_.Exception.Message
  $out | ConvertTo-Json -Compress -Depth 5
}
