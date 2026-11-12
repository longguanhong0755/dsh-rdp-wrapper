# dsh-rdp-wrapper : start the in-session bridge only inside the child session
# created by rdp-child-host.exe. The host registers a temporary task and runs
# it with TASK_RUN_USE_SESSION_ID; the expected session id is passed on the
# command line, so a stray run anywhere else (console session) exits at once.
# ASCII-only source.
param(
  [Parameter(Mandatory)][string]$Bridge,
  [Parameter(Mandatory)][uint32]$SessionId
)
$ErrorActionPreference = 'Stop'
$log = Join-Path $env:ProgramData 'dsh-rdp-wrapper\logs\launcher.log'
function Write-LauncherLog([string]$m) {
  try {
    ("[{0}] [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $PID, $m) |
      Out-File -FilePath $log -Append -Encoding utf8
  } catch { }
}

$cur = [uint32][Diagnostics.Process]::GetCurrentProcess().SessionId
Write-LauncherLog ('entered pid={0} session={1} expected={2} bridge={3}' -f $PID, $cur, $SessionId, $Bridge)

if ($cur -ne $SessionId) {
  Write-LauncherLog 'not inside the child session, exit'
  exit 1
}

Write-LauncherLog ('starting bridge ' + $Bridge)
& $Bridge -Port 0
Write-LauncherLog 'bridge exited'
