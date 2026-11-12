# Dev probe: log XAML popup size over time after a desktop right-click.
# ASCII-only source.
param([int]$X = 960, [int]$Y = 540, [int]$MaxMs = 6000)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public class PopupGrowth
{
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static string Cls(IntPtr h) { var s = new StringBuilder(512); GetClassName(h, s, s.Capacity); return s.ToString(); }

    public static string BiggestPopup()
    {
        int best = 0; string desc = null;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (Cls(h) != "Microsoft.UI.Content.PopupWindowSiteBridge" &&
                Cls(h) != "XamlExplorerHostIslandWindow_WASDK") return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            if (w * hh > best) { best = w * hh; desc = r.Left + "," + r.Top + "," + w + "x" + hh; }
            return true;
        }, IntPtr.Zero);
        return desc;
    }

    public static void RightClick(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(100);
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(20);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
    }

    public static void Esc()
    {
        keybd_event(0x1B, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(0x1B, 0, 2, UIntPtr.Zero);
    }

    public static string Run(int x, int y, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        RightClick(x, y);
        var lines = new List<string>();
        string last = "";
        while (sw.ElapsedMilliseconds < maxMs)
        {
            string pop = BiggestPopup();
            string fg = Cls(GetForegroundWindow());
            string cur = (pop ?? "-") + " fg=" + fg;
            if (cur != last)
            {
                lines.Add(sw.ElapsedMilliseconds + "ms " + cur);
                last = cur;
            }
            System.Threading.Thread.Sleep(40);
        }
        return string.Join(" | ", lines.ToArray());
    }
}
'@

[PopupGrowth]::Esc()
Start-Sleep -Milliseconds 800
$timeline = [PopupGrowth]::Run($X, $Y, $MaxMs)
[PopupGrowth]::Esc()
@{ timeline = $timeline } | ConvertTo-Json -Compress
