# Dev probe: dump top-level window z-order and the foreground window.
# ASCII-only source.
param([switch]$All)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class WinProbe
{
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);

    public static string Dump(IntPtr fg)
    {
        var sb = new StringBuilder();
        int z = 0;
        EnumWindows((h, l) =>
        {
            z++;
            string cls = Cls(h), txt = Txt(h);
            RECT r; GetWindowRect(h, out r);
            uint pid; GetWindowThreadProcessId(h, out pid);
            long ex = GetWindowLong(h, -20);
            sb.AppendLine(string.Format("{0:000} hwnd={1} pid={2} vis={3} en={4} fg={5} cls={6} txt=[{7}] rect={8},{9},{10}x{11} ex=0x{12:X}",
                z, h.ToInt64(), pid, IsWindowVisible(h), IsWindowEnabled(h), h == fg, cls, txt,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, ex));
            if (cls.IndexOf("Xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Composition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                EnumChildWindows(h, (c, l2) =>
                {
                    RECT cr; GetWindowRect(c, out cr);
                    sb.AppendLine(string.Format("      CH hwnd={0} vis={1} cls={2} txt=[{3}] rect={4},{5},{6}x{7} ex=0x{8:X}",
                        c.ToInt64(), IsWindowVisible(c), Cls(c), Txt(c), cr.Left, cr.Top, cr.Right - cr.Left, cr.Bottom - cr.Top,
                        GetWindowLong(c, -20)));
                    return true;
                }, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }

    public static string Cls(IntPtr h) { var s = new StringBuilder(512); GetClassName(h, s, s.Capacity); return s.ToString(); }
    public static string Txt(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, s.Capacity); return s.ToString(); }
}
'@

$fg = [WinProbe]::GetForegroundWindow()
$out = [ordered]@{
  foreground = [WinProbe]::Cls($fg) + ' [' + [WinProbe]::Txt($fg) + '] hwnd=' + $fg.ToInt64()
  windows = [WinProbe]::Dump($fg)
}
$out | ConvertTo-Json -Depth 3 -Compress
