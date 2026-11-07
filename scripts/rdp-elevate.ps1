# dsh-rdp-wrapper : elevate wrapper. Runs scripts\rdp-elevated.ps1 with an
# elevated token (silent on this machine) and returns its JSON result.
# ASCII-only source.
param(
[Parameter(Mandatory)][string]$Action,
[string]$AgentUser = '',
[string]$SessionId = '',
  [string]$BridgeSource = ''
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$rf = Join-Path $env:TEMP ("dsh-rdp-elevated-{0}.json" -f [Guid]::NewGuid().ToString('N'))
$eargs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'rdp-elevated.ps1'),
           '-Action', $Action, '-ResultFile', $rf)
if ($AgentUser) { $eargs += @('-AgentUser', $AgentUser) }
if ($SessionId) { $eargs += @('-SessionId', $SessionId) }
if ($BridgeSource) { $eargs += @('-BridgeSource', $BridgeSource) }
$proc = Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -PassThru -ArgumentList $eargs
if ($proc.ExitCode -ne 0) {
  Write-Output ('{"ok":false,"error":"elevated process exit code ' + $proc.ExitCode + '"}')
  exit 1
}
if (Test-Path $rf) {
  Write-Output (Get-Content $rf -Raw)
  Remove-Item $rf -Force -ErrorAction SilentlyContinue
} else {
  Write-Output '{"ok":false,"error":"elevated script wrote no result file"}'
}
