# =====================================================================
# dsh-rdp-wrapper : in-session computer-use bridge (PowerShell logic layer)
# Runs INSIDE an interactive Windows session (normally the RDP loopback
# session of the dedicated agent user).
# Protocol: JSON-RPC over 127.0.0.1 TCP, one request per connection:
#   client -> {"id":1,"method":"hello","params":{"token":"..."}}
#   client -> {"id":2,"method":"<name>","params":{...}}
#   bridge -> {"id":2,"ok":true,"result":{...}}  |  {"id":2,"ok":false,"error":{...}}
# Advertises itself via a ports file: <PortsDir>\bridge-session-<id>.json
# ASCII-only source (PowerShell 5.1 safe).
# =====================================================================
param(
  [int]$Port = 0,
  [string]$PortsDir = "$env:ProgramData\dsh-rdp-wrapper\ports",
  [string]$DataDir  = "$env:ProgramData\dsh-rdp-wrapper\data",
  [string]$LogDir   = "$env:ProgramData\dsh-rdp-wrapper\logs"
)

$ErrorActionPreference = 'Stop'
$script:Version = '0.1.0'
$script:StartTime = Get-Date

foreach ($d in @($PortsDir, $DataDir, $LogDir, (Join-Path $DataDir 'screenshots'))) {
  if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

function Write-Log([string]$msg) {
  try {
    $sid = (Get-Process -Id $PID).SessionId
    $log = Join-Path $LogDir ("bridge-s{0}.log" -f $sid)
    if ((Test-Path $log) -and ((Get-Item $log).Length -gt 5MB)) {
      Move-Item -Force $log ($log + '.old')
    }
    ("[{0}] [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $PID, $msg) |
      Out-File -FilePath $log -Append -Encoding ascii
  } catch { }
}

Write-Log "bridge starting, version $script:Version"

# ---------------- native core ----------------
Add-Type -Path (Join-Path $PSScriptRoot 'rdp-bridge.cs') -ReferencedAssemblies System.Drawing
$null = [W32]::SetDPIAware()
$null = [W32]::KeepAliveStart()

# ---------------- key names -> virtual-key codes ----------------
$script:VK = @{
  ctrl=0x11; control=0x11; alt=0x12; menu=0x12; shift=0x10; win=0x5B; meta=0x5B; lwin=0x5B; rwin=0x5C;
  enter=0x0D; return=0x0D; tab=0x09; esc=0x1B; escape=0x1B; space=0x20; backspace=0x08; bksp=0x08;
  delete=0x2E; del=0x2E; insert=0x2D; ins=0x2D; home=0x24; end=0x23; pgup=0x21; pgdn=0x22;
  pageup=0x21; pagedown=0x22; up=0x26; down=0x28; left=0x25; right=0x27;
  printscreen=0x2C; prtsc=0x2C; capslock=0x14; numlock=0x90; scrolllock=0x91; pause=0x13; apps=0x5D;
  volumeup=0xAF; volumedown=0xAE; volumemute=0xAD
}
function Get-VKCode([string]$name) {
  $n = $name.ToLower().Trim()
  if ($script:VK.Contains($n)) { return [uint16]$script:VK[$n] }
  if ($n -match '^f(\d{1,2})$') {
    $i = [int]$matches[1]
    if ($i -ge 1 -and $i -le 24) { return [uint16](0x70 + $i - 1) }
  }
  if ($n.Length -eq 1) {
    $c = $n.ToUpper()[0]
    $code = [int]$c
    if (($code -ge 48 -and $code -le 57) -or ($code -ge 65 -and $code -le 90)) { return [uint16]$code }
  }
  throw "unknown key name: $name"
}

function Find-Hwnd {
  param($Handle, [string]$Title, [string]$Class, [string]$Process)
  $h = 0
  if ($Handle) { $h = [int64]$Handle }
  if ($h -ne 0) { return [IntPtr]$h }
  $wins = [W32]::Windows()
  foreach ($w in $wins) {
    $pn = ''
    try { $pn = (Get-Process -Id $w['pid'] -ErrorAction SilentlyContinue).ProcessName } catch { }
    if ($Process -and ($pn -notlike "*$Process*")) { continue }
    if ($Title -and ($w['title'] -like "*$Title*")) { return [IntPtr][int64]$w['handle'] }
    if (-not $Title -and ($Class -or $Process) -and ($Class -eq '' -or $w['class'] -eq $Class)) { return [IntPtr][int64]$w['handle'] }
  }
  return [IntPtr]::Zero
}

# ---------------- exec ----------------
function Invoke-ExecMethod($p) {
  $command = [string]$p.command
  if (-not $command) { throw "exec requires 'command'" }
  $shell = $true; if ($null -ne $p.shell) { $shell = [bool]$p.shell }
  $wait  = $true; if ($null -ne $p.wait) { $wait = [bool]$p.wait }
  $timeoutMs = 30000; if ($null -ne $p.timeoutMs) { $timeoutMs = [int]$p.timeoutMs }
  if ($timeoutMs -lt 1000) { $timeoutMs = 1000 }
  if ($timeoutMs -gt 600000) { $timeoutMs = 600000 }
  $cwd = $null; if ($null -ne $p.cwd) { $cwd = [string]$p.cwd }

  $psi = New-Object System.Diagnostics.ProcessStartInfo
  if ($shell) {
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = '/d /c chcp 65001>nul & ' + $command
  } else {
    $psi.FileName = $command
    if ($p.args) {
      $quoted = @($p.args | ForEach-Object { '"' + ($_ -replace '"','\"') + '"' })
      $psi.Arguments = ($quoted -join ' ')
    }
  }
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.WorkingDirectory = if ($cwd) { $cwd } else { $DataDir }
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  if ($p.env -and $p.env -is [System.Management.Automation.PSCustomObject]) {
    foreach ($pr in $p.env.PSObject.Properties) { $psi.EnvironmentVariables[$pr.Name] = [string]$pr.Value }
  }

  if (-not $wait) {
    $proc = [Diagnostics.Process]::Start($psi)
    $npid = $proc.Id
    try { return @{ pid = $npid; waited = $false } } finally { try { $proc.Dispose() } catch { } }
  }

  $proc = [Diagnostics.Process]::Start($psi)
  $tOut = $proc.StandardOutput.ReadToEndAsync()
  $tErr = $proc.StandardError.ReadToEndAsync()
  $timedOut = $false
  if (-not $proc.WaitForExit($timeoutMs)) {
    $timedOut = $true
    try { $proc.Kill() } catch { }
    try { $proc.WaitForExit(5000) } catch { }
  }
  $stdout = $tOut.GetAwaiter().GetResult()
  $stderr = $tErr.GetAwaiter().GetResult()
  if ($stdout.Length -gt 524288) { $stdout = $stdout.Substring(0, 524288) + "`n[truncated]" }
  if ($stderr.Length -gt 524288) { $stderr = $stderr.Substring(0, 524288) + "`n[truncated]" }
  $exit = -1
  if (-not $timedOut) { $exit = $proc.ExitCode }
  $pidx = $proc.Id
  try { $proc.Dispose() } catch { }
  return @{ pid = $pidx; exitcode = $exit; stdout = $stdout; stderr = $stderr; timedOut = $timedOut }
}

# ---------------- dispatcher ----------------
$script:ShouldStop = $false
$script:Token = [Guid]::NewGuid().ToString('N')
$script:SessionId = (Get-Process -Id $PID).SessionId

function Invoke-Method([string]$method, $p) {
  switch ($method) {
    'ping' { return @{ pong = $true; ts = (Get-Date).ToString('o') } }

    'info' {
      $m = [W32]::Metrics()
      $u = (whoami.exe).Trim()
      return @{
        version = $script:Version
        user = $u
        session = $script:SessionId
        sessionName = $env:SESSIONNAME
        pid = $PID
        screen = @{ x = $m[0]; y = $m[1]; width = $m[2]; height = $m[3] }
        uptimeSec = [int]((Get-Date) - $script:StartTime).TotalSeconds
      }
    }

    'screenshot' {
      $mode = 'full'; if ($null -ne $p.mode) { $mode = [string]$p.mode }
      $bytes = $null
      if ($mode -eq 'full') {
        $m = [W32]::Metrics()
        $bytes = [W32]::CaptureRegion($m[0], $m[1], $m[2], $m[3])
      } elseif ($mode -eq 'region') {
        $bytes = [W32]::CaptureRegion([int]$p.x, [int]$p.y, [int]$p.w, [int]$p.h)
      } elseif ($mode -eq 'window') {
        $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
        if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
        $bytes = [W32]::CaptureWindow($hwnd)
      } else { throw "unknown screenshot mode: $mode" }
      if (-not $bytes -or $bytes.Length -eq 0) { throw "capture produced no data" }
      $file = Join-Path (Join-Path $DataDir 'screenshots') ("shot-{0:yyyyMMdd-HHmmss-fff}.png" -f (Get-Date))
      [IO.File]::WriteAllBytes($file, $bytes)
      return @{ file = $file; bytes = $bytes.Length; mode = $mode }
    }

    'mouse' {
      $action = [string]$p.action
      $mode = 'auto'; if ($null -ne $p.mode) { $mode = [string]$p.mode }
      switch ($action) {
        'move' {
          $null = [W32]::MouseMoveAbs([int]$p.x, [int]$p.y)
          $pt = New-Object W32+POINT
          $null = [W32]::GetCursorPos([ref]$pt)
          return @{ moved = $true; x = $pt.X; y = $pt.Y }
        }
        'click' {
          $null = [W32]::MouseMoveAbs([int]$p.x, [int]$p.y)
          if ($mode -eq 'message') {
            $t = $null
            try { $t = [W32]::MessageClick([int]$p.x, [int]$p.y, 'left', $false) } catch { }
            if (-not $t) { throw 'no window at the click point' }
            return @{ clicked = $true; via = 'message'; target = $t }
          }
          $null = [W32]::MouseClick('left', $false)
          return @{ clicked = $true; via = 'input' }
        }
        'dblclick' {
          $null = [W32]::MouseMoveAbs([int]$p.x, [int]$p.y)
          if ($mode -eq 'message') {
            $t = $null
            try { $t = [W32]::MessageClick([int]$p.x, [int]$p.y, 'left', $true) } catch { }
            if (-not $t) { throw 'no window at the click point' }
            return @{ clicked = $true; double = $true; via = 'message'; target = $t }
          }
          $null = [W32]::MouseClick('left', $true)
          return @{ clicked = $true; double = $true; via = 'input' }
        }
        'rightclick' {
          $waitPopup = $true; if ($null -ne $p.waitPopup) { $waitPopup = [bool]$p.waitPopup }
          $null = [W32]::MouseMoveAbs([int]$p.x, [int]$p.y)
          if ($mode -eq 'message') {
            $t = $null
            try { $t = [W32]::MessageClick([int]$p.x, [int]$p.y, 'right', $false) } catch { }
            if (-not $t) { throw 'no window at the click point' }
            return @{ clicked = $true; button = 'right'; via = 'message'; target = $t }
          }
          $before = [W32]::PopupSnapshot()
          $null = [W32]::MouseClick('right', $false)
          $res = @{ clicked = $true; button = 'right'; via = 'input' }
          if ($waitPopup) {
            $res.popup = [W32]::WaitForPopup(6000, $before)
          }
          return $res
        }
        'middleclick' {
          $null = [W32]::MouseMoveAbs([int]$p.x, [int]$p.y)
          if ($mode -eq 'message') {
            $t = $null
            try { $t = [W32]::MessageClick([int]$p.x, [int]$p.y, 'middle', $false) } catch { }
            if (-not $t) { throw 'no window at the click point' }
            return @{ clicked = $true; button = 'middle'; via = 'message'; target = $t }
          }
          $null = [W32]::MouseClick('middle', $false)
          return @{ clicked = $true; button = 'middle'; via = 'input' }
        }
        'drag' {
          if ($mode -eq 'message') {
            $t = $null
            try { $t = [W32]::MessageDrag([int]$p.x, [int]$p.y, [int]$p.x2, [int]$p.y2, 12) } catch { }
            if (-not $t) { throw 'no window at the drag start point' }
            return @{ dragged = $true; via = 'message'; target = $t }
          }
          $null = [W32]::MouseDrag([int]$p.x, [int]$p.y, [int]$p.x2, [int]$p.y2, 12)
          return @{ dragged = $true; via = 'input' }
        }
        'scroll' {
          $amt = 3; if ($null -ne $p.amount) { $amt = [int]$p.amount }
          $null = [W32]::MouseMoveAbs([int]$p.x, [int]$p.y)
          if ($mode -eq 'message') {
            $t = $null
            try { $t = [W32]::MessageWheel([int]$p.x, [int]$p.y, $amt * 120) } catch { }
            if (-not $t) { throw 'no window at the scroll point' }
            return @{ scrolled = $true; amount = $amt; via = 'message'; target = $t }
          }
          $null = [W32]::MouseWheel($amt * 120)
          return @{ scrolled = $true; amount = $amt; via = 'input' }
        }
        default { throw "unknown mouse action: $action" }
      }
    }

    'key' {
      $action = 'press'; if ($null -ne $p.action) { $action = [string]$p.action }
      if ($action -eq 'type') {
        $text = [string]$p.text
        if (-not $text) { throw "key type requires 'text'" }
        # VK_PACKET unicode typing is unreliable under CJK IMEs (chars get
        # rewritten to garbage). Pure printable-ASCII (plus tab/newline) still
        # goes as real keystrokes; anything else is typed via a clipboard
        # round-trip (save clipboard, set text, ctrl+v, best-effort restore).
        if ($text -match '[^\x09\x0A\x0D\x20-\x7E]') {
          $saved = ''
          try { $saved = [W32]::GetClipboardText() } catch { }
          $null = [W32]::SetClipboardText($text)
          $null = [W32]::KeyCombo([uint16[]]@(0x11, 0x56))
          Start-Sleep -Milliseconds 400
          try { if ($saved -ne '') { $null = [W32]::SetClipboardText($saved) } } catch { }
          return @{ typed = $true; via = 'clipboard'; chars = $text.Length }
        }
        $null = [W32]::TypeText($text)
        return @{ typed = $true; via = 'keys'; chars = $text.Length }
      }
      if ($action -eq 'press') {
        $keys = @([string]$p.keys -split '\+') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        if (-not $keys -or @($keys).Count -eq 0) { throw "key press requires 'keys'" }
        $vks = @($keys | ForEach-Object { Get-VKCode $_ })
        $null = [W32]::KeyCombo([uint16[]]$vks)
        return @{ pressed = ($keys -join '+') }
      }
      throw "unknown key action: $action"
    }

    'window' {
      $action = [string]$p.action
      switch ($action) {
        'list' {
          $wins = [W32]::Windows()
          $out = foreach ($w in $wins) {
            $pn = ''
            try { $pn = (Get-Process -Id $w['pid'] -ErrorAction SilentlyContinue).ProcessName } catch { }
            @{ handle = $w['handle']; title = $w['title']; class = $w['class']; pid = $w['pid']; process = $pn; rect = $w['rect']; minimized = $w['minimized']; maximized = $w['maximized'] }
          }
          return @{ windows = @($out) }
        }
        'focus' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          return @{ focused = [W32]::FocusWindow($hwnd); handle = $hwnd.ToInt64() }
        }
        'minimize' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $null = [W32]::ShowWindow($hwnd, 6)
          return @{ done = $true }
        }
        'maximize' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $null = [W32]::ShowWindow($hwnd, 3)
          return @{ done = $true }
        }
        'restore' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $null = [W32]::ShowWindow($hwnd, 9)
          return @{ done = $true }
        }
        'close' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $null = [W32]::PostMessageW($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
          return @{ done = $true }
        }
        'move' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $r = [W32]::GetRect($hwnd)
          $null = [W32]::MoveWindow($hwnd, [int]$p.x, [int]$p.y, ($r.Right - $r.Left), ($r.Bottom - $r.Top), $true)
          return @{ done = $true }
        }
        'resize' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $r = [W32]::GetRect($hwnd)
          $null = [W32]::MoveWindow($hwnd, $r.Left, $r.Top, [int]$p.w, [int]$p.h, $true)
          return @{ done = $true }
        }
        'rect' {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
          $r = [W32]::GetRect($hwnd)
          return @{ x = $r.Left; y = $r.Top; w = ($r.Right - $r.Left); h = ($r.Bottom - $r.Top) }
        }
        'wait' {
          $tmo = 15000; if ($null -ne $p.timeoutMs) { $tmo = [int]$p.timeoutMs }
          $deadline = (Get-Date).AddMilliseconds($tmo)
          while ((Get-Date) -lt $deadline) {
            $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
            if ($hwnd -ne [IntPtr]::Zero) { return @{ found = $true; handle = $hwnd.ToInt64() } }
            Start-Sleep -Milliseconds 250
          }
          return @{ found = $false }
        }
        default { throw "unknown window action: $action" }
      }
    }

    'exec' {
      return (Invoke-ExecMethod $p)
    }

    'clipboard' {
      $action = 'get'; if ($null -ne $p.action) { $action = [string]$p.action }
      if ($action -eq 'get') { return @{ text = [W32]::GetClipboardText() } }
      if ($action -eq 'set') { $null = [W32]::SetClipboardText([string]$p.text); return @{ set = $true } }
      throw "unknown clipboard action: $action"
    }

    'shutdown' {
      $script:ShouldStop = $true
      return @{ stopping = $true }
    }

    default { throw "unknown method: $method" }
  }
}

