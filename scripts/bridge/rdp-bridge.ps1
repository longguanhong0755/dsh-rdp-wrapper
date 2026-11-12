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
$script:Version = '0.5.0'
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
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Drawing
$bridgeRefs = @(
  [System.Windows.Automation.AutomationElement].Assembly.Location,
  [System.Windows.Automation.ControlType].Assembly.Location,
  [System.Windows.Rect].Assembly.Location,
  [System.Drawing.Bitmap].Assembly.Location
)
Add-Type -Path (Join-Path $PSScriptRoot 'rdp-bridge.cs') -ReferencedAssemblies $bridgeRefs
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
  # A caller can remember a handle across calls, so verify it still exists:
  # returning it blindly made wait(gone=true) burn the full timeout.
  if ($h -ne 0) {
    if ([W32]::IsWindow([IntPtr]$h)) { return [IntPtr]$h }
    return [IntPtr]::Zero
  }
  if ($Process -and $Process.ToLowerInvariant().EndsWith('.exe')) {
    $Process = $Process.Substring(0, $Process.Length - 4)
  }
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

# Resolve the window a uia/state call should describe. With no filter the
# foreground window is used (what the agent is looking at right now).
function Resolve-TargetWindow($p) {
  $hasFilter = $false
  if ($null -ne $p.handle -and [int64]$p.handle -ne 0) { $hasFilter = $true }
  if ($p.title -or $p.class -or $p.process) { $hasFilter = $true }
  if (-not $hasFilter) {
    $fg = Get-ActiveRootWindow
    if ($fg -eq [IntPtr]::Zero) { throw 'no foreground window' }
    return $fg
  }
  $h = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
  if ($h -eq [IntPtr]::Zero) { throw 'window not found (handle/title/class/process filter matched nothing)' }
  return $h
}

# Focus may sit on a child control (an Edit after typing). Use its top-level
# window: UIA searches from a leaf would otherwise return no descendants.
function Get-ActiveRootWindow() {
  $fg = [W32]::GetForegroundWindow()
  if ($fg -eq [IntPtr]::Zero) { return $fg }
  $root = [W32]::GetAncestor($fg, 2) # GA_ROOT
  if ($root -ne [IntPtr]::Zero) { return $root }
  return $fg
}

