# dsh-rdp-wrapper : read-only preflight/doctor. ASCII-only source.
$ErrorActionPreference = 'Continue'
$out = [ordered]@{
  os = (Get-CimInstance Win32_OperatingSystem).Caption
  user = (whoami.exe).Trim()
  isAdmin = $false
}
try {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $out.isAdmin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

# RDP host
$out.termsrv = @{ }
try {
  $svc = Get-Service TermService -ErrorAction Stop
  $out.termsrv.status = $svc.Status.ToString()
  $out.termsrv.startType = $svc.StartType.ToString()
} catch { $out.termsrv.status = 'not found' }
try { $out.fDenyTSConnections = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -ErrorAction Stop).fDenyTSConnections } catch { $out.fDenyTSConnections = 'unknown' }
try {
  $conn = Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction Stop | Select-Object -First 1
  $out.port3389 = 'LISTENING (pid ' + $conn.OwningProcess + ')'
} catch { $out.port3389 = 'not listening' }
try { $out.termsrvVersion = (Get-Item "$env:SystemRoot\System32\termsrv.dll").VersionInfo.FileVersion } catch { }
$out.rdpwrap = @{
  installed = (Test-Path 'C:\Program Files\RDP Wrapper')
  ini = if (Test-Path 'C:\Program Files\RDP Wrapper\rdpwrap.ini') { (Get-Item 'C:\Program Files\RDP Wrapper\rdpwrap.ini').Length } else { 0 }
}

# sessions
$sess = @()
try {
  $quser = quser 2>$null
  for ($i = 1; $i -lt $quser.Count; $i++) {
    $t = $quser[$i] -split '\s{2,}'
    $t = @($t | Where-Object { $_.Trim() })
    $numIdx = -1
    for ($j = 0; $j -lt $t.Count; $j++) { if ($t[$j] -match '^\d+$') { $numIdx = $j; break } }
    if ($numIdx -lt 0) { continue }
    $sess += @{
      user = ($t[0] -replace '^>', '')
      sessionName = if ($numIdx -ge 1 -and $numIdx -gt 0) { $t[$numIdx - 1] } else { '' }
      id = [int]$t[$numIdx]
      state = $t[$numIdx + 1]
    }
  }
} catch { }
$out.sessions = $sess

# plugin state
$PD = "$env:ProgramData\dsh-rdp-wrapper"
$out.provisioned = Test-Path "$PD\config.json"
if ($out.provisioned) {
  try { $out.config = Get-Content "$PD\config.json" -Raw | ConvertFrom-Json } catch { }
  $out.childHostExe = Test-Path (Join-Path $PSScriptRoot 'childhost\rdp-child-host.exe')
  $out.childBridgeScript = Test-Path (Join-Path $PSScriptRoot 'bridge\rdp-bridge.ps1')
  $out.childHostStatus = $null
  try {
    if (Test-Path "$PD\ports\childhost.json") {
      $out.childHostStatus = Get-Content "$PD\ports\childhost.json" -Raw | ConvertFrom-Json
    }
  } catch { }
}
try {
  $out.childSessionsEnabled = [bool](Get-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name WTSEnableChildSessions -ErrorAction Stop).WTSEnableChildSessions
} catch { $out.childSessionsEnabled = $false }

# bridges (ports files + TCP probe)
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
$out | ConvertTo-Json -Depth 6