# ---------------- server loop ----------------
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
$script:Port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port

$portsFile = Join-Path $PortsDir ("bridge-session-{0}.json" -f $script:SessionId)
$portsPayload = @{
  port = $script:Port
  token = $script:Token
  pid = $PID
  session = $script:SessionId
  sessionName = $env:SESSIONNAME
  user = (whoami.exe).Trim()
  started = (Get-Date).ToString('o')
  version = $script:Version
} | ConvertTo-Json -Compress
[IO.File]::WriteAllText($portsFile, $portsPayload, (New-Object Text.UTF8Encoding($false)))

Write-Log ("listening on 127.0.0.1:{0} (session {1}), ports file: {2}" -f $script:Port, $script:SessionId, $portsFile)

while ($true) {
  if ($script:ShouldStop) { break }
  $client = $listener.AcceptTcpClient()
  try {
    $client.ReceiveTimeout = 60000
    $client.SendTimeout = 60000
    $stream = $client.GetStream()
    $reader = New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding($false)))
    $writer = New-Object IO.StreamWriter($stream, (New-Object Text.UTF8Encoding($false)))
    $writer.NewLine = "`n"

    # --- hello (token check) ---
    $line = $reader.ReadLine()
    if (-not $line) { throw "empty hello" }
    $hello = $line | ConvertFrom-Json
    if ($hello.method -ne 'hello') { throw "expected hello" }
    if ($hello.params.token -ne $token) {
      $writer.WriteLine('{"id":1,"ok":false,"error":{"code":401,"message":"bad token"}}')
      $writer.Flush()
      throw "bad token from client"
    }
    $writer.WriteLine('{"id":1,"ok":true,"result":{"bridge":"dsh-rdp-wrapper","version":"' + $script:Version + '"}}')
    $writer.Flush()

    # --- single request ---
    $reqLine = $reader.ReadLine()
    if (-not $reqLine) { throw "empty request" }
    $req = $reqLine | ConvertFrom-Json
    $resp = $null
    try {
      $result = Invoke-Method $req.method $req.params
      $resp = @{ id = $req.id; ok = $true; result = $result }
    } catch {
      $resp = @{ id = $req.id; ok = $false; error = @{ code = 500; message = ("{0}: {1}" -f $_.Exception.GetType().Name, $_.Exception.Message) } }
    }
    $json = $resp | ConvertTo-Json -Compress -Depth 8
    $writer.WriteLine($json)
    $writer.Flush()
    Write-Log ("req {0} ok={1}" -f $req.method, $resp.ok)
  } catch {
    Write-Log ("connection error: {0}" -f $_.Exception.Message)
  } finally {
    try { $client.Close() } catch { }
  }
  if ($script:ShouldStop) { break }
}

# ---------------- cleanup ----------------
try { $listener.Stop() } catch { }
try { Remove-Item $portsFile -Force -ErrorAction SilentlyContinue } catch { }
Write-Log "bridge stopped"