function Get-WindowInfo([IntPtr]$hwnd) {
  $t = New-Object Text.StringBuilder 512
  $null = [W32]::GetWindowTextW($hwnd, $t, 512)
  $c = New-Object Text.StringBuilder 256
  $null = [W32]::GetClassNameW($hwnd, $c, 256)
  $wpid = [uint32]0
  $null = [W32]::GetWindowThreadProcessId($hwnd, [ref]$wpid)
  $pn = ''
  try { $pn = (Get-Process -Id ([int]$wpid) -ErrorAction SilentlyContinue).ProcessName } catch { }
  $r = [W32]::GetRect($hwnd)
  return @{
    handle = $hwnd.ToInt64()
    title = $t.ToString()
    class = $c.ToString()
    pid = [int]$wpid
    process = $pn
    rect = @{ x = $r.Left; y = $r.Top; w = ($r.Right - $r.Left); h = ($r.Bottom - $r.Top) }
    minimized = [W32]::IsIconic($hwnd)
    maximized = [W32]::IsZoomed($hwnd)
  }
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
      $originX = 0
      $originY = 0
      if ($mode -eq 'full') {
        $m = [W32]::Metrics()
        $originX = $m[0]; $originY = $m[1]
        $bytes = [W32]::CaptureRegion($m[0], $m[1], $m[2], $m[3])
      } elseif ($mode -eq 'region') {
        $originX = [int]$p.x; $originY = [int]$p.y
        $bytes = [W32]::CaptureRegion([int]$p.x, [int]$p.y, [int]$p.w, [int]$p.h)
      } elseif ($mode -eq 'window') {
        $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
        if ($hwnd -eq [IntPtr]::Zero) { throw "window not found" }
        $bytes = [W32]::CaptureWindow($hwnd)
        # Window captures start at the window's top-left, so report that
        # origin: mouse coordinates = origin + pixel in the returned image.
        $wr = New-Object W32+RECT
        if ([W32]::GetWindowRect($hwnd, [ref]$wr)) { $originX = $wr.Left; $originY = $wr.Top }
      } else { throw "unknown screenshot mode: $mode" }
      if (-not $bytes -or $bytes.Length -eq 0) { throw "capture produced no data" }
      $file = Join-Path (Join-Path $DataDir 'screenshots') ("shot-{0:yyyyMMdd-HHmmss-fff}.png" -f (Get-Date))
      [IO.File]::WriteAllBytes($file, $bytes)
      $out = @{ file = $file; bytes = $bytes.Length; mode = $mode; origin = @{ x = $originX; y = $originY } }
      # savePath is an extra copy the caller may rely on (e.g. dropping evidence
      # into the workspace). Report it explicitly: silently ignoring it made an
      # agent believe the file existed when it did not.
      if ($p.savePath) {
        $dst = [string]$p.savePath
        if (-not [IO.Path]::IsPathRooted($dst)) {
          $dst = Join-Path ([Environment]::GetFolderPath('UserProfile')) $dst
        }
        try {
          $dstDir = [IO.Path]::GetDirectoryName($dst)
          if ($dstDir -and -not (Test-Path -LiteralPath $dstDir)) {
            $null = New-Item -ItemType Directory -Force -Path $dstDir
          }
          [IO.File]::Copy($file, $dst, $true)
          $out.savedTo = $dst
        } catch {
          $out.saveError = $_.Exception.Message
        }
      }
      $img = $null
      try { $img = [System.Drawing.Image]::FromFile($file) } catch { }
      if ($img) {
        try { $out.width = $img.Width; $out.height = $img.Height } finally { $img.Dispose() }
      }
      return $out
    }

    'mouse' {
      $action = [string]$p.action
      $mode = 'auto'; if ($null -ne $p.mode) { $mode = [string]$p.mode }
      if (@('move','click','dblclick','rightclick','middleclick','drag','scroll') -contains $action) {
        if ($null -eq $p.x -or $null -eq $p.y) { throw "mouse $action requires x and y" }
      }
      if ($action -eq 'drag' -and ($null -eq $p.x2 -or $null -eq $p.y2)) {
        throw 'mouse drag requires x2 and y2 (refusing to drag to an implicit 0,0)'
      }
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
        $clear = $false; if ($null -ne $p.clear) { $clear = [bool]$p.clear }
        if ($clear) {
          $null = [W32]::KeyCombo([uint16[]]@(0x11, 0x41))
          Start-Sleep -Milliseconds 60
        }
        # VK_PACKET unicode typing is unreliable under CJK IMEs (chars get
        # rewritten to garbage). Pure printable-ASCII (plus tab/newline) still
        # goes as real keystrokes; anything else is pasted via the clipboard
        # (save clipboard, set text, ctrl+v, best-effort restore).
        if ($text -match '[^\x09\x0A\x0D\x20-\x7E]') {
          $null = [W32]::PasteText($text)
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
          $pf = ''
          if ($p.process) {
            $pf = [string]$p.process
            if ($pf.ToLowerInvariant().EndsWith('.exe')) { $pf = $pf.Substring(0, $pf.Length - 4) }
          }
          $out = foreach ($w in $wins) {
            if ($null -ne $p.handle -and ([int64]$w['handle'] -ne [int64]$p.handle)) { continue }
            $pn = ''
            try { $pn = (Get-Process -Id $w['pid'] -ErrorAction SilentlyContinue).ProcessName } catch { }
            if ($pf -and ($pn -notlike ("*" + $pf + "*"))) { continue }
            if ($p.title -and ([string]$w['title'] -notlike ("*" + [string]$p.title + "*"))) { continue }
            if ($p.class -and ([string]$w['class'] -notlike ("*" + [string]$p.class + "*"))) { continue }
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

    # One-call situational snapshot: target window, compact window list,
    # numbered actionable UIA elements and an annotated screenshot whose
    # labels match the element indices. This replaces the old
    # screenshot -> guess coordinates -> click loop.
    'state' {
      $vision = $true; if ($null -ne $p.vision) { $vision = [bool]$p.vision }
      $wantWins = $true; if ($null -ne $p.windows) { $wantWins = [bool]$p.windows }
      $interactive = $true; if ($null -ne $p.interactive) { $interactive = [bool]$p.interactive }
      $max = 150; if ($null -ne $p.max) { $max = [int]$p.max }
      if ($max -lt 1) { $max = 150 }
      if ($max -gt 400) { $max = 400 }
      $windowMax = 25; if ($null -ne $p.windowMax) { $windowMax = [int]$p.windowMax }
      $hwnd = Resolve-TargetWindow $p
      $target = Get-WindowInfo $hwnd
      if ([W32]::IsHungAppWindow($hwnd)) { throw 'target window is not responding; use rdp_screenshot + rdp_mouse instead' }
      $u = [Uia]::List([int64]$hwnd, $interactive, $max, [string]$p.role)
      $res = @{
        target = $target
        active = (Get-WindowInfo (Get-ActiveRootWindow))
        elements = $u['elements']
        total = $u['total']
        truncated = $u['truncated']
      }
      if ($wantWins) {
        $compact = @()
        foreach ($w in [W32]::Windows()) {
          if (-not $w['title']) { continue }
          $pn = ''
          try { $pn = (Get-Process -Id $w['pid'] -ErrorAction SilentlyContinue).ProcessName } catch { }
          $compact += @{
            handle = $w['handle']; title = $w['title']; process = $pn; class = $w['class']
            rect = $w['rect']; minimized = $w['minimized']
          }
        }
        $res.windowCount = $compact.Count
        $res.windows = @($compact | Select-Object -First $windowMax)
      }
      if ($vision) {
        $m = [W32]::Metrics()
        $boxes = New-Object System.Collections.Generic.List[int]
        $labels = New-Object System.Collections.Generic.List[string]
        foreach ($e in @($u['elements'])) {
          if ([int]$e['w'] -gt 0 -and [int]$e['h'] -gt 0) {
            $boxes.Add([int]$e['x']); $boxes.Add([int]$e['y'])
            $boxes.Add([int]$e['w']); $boxes.Add([int]$e['h'])
            $labels.Add([string]$e['i'])
          }
        }
        $bytes = [W32]::CaptureAnnotated($m[0], $m[1], $m[2], $m[3], $boxes.ToArray(), $labels.ToArray(), $true)
        $file = Join-Path (Join-Path $DataDir 'screenshots') ("state-{0:yyyyMMdd-HHmmss-fff}.png" -f (Get-Date))
        [IO.File]::WriteAllBytes($file, $bytes)
        $res.file = $file
        $res.bytes = $bytes.Length
        $res.screen = @{ x = $m[0]; y = $m[1]; width = $m[2]; height = $m[3] }
      }
      return $res
    }

    # UI Automation actions. Indices come from the latest state/list/find and
    # stay valid until the next list (find/wait append to the cache).
    'uia' {
      $action = [string]$p.action
      switch ($action) {
        'list' {
          $hwnd = Resolve-TargetWindow $p
          $interactive = $true; if ($null -ne $p.interactive) { $interactive = [bool]$p.interactive }
          $max = 200; if ($null -ne $p.max) { $max = [int]$p.max }
          $u = [Uia]::List([int64]$hwnd, $interactive, $max, [string]$p.role)
          return @{ target = (Get-WindowInfo $hwnd); elements = $u['elements']; total = $u['total']; truncated = $u['truncated'] }
        }
        'find' {
          $hwnd = Resolve-TargetWindow $p
          # find defaults to ALL named/value elements (Text labels included),
          # because locating content is the usual reason to call it; list
          # defaults to actionable controls.
          $interactive = $false; if ($null -ne $p.interactive) { $interactive = [bool]$p.interactive }
          $max = 30; if ($null -ne $p.max) { $max = [int]$p.max }
          return [Uia]::Find([int64]$hwnd, [string]$p.text, [string]$p.role, $interactive, $max)
        }
        'get' {
          if ($null -eq $p.i) { throw "uia get requires 'i'" }
          return [Uia]::Get([int]$p.i)
        }
        'click' {
          $hwnd = [int64]0; if ($null -ne $p.handle) { $hwnd = [int64]$p.handle }
          $idx = -1; if ($null -ne $p.i) { $idx = [int]$p.i }
          return [Uia]::Click($idx, [string]$p.text, [string]$p.role, $hwnd, [bool]$p.double)
        }
        'setvalue' {
          $hwnd = [int64]0; if ($null -ne $p.handle) { $hwnd = [int64]$p.handle }
          $idx = -1; if ($null -ne $p.i) { $idx = [int]$p.i }
          if ($null -eq $p.value) { throw "uia setvalue requires 'value'" }
          return [Uia]::SetValue($idx, [string]$p.text, [string]$p.role, $hwnd, [string]$p.value, [bool]$p.submit, [bool]$p.clear)
        }
        'focus' {
          $hwnd = [int64]0; if ($null -ne $p.handle) { $hwnd = [int64]$p.handle }
          $idx = -1; if ($null -ne $p.i) { $idx = [int]$p.i }
          return [Uia]::Focus($idx, [string]$p.text, [string]$p.role, $hwnd)
        }
        'wait' {
          $hwnd = [int64]0; if ($null -ne $p.handle) { $hwnd = [int64]$p.handle }
          $tmo = 15000; if ($null -ne $p.timeoutMs) { $tmo = [int]$p.timeoutMs }
          return [Uia]::WaitForElement($hwnd, [string]$p.text, [string]$p.role, $tmo, [bool]$p.gone)
        }
        default { throw "unknown uia action: $action" }
      }
    }

    'wait' {
      $mode = 'element'; if ($p.mode) { $mode = [string]$p.mode }
      $tmo = 15000; if ($null -ne $p.timeoutMs) { $tmo = [int]$p.timeoutMs }
      if ($tmo -lt 100) { $tmo = 100 }
      if ($tmo -gt 180000) { $tmo = 180000 }
      if ($mode -eq 'element') {
        $hwnd = [int64]0; if ($null -ne $p.handle) { $hwnd = [int64]$p.handle }
        $gone = $false; if ($null -ne $p.gone) { $gone = [bool]$p.gone }
        $u = [Uia]::WaitForElement($hwnd, [string]$p.text, [string]$p.role, $tmo, $gone)
        $u['mode'] = 'element'
        return $u
      }
      if ($mode -eq 'window') {
        $gone = $false; if ($null -ne $p.gone) { $gone = [bool]$p.gone }
        $start = Get-Date
        while (((Get-Date) - $start).TotalMilliseconds -lt $tmo) {
          $hwnd = Find-Hwnd -Handle $p.handle -Title $p.title -Class $p.class -Process $p.process
          $exists = ($hwnd -ne [IntPtr]::Zero)
          if ($gone -and -not $exists) {
            return @{ found = $true; gone = $true; mode = 'window'; waitedMs = [int]((Get-Date) - $start).TotalMilliseconds }
          }
          if (-not $gone -and $exists) {
            return @{ found = $true; mode = 'window'; waitedMs = [int]((Get-Date) - $start).TotalMilliseconds; window = (Get-WindowInfo $hwnd) }
          }
          Start-Sleep -Milliseconds 250
        }
        return @{ found = $false; gone = $gone; mode = 'window'; waitedMs = [int]((Get-Date) - $start).TotalMilliseconds }
      }
      if ($mode -eq 'idle' -or $mode -eq 'change') {
        $m = [W32]::Metrics()
        $x = $m[0]; $y = $m[1]; $w = $m[2]; $h = $m[3]
        if ($null -ne $p.x) { $x = [int]$p.x }
        if ($null -ne $p.y) { $y = [int]$p.y }
        if ($null -ne $p.width) { $w = [int]$p.width }
        if ($null -ne $p.height) { $h = [int]$p.height }
        $stable = 700; if ($null -ne $p.stableMs) { $stable = [int]$p.stableMs }
        $r = [W32]::WaitScreen($mode, $x, $y, $w, $h, $stable, $tmo)
        $r['mode'] = $mode
        return $r
      }
      throw "unknown wait mode: $mode"
    }

    # Launch an app (Start Menu display name, UWP app or exe path) and wait
    # for its window, so one call replaces exec + window list + focus polling.
    'app' {
      $action = 'launch'; if ($p.action) { $action = [string]$p.action }
      if ($action -ne 'launch') { throw "unknown app action: $action" }
      $name = [string]$p.name
      $path = [string]$p.path
      if (-not $name -and -not $path) { throw "app launch requires 'name' or 'path'" }
      $tmo = 20000; if ($null -ne $p.timeoutMs) { $tmo = [int]$p.timeoutMs }
      if ($tmo -lt 1000) { $tmo = 1000 }
      $argList = $null
      if ($null -ne $p.args) { $argList = @($p.args | ForEach-Object { [string]$_ }) }
      $before = @{}
      $beforePids = @{}
      foreach ($pr in (Get-Process -ErrorAction SilentlyContinue)) { $beforePids[[int]$pr.Id] = $true }
      foreach ($w in [W32]::Windows()) { $before[[int64]$w['handle']] = $true }
      $fgBefore = (Get-ActiveRootWindow).ToInt64()
      $launchedPid = 0
      $launchedName = ''
      $appId = ''
      $matchType = ''
      if ($path) {
        if ($argList) { $proc = Start-Process -FilePath $path -ArgumentList $argList -PassThru }
        else { $proc = Start-Process -FilePath $path -PassThru }
        $launchedPid = $proc.Id
        $matchType = 'path'
      } else {
        $app = $null
        try { $app = Get-StartApps | Where-Object { $_.Name -eq $name } | Select-Object -First 1 } catch { }
        if ($app) { $matchType = 'exact' }
        if (-not $app) {
          # Start-with beats contains: "Notepad" should not silently become
          # "Notepad++" when an exact entry is missing, but a reported
          # close match is still more useful than failing outright.
          try { $app = Get-StartApps | Where-Object { $_.Name -like ($name + '*') } | Sort-Object { $_.Name.Length } | Select-Object -First 1 } catch { }
          if ($app) { $matchType = 'prefix' }
        }
        if (-not $app) {
          try { $app = Get-StartApps | Where-Object { $_.Name -like ('*' + $name + '*') } | Sort-Object { $_.Name.Length } | Select-Object -First 1 } catch { }
          if ($app) { $matchType = 'contains' }
        }
        if ($app) {
          $launchedName = [string]$app.Name
          $appId = [string]$app.AppID
          Start-Process explorer.exe ('shell:AppsFolder\' + $app.AppID)
        } else {
          # Common Start Menu names that differ from their executable.
          $alias = @{
            'paint' = 'mspaint.exe'; 'wordpad' = 'write.exe'; 'calculator' = 'calc.exe';
            'task manager' = 'taskmgr.exe'; 'snipping tool' = 'snippingtool.exe';
            'control panel' = 'control.exe'; 'file explorer' = 'explorer.exe';
            'command prompt' = 'cmd.exe'; 'registry editor' = 'regedit.exe'
          }
          $exe = $name
          $key = $name.ToLowerInvariant()
          if ($alias.ContainsKey($key)) { $exe = $alias[$key] }
          $matchType = 'exe'
          try {
            if ($argList) { $proc = Start-Process -FilePath $exe -ArgumentList $argList -PassThru }
            else { $proc = Start-Process -FilePath $exe -PassThru }
            $launchedPid = $proc.Id
          } catch {
            $names = @()
            try { $names = @(Get-StartApps | Where-Object { $_.Name -like ('*' + $name + '*') } | Select-Object -First 8 -ExpandProperty Name) } catch { }
            $hint = "no app named '" + $name + "' could be started. Pass path=<full exe path> instead"
            if ($names.Count -gt 0) { $hint = $hint + '; Start Menu matches: ' + ($names -join ', ') }
            throw $hint
          }
        }
      }

      # Identify the new window by score, not by "first window that appeared":
      # startup apps and background helpers (Windows Terminal, conhost, ...)
      # can surface at the same moment and must not be mistaken for the app.
      $hint = ''
      if ($path) { $hint = [IO.Path]::GetFileNameWithoutExtension($path) }
      elseif ($launchedName) { $hint = $launchedName }
      else { $hint = $name }
      $hint = ($hint -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
      $infra = @('windowsterminal', 'openconsole', 'conhost', 'powershell', 'pwsh', 'cmd', 'textinputhost', 'searchhost', 'startmenuexperiencehost', 'shellexperiencehost', 'dwm', 'csrss', 'winlogon')
      # A caller may pass process="Foo.exe" while window enumeration reports
      # "Foo": normalise instead of silently matching nothing for 20 seconds.
      $procFilter = ''
      if ($p.process) {
        $procFilter = [string]$p.process
        if ($procFilter.ToLowerInvariant().EndsWith('.exe')) {
          $procFilter = $procFilter.Substring(0, $procFilter.Length - 4)
        }
      }
      $start = Get-Date
      $found = $null
      $foundScore = -9999
      $alt = $null
      $altScore = -9999
      $filterIgnored = $false
      while (((Get-Date) - $start).TotalMilliseconds -lt $tmo) {
        $fgNow = (Get-ActiveRootWindow).ToInt64()
        $found = $null
        $foundScore = -9999
        foreach ($w in [W32]::Windows()) {
          $h = [int64]$w['handle']
          if ($before.ContainsKey($h)) { continue }
          if (-not $w['title']) { continue }
          $pn = ''
          try { $pn = (Get-Process -Id $w['pid'] -ErrorAction SilentlyContinue).ProcessName } catch { }
          $cls = [string]$w['class']
          if ($cls -eq 'Shell_TrayWnd' -or $cls -eq 'Progman' -or $cls -eq 'WorkerW' -or $cls -eq '#32768') { continue }
          $pl = $pn.ToLowerInvariant()
          # Best new window regardless of the caller's filters. If the filters
          # were slightly wrong (e.g. "Foo.exe" vs "Foo", or a stale title),
          # this lets us return the launched app instead of timing out.
          $altS = 0
          if ($hint.Length -ge 3) {
            if ($pl.StartsWith($hint) -or $hint.StartsWith($pl)) { $altS += 40 }
            elseif ($pl.Contains($hint) -or (([string]$w['title']).ToLowerInvariant().Contains($hint))) { $altS += 25 }
          }
          if (-not $beforePids.ContainsKey([int]$w['pid'])) { $altS += 20 }
          if (-not $w['minimized']) { $altS += 10 }
          if ($h -eq $fgNow) { $altS += 15 }
          if ($infra -contains $pl) { $altS -= 60 }
          if ($cls -eq 'CASCADIA_HOSTING_WINDOW_CLASS') { $altS -= 30 }
          if ($altS -gt $altScore) { $altScore = $altS; $alt = $w }
          if ($procFilter -and ($pn -notlike ("*" + $procFilter + "*"))) { continue }
          if ($p.title -and ($w['title'] -notlike ("*" + [string]$p.title + "*"))) { continue }
          $score = $altS
          if ($score -gt $foundScore) { $foundScore = $score; $found = $w }
        }
        if ($found -and $foundScore -ge 20) { break }
        # Wrong filters still deserve a fast, explicit recovery: after a short
        # grace period accept a strong unfiltered candidate and say so.
        if (($procFilter -or $p.title) -and $alt -and $altScore -ge 60 -and ((Get-Date) - $start).TotalSeconds -ge 2.5) {
          $found = $alt; $foundScore = $altScore; $filterIgnored = $true
          break
        }
        Start-Sleep -Milliseconds 250
      }
      if (-not $found -and $alt -and $altScore -ge 20 -and ($procFilter -or $p.title)) {
        $found = $alt; $foundScore = $altScore; $filterIgnored = $true
      }
      if (-not $found) {
        # Single-instance apps reuse an existing window: accept a foreground
        # change only when it plausibly is the requested app.
        $fg = Get-ActiveRootWindow
        if ($fg -ne [IntPtr]::Zero -and $fg.ToInt64() -ne $fgBefore) {
          $info = Get-WindowInfo $fg
          $okP = $true; if ($procFilter) { $okP = ($info.process -like ("*" + $procFilter + "*")) }
          $okT = $true; if ($p.title) { $okT = ($info.title -like ("*" + [string]$p.title + "*")) }
          $fgPl = ([string]$info.process).ToLowerInvariant()
          $okH = ($hint.Length -lt 3) -or $fgPl.Contains($hint) -or $hint.Contains($fgPl) -or (([string]$info.title).ToLowerInvariant().Contains($hint))
          if ($okP -and $okT -and $okH) { $found = @{ handle = $fg.ToInt64() }; $filterIgnored = $false }
        }
      }
      $res = @{ launched = $true; waitedMs = [int]((Get-Date) - $start).TotalMilliseconds; found = ($null -ne $found) }
      if ($filterIgnored) {
        $res.filterIgnored = $true
        $res.warning = 'window filters did not match; used the best new window'
      }
      if ($matchType) { $res.match = $matchType }
      if ($foundScore -gt -9999) { $res.score = $foundScore }
      if ($launchedPid -ne 0) { $res.pid = $launchedPid }
      if ($launchedName) { $res.launchedName = $launchedName }
      if ($appId) { $res.appId = $appId }
      if ($found) {
        $res.handle = $found['handle']
        if ($found['title']) { $res.title = $found['title'] }
        if ($found['class']) { $res.class = $found['class'] }
        $res.window = (Get-WindowInfo ([IntPtr][int64]$found['handle']))
      }
      return $res
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
