# =====================================================================
# dsh-rdp-wrapper : elevated dispatcher (runs with admin token, silently
# elevated via UAC "elevate without prompting" on this machine).
# Always writes a JSON result file; stdout of the elevated process is lost.
# ASCII-only source.
# =====================================================================
param(
  [string]$Action = '',
  [string]$ResultFile = '',
[string]$AgentUser = '',
[int]$ScreenW = 1920,
  [int]$ScreenH = 1080,
  [string]$BridgeSource = '',
  [string]$SessionId = ''
)

$ErrorActionPreference = 'Stop'
$PD = "$env:ProgramData\dsh-rdp-wrapper"
$result = @{ ok = $false; action = $Action }

try {
  switch ($Action) {

    'provision' {
      if (-not $AgentUser) { $AgentUser = $env:USERNAME }

      # ---- directories + ACLs ----
      foreach ($d in @($PD, "$PD\ports", "$PD\data", "$PD\logs", "$PD\bridge", "$PD\data\screenshots")) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
      }
      icacls $PD /grant "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" "SYSTEM:(OI)(CI)F" | Out-Null
      foreach ($d in @("$PD\ports", "$PD\data", "$PD\logs")) {
        icacls $d /grant "${AgentUser}:(OI)(CI)M" | Out-Null
      }
      icacls "$PD\bridge" /grant "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

      # ---- bridge copy (child host launches this copy inside the session) ----
      if ($BridgeSource) {
        Copy-Item "$BridgeSource\rdp-bridge.cs" "$PD\bridge\" -Force
        Copy-Item "$BridgeSource\rdp-bridge.ps1" "$PD\bridge\" -Force
      }

      # ---- config (child-session model: no dedicated account, no logon task) ----
      $cfg = @{
        agentUser = $AgentUser
        screenW = $ScreenW
        screenH = $ScreenH
        rdpHost = '127.0.0.1'
        provisionedAt = (Get-Date).ToString('o')
        version = '0.1.0'
      } | ConvertTo-Json -Compress
      [IO.File]::WriteAllText("$PD\config.json", $cfg, (New-Object Text.UTF8Encoding($false)))

      # ---- RDP host guard ----
      Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
      $result.user = $AgentUser
      $result.note = 'child sessions clone the interactive logon; no agent account or logon task is created'
    }

    'run-task' {
      throw "run-task is obsolete: the child host starts the bridge through a per-session task"
    }

    'logoff' {
      if (-not $SessionId) { throw "logoff requires SessionId" }
      logoff $SessionId | Out-Null
      $result.loggedOff = [int]$SessionId
    }

    'enable-rdp' {
      Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
      try { Start-Service TermService -ErrorAction SilentlyContinue } catch { }
      $result.enabled = $true
    }

    default { throw "unknown action: $Action" }
  }
  $result.ok = $true
} catch {
  $result.error = $_.Exception.Message
}

if ($ResultFile) {
  [IO.File]::WriteAllText($ResultFile, ($result | ConvertTo-Json -Compress -Depth 6), (New-Object Text.UTF8Encoding($false)))
}
