# dsh-rdp-wrapper : synthetic mouse click probe.
# Opens its own WinForms window with two buttons, clicks one via SendInput and
# one via legacy mouse_event, then reports whether the Click handlers fired.
# Run with -STA. Use -Out to write the JSON result to a file.
param([string]$Out = '')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public class ProbeInput {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cb);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }

  public static uint SendClick() {
    INPUT[] a = new INPUT[2];
    a[0].type = 0; a[0].mi.dwFlags = 0x0002;
    a[1].type = 0; a[1].mi.dwFlags = 0x0004;
    return SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
  }

  public static void LegacyClick() {
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
  }

  public static string Cursor() {
    POINT p; GetCursorPos(out p); return p.X + "," + p.Y;
  }

  public static string FgTitle() {
    IntPtr h = GetForegroundWindow();
    StringBuilder sb = new StringBuilder(256);
    GetWindowText(h, sb, 256);
    return h.ToInt64().ToString() + "|" + sb.ToString();
  }
}
'@

$res = [ordered]@{
  elevated          = $false
  session           = (Get-Process -Id $PID).SessionId
  foreground        = ''
  cursorBefore      = ''
  cursorAfter       = ''
  sendInputReturn   = 0
  clickedSendInput  = $false
  clickedMouseEvent = $false
  error             = ''
}
try {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $res.elevated = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

$form = New-Object System.Windows.Forms.Form
$form.Text = 'input probe'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(240, 200)
$form.Size = New-Object System.Drawing.Size(460, 240)
$form.TopMost = $true
$b1 = New-Object System.Windows.Forms.Button
$b1.Text = 'SendInput'
$b1.Location = New-Object System.Drawing.Point(30, 50)
$b1.Size = New-Object System.Drawing.Size(160, 70)
$b2 = New-Object System.Windows.Forms.Button
$b2.Text = 'mouse_event'
$b2.Location = New-Object System.Drawing.Point(250, 50)
$b2.Size = New-Object System.Drawing.Size(160, 70)
[void]$form.Controls.Add($b1)
[void]$form.Controls.Add($b2)
$script:c1 = $false
$script:c2 = $false
$b1.Add_Click({ $script:c1 = $true })
$b2.Add_Click({ $script:c2 = $true })

$p = New-Object ProbeInput+POINT
[void][ProbeInput]::GetCursorPos([ref]$p)
$script:cx = $p.X
$script:cy = $p.Y
$res.cursorBefore = [ProbeInput]::Cursor()
$res.foreground = [ProbeInput]::FgTitle()

$t = New-Object System.Windows.Forms.Timer
$t.Interval = 600
$script:stage = 0
$t.Add_Tick({
  try {
    if ($script:stage -eq 0) {
      $script:stage = 1
      $q = $b1.PointToScreen((New-Object System.Drawing.Point([int]($b1.Width / 2), [int]($b1.Height / 2))))
      [void][ProbeInput]::SetCursorPos($q.X, $q.Y)
      Start-Sleep -Milliseconds 150
      $res.sendInputReturn = [ProbeInput]::SendClick()
    } elseif ($script:stage -eq 1) {
      $script:stage = 2
      $q = $b2.PointToScreen((New-Object System.Drawing.Point([int]($b2.Width / 2), [int]($b2.Height / 2))))
      [void][ProbeInput]::SetCursorPos($q.X, $q.Y)
      Start-Sleep -Milliseconds 150
      [ProbeInput]::LegacyClick()
    } else {
      $t.Stop()
      $res.clickedSendInput = [bool]$script:c1
      $res.clickedMouseEvent = [bool]$script:c2
      $res.cursorAfter = [ProbeInput]::Cursor()
      [void][ProbeInput]::SetCursorPos($script:cx, $script:cy)
      $form.Close()
    }
  } catch {
    $res.error = $_.Exception.Message
    $t.Stop()
    $form.Close()
  }
})
$form.Add_Shown({ $t.Start() })
[System.Windows.Forms.Application]::Run($form)

$json = $res | ConvertTo-Json -Compress
Write-Output $json
if ($Out) {
  [IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))
}
