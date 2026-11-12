# dsh-rdp-wrapper : launch the child-session host (elevated, silent on this box).
# The host creates a real RDP child session via MsRdpClient10 and starts the
# bridge inside it. ASCII-only source.
param(
  [int]$Width = 1920,
  [int]$Height = 1080,
  [int]$Port = 3389,
  [int]$ExitAfterSec = 0,
  [switch]$NoBridge
)
$ErrorActionPreference = 'Stop'
$PD = "$env:ProgramData\dsh-rdp-wrapper"
$here = $PSScriptRoot
$exe = Join-Path $here 'rdp-child-host.exe'
$bridge = Join-Path (Split-Path $here -Parent) 'bridge\rdp-bridge.ps1'
$log = Join-Path $PD 'logs\childhost.log'
$status = Join-Path $PD 'ports\childhost.json'
if (-not (Test-Path $exe)) { throw "missing $exe (compile ChildHost.cs first)" }
if (-not (Test-Path (Join-Path $PD 'ports'))) { New-Item -ItemType Directory -Path (Join-Path $PD 'ports') -Force | Out-Null }

$argLine = '--width {0} --height {1} --port {2} --log "{3}" --status "{4}"' -f $Width, $Height, $Port, $log, $status
if ($ExitAfterSec -gt 0) { $argLine += (' --exit-after ' + $ExitAfterSec) }
if (-not $NoBridge) { $argLine += (' --bridge "{0}"' -f $bridge) }

$proc = Start-Process -FilePath $exe -ArgumentList $argLine -Verb RunAs -PassThru
Write-Output ('{"ok":true,"pid":' + $proc.Id + ',"status":"' + $status.Replace('\', '\\') + '"}')
