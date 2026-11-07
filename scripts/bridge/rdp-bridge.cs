// =====================================================================
// dsh-rdp-wrapper : native layer of the in-session bridge (C# 5)
// Compiled at bridge startup via Add-Type (Windows PowerShell 5.1).
// ASCII-only source. Build: compiled inside rdp-bridge.ps1 via Add-Type.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class W32
{
    // ---- mouse event flags ----
    const uint ME_MOVE = 0x0001;
    const uint ME_ABSOLUTE = 0x8000;
    const uint ME_VIRTUALDESK = 0x4000;
    const uint ME_LEFTDOWN = 0x0002;
    const uint ME_LEFTUP = 0x0004;
    const uint ME_RIGHTDOWN = 0x0008;
    const uint ME_RIGHTUP = 0x0010;
    const uint ME_MIDDLEDOWN = 0x0020;
    const uint ME_MIDDLEUP = 0x0040;
    const uint ME_WHEEL = 0x0800;
    // ---- key event flags ----
    const uint KE_KEYUP = 0x0002;
    const uint KE_UNICODE = 0x0004;
    // ---- GetSystemMetrics indexes (virtual screen) ----
    const int SM_XVIRT = 76;
    const int SM_YVIRT = 77;
    const int SM_CXVIRT = 78;
    const int SM_CYVIRT = 79;
    // ---- SetThreadExecutionState flags ----
    const uint ES_CONTINUOUS = 0x80000000u;
    const uint ES_SYSTEM_REQUIRED = 0x00000001u;
    const uint ES_DISPLAY_REQUIRED = 0x00000002u;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }

    // ---- P/Invoke declarations ----
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")]  public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static List<Dictionary<string, object>> ChildControls(IntPtr parent)
    {
        var list = new List<Dictionary<string, object>>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            StringBuilder cn = new StringBuilder(256);
            GetClassNameW(h, cn, 256);
            StringBuilder t = new StringBuilder(256);
            GetWindowTextW(h, t, 256);
            long style = GetWindowLongPtr(h, -16); // GWL_STYLE
            var d = new Dictionary<string, object>();
            d["handle"] = h.ToInt64();
            d["class"] = cn.ToString();
            d["text"] = t.ToString();
            d["style"] = style;
            list.Add(d);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW64(IntPtr hWnd, int nIndex);
    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8) return GetWindowLongPtrW64(hWnd, nIndex).ToInt64();
        return GetWindowLongW(hWnd, nIndex);
    }
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    public static void SetTopmost(IntPtr h, bool topmost)
    {
        // SWP_NOMOVE(2) | SWP_NOSIZE(1), HWND_TOPMOST(-1) / HWND_NOTOPMOST(-2)
        SetWindowPos(h, topmost ? (IntPtr)(-1) : (IntPtr)(-2), 0, 0, 0, 0, 3);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PTSTRUCT { public int X, Y; }
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(PTSTRUCT p);
    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public static string WhoIsAt(int x, int y)
    {
        PTSTRUCT p; p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return "none";
        StringBuilder cn = new StringBuilder(256);
        GetClassNameW(h, cn, 256);
        StringBuilder t = new StringBuilder(256);
        GetWindowTextW(h, t, 256);
        uint pid = 0;
        GetWindowThreadProcessId(h, out pid);
        IntPtr root = GetAncestor(h, 2); // GA_ROOT
        StringBuilder rcn = new StringBuilder(256);
        GetClassNameW(root, rcn, 256);
        uint rpid = 0;
        GetWindowThreadProcessId(root, out rpid);
        return "hit=" + cn.ToString() + "/" + t.ToString() + "/pid" + pid + " root=" + rcn.ToString() + "/pid" + rpid;
    }

    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr hMem);

    // ---- DPI ----
    public static void SetDPIAware() { SetProcessDPIAware(); }

    // ---- keep-alive (prevent display off / sleep; refresh every 60s) ----
    static Thread keepAliveThread;
    public static void KeepAliveStart()
    {
        if (keepAliveThread != null) return;
        keepAliveThread = new Thread(delegate()
        {
            while (true)
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                Thread.Sleep(60000);
            }
        });
        keepAliveThread.IsBackground = true;
        keepAliveThread.Start();
    }

    // ---- mouse ----
    static void SendMouseInput(uint flags, int dx, int dy, int data)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = 0; // INPUT_MOUSE
        inp[0].U.mi.dx = dx;
        inp[0].U.mi.dy = dy;
        inp[0].U.mi.mouseData = unchecked((uint)data);
        inp[0].U.mi.dwFlags = flags;
        inp[0].U.mi.time = 0;
        inp[0].U.mi.dwExtraInfo = IntPtr.Zero;
        if (SendInput(1, inp, Marshal.SizeOf(typeof(INPUT))) == 0) throw new Exception("SendInput failed");
    }

    public static void MouseMoveAbs(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRT), vy = GetSystemMetrics(SM_YVIRT);
        int vw = GetSystemMetrics(SM_CXVIRT), vh = GetSystemMetrics(SM_CYVIRT);
        if (vw <= 1) vw = 2;
        if (vh <= 1) vh = 2;
        int nx = (int)((long)(x - vx) * 65535 / (vw - 1));
        int ny = (int)((long)(y - vy) * 65535 / (vh - 1));
        if (nx < 0) nx = 0;
        if (nx > 65535) nx = 65535;
        if (ny < 0) ny = 0;
        if (ny > 65535) ny = 65535;
        SendMouseInput(ME_MOVE | ME_ABSOLUTE | ME_VIRTUALDESK, nx, ny, 0);
    }

    public static void MouseClick(string button, bool doubleClick)
    {
        uint down, up;
        if (button == "right") { down = ME_RIGHTDOWN; up = ME_RIGHTUP; }
        else if (button == "middle") { down = ME_MIDDLEDOWN; up = ME_MIDDLEUP; }
        else { down = ME_LEFTDOWN; up = ME_LEFTUP; }
        SendMouseInput(down, 0, 0, 0);
        Thread.Sleep(15);
        SendMouseInput(up, 0, 0, 0);
        if (doubleClick)
        {
            Thread.Sleep(50);
            SendMouseInput(down, 0, 0, 0);
            Thread.Sleep(15);
            SendMouseInput(up, 0, 0, 0);
        }
    }

    // Popup windows that carry a menu: Win11 XAML flyouts use
    // Microsoft.UI.Content.PopupWindowSiteBridge (the visual itself is
    // DirectComposition, so the window can be 0x0 while the menu is not).
    // Classic Win32 menus use #32768 and appear instantly.
    static bool IsMenuPopupClass(string c)
    {
        return c == "Microsoft.UI.Content.PopupWindowSiteBridge" ||
               c == "XamlExplorerHostIslandWindow_WASDK" ||
               c == "#32768";
    }

    static string ClassName(IntPtr h)
    {
        StringBuilder cn = new StringBuilder(256);
        GetClassNameW(h, cn, 256);
        return cn.ToString();
    }

    // Snapshot of visible menu popups (handle -> rect). A popup that is
    // already visible at click time must not satisfy the wait; its handle may
    // be reused for the next menu, in which case the rect change is the signal.
    public static Dictionary<long, string> PopupSnapshot()
    {
        Dictionary<long, string> map = new Dictionary<long, string>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            string c = ClassName(h);
            if (!IsMenuPopupClass(c)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = r.Right - r.Left;
            int hh = r.Bottom - r.Top;
            if (w <= 1 || hh <= 1) return true;
            map[h.ToInt64()] = r.Left + "," + r.Top + "," + w + "x" + hh;
            return true;
        }, IntPtr.Zero);
        return map;
    }

    // Right-click menus need time to render: the XAML host becomes foreground
    // a few seconds after the click, then the popup grows to its final size.
    // Wait until a new (or resized) popup is stable, so an immediately
    // following screenshot shows the menu. Classic Win32 menus return at once.
    public static Dictionary<string, object> WaitForPopup(int maxMs, Dictionary<long, string> before)
    {
        if (maxMs < 0) maxMs = 0;
        int start = Environment.TickCount;
        string lastRect = null;
        int stable = 0;
        string foundClass = null;
        string foundRect = null;
        string foundTitle = null;
        bool found = false;

        while (true)
        {
            int elapsed = unchecked(Environment.TickCount - start);
            if (elapsed >= maxMs) break;

            string rect = null, cls = null, title = null;
            long bestArea = 0;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                string c = ClassName(h);
                if (!IsMenuPopupClass(c)) return true;
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                int w = r.Right - r.Left;
                int hh = r.Bottom - r.Top;
                if (w <= 1 || hh <= 1) return true; // XAML host is 0x0
                bool classic = c == "#32768";
                // Skip the XAML opening animation's initial sliver; a real
                // menu is at least a few dozen pixels wide.
                if (classic ? (w < 4 || hh < 4) : (w < 40 || hh < 24)) return true;
                string key = r.Left + "," + r.Top + "," + w + "x" + hh;
                if (before != null)
                {
                    long hv = h.ToInt64();
                    string old;
                    if (before.TryGetValue(hv, out old) && old == key) return true;
                }
                long area = (long)w * hh;
                if (area > bestArea)
                {
                    bestArea = area;
                    rect = w + "x" + hh;
                    cls = c;
                    StringBuilder t = new StringBuilder(256);
                    GetWindowTextW(h, t, 256);
                    title = t.ToString();
                }
                return true;
            }, IntPtr.Zero);

            if (rect != null)
            {
                if (rect == lastRect)
                {
                    stable += 50;
                }
                else
                {
                    lastRect = rect;
                    stable = 0;
                    foundClass = cls;
                    foundRect = rect;
                    foundTitle = title;
                }
                if (cls == "#32768" || stable >= 120)
                {
                    found = true;
                    // Let the opening animation settle before the caller takes
                    // a screenshot; classic menus only need a short beat so all
                    // items are painted.
                    if (cls == "#32768") Thread.Sleep(120);
                    else Thread.Sleep(250);
                    break;
                }
            }
            else
            {
                lastRect = null;
                stable = 0;
            }
            Thread.Sleep(50);
        }

        Dictionary<string, object> d = new Dictionary<string, object>();
        d["found"] = found;
        d["waitedMs"] = unchecked(Environment.TickCount - start);
        if (found)
        {
            d["class"] = foundClass;
            d["rect"] = foundRect;
            if (!string.IsNullOrEmpty(foundTitle)) d["title"] = foundTitle;
        }
        return d;
    }

    public static void MouseDrag(int x1, int y1, int x2, int y2, int steps)
    {
        if (steps < 1) steps = 1;
        MouseMoveAbs(x1, y1);
        SendMouseInput(ME_LEFTDOWN, 0, 0, 0);
        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            MouseMoveAbs(x, y);
            Thread.Sleep(10);
        }
        SendMouseInput(ME_LEFTUP, 0, 0, 0);
    }

    public static void MouseWheel(int amount)
    {
        SendMouseInput(ME_WHEEL, 0, 0, amount);  // amount in multiples of WHEEL_DELTA (120)
    }

    // ---- message-based input (opt-in via mode=message) ----
    // Posts clicks to the control under the pointer, so standard dialogs can be
    // driven without moving the real cursor. Useful when the target window is
    // occluded/background; self-drawn surfaces (desktop, taskbar) ignore it.
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref PTSTRUCT p);

    static int ClickPriority(string cls)
    {
        switch (cls.ToLowerInvariant())
        {
            case "button": return 100;
            case "syslink": return 95;
            case "edit": case "richedit": case "richedit20w": case "richedit50w": return 90;
            case "combobox": case "listbox": case "syslistview32": case "systreeview32": return 90;
            case "toolbarwindow32": case "msctls_trackbar32": case "msctls_updown32": return 90;
            case "static": return 10;
            default: return 50;
        }
    }

    static IntPtr FindClickTarget(IntPtr root, PTSTRUCT pt)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = -1, bestArea = int.MaxValue;
        EnumChildWindows(root, delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || !IsWindowEnabled(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            if (pt.X < r.Left || pt.X >= r.Right || pt.Y < r.Top || pt.Y >= r.Bottom) return true;
            StringBuilder cn = new StringBuilder(256);
            GetClassNameW(h, cn, 256);
            int score = ClickPriority(cn.ToString());
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (score > bestScore || (score == bestScore && area < bestArea))
            {
                best = h; bestScore = score; bestArea = area;
            }
            return true;
        }, IntPtr.Zero);
        if (best != IntPtr.Zero && bestScore >= 50) return best;
        return root;
    }

    static IntPtr ClientLParam(IntPtr target, PTSTRUCT pt)
    {
        PTSTRUCT cp = pt;
        ScreenToClient(target, ref cp);
        return (IntPtr)((cp.Y << 16) | (cp.X & 0xFFFF));
    }

    static Dictionary<string, object> TargetInfo(IntPtr target)
    {
        StringBuilder cn = new StringBuilder(256);
        GetClassNameW(target, cn, 256);
        var d = new Dictionary<string, object>();
        d["handle"] = target.ToInt64();
        d["class"] = cn.ToString();
        return d;
    }

    public static Dictionary<string, object> MessageClick(int x, int y, string button, bool doubleClick)
    {
        PTSTRUCT pt; pt.X = x; pt.Y = y;
        IntPtr hit = WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return null;
        IntPtr root = GetAncestor(hit, 2); // GA_ROOT
        if (root == IntPtr.Zero) root = hit;
        IntPtr target = FindClickTarget(root, pt);
        var info = TargetInfo(target);

        IntPtr fg = GetForegroundWindow();
        if (fg != root)
        {
            SetForegroundWindow(root);
            Thread.Sleep(40);
        }

        bool ok;
        if (button == "left" && !doubleClick &&
            string.Equals((string)info["class"], "Button", StringComparison.OrdinalIgnoreCase))
        {
            ok = PostMessageW(target, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
            info["method"] = "bm_click";
        }
        else
        {
            IntPtr lp = ClientLParam(target, pt);
            uint down, up, dbl;
            int mk;
            if (button == "right") { down = 0x0204; up = 0x0205; dbl = 0; mk = 2; }
            else if (button == "middle") { down = 0x0207; up = 0x0208; dbl = 0; mk = 16; }
            else { down = 0x0201; up = 0x0202; dbl = 0x0203; mk = 1; }
            PostMessageW(target, 0x0200, IntPtr.Zero, lp); // WM_MOUSEMOVE
            Thread.Sleep(20);
            PostMessageW(target, down, (IntPtr)mk, lp);
            Thread.Sleep(40);
            PostMessageW(target, up, IntPtr.Zero, lp);
            if (doubleClick && dbl != 0)
            {
                Thread.Sleep(60);
                PostMessageW(target, dbl, (IntPtr)mk, lp);
                Thread.Sleep(20);
                PostMessageW(target, up, IntPtr.Zero, lp);
            }
            if (button == "right")
            {
                IntPtr menuLp = (IntPtr)((y << 16) | (x & 0xFFFF));
                PostMessageW(target, 0x007B, target, menuLp); // WM_CONTEXTMENU
            }
            ok = true;
            info["method"] = doubleClick ? "wm_lbuttondblclk" : "wm_mouse";
        }
        info["posted"] = ok;
        return info;
    }

    public static Dictionary<string, object> MessageDrag(int x1, int y1, int x2, int y2, int steps)
    {
        PTSTRUCT p1; p1.X = x1; p1.Y = y1;
        IntPtr hit = WindowFromPoint(p1);
        if (hit == IntPtr.Zero) return null;
        IntPtr root = GetAncestor(hit, 2);
        if (root == IntPtr.Zero) root = hit;
        IntPtr target = FindClickTarget(root, p1);
        var info = TargetInfo(target);
        PTSTRUCT p2; p2.X = x2; p2.Y = y2;
        PTSTRUCT c1 = p1, c2 = p2;
        ScreenToClient(target, ref c1);
        ScreenToClient(target, ref c2);
        IntPtr lp1 = (IntPtr)((c1.Y << 16) | (c1.X & 0xFFFF));
        IntPtr lp2 = (IntPtr)((c2.Y << 16) | (c2.X & 0xFFFF));
        if (steps < 1) steps = 1;
        PostMessageW(target, 0x0200, IntPtr.Zero, lp1);
        Thread.Sleep(20);
        PostMessageW(target, 0x0201, (IntPtr)1, lp1);
        Thread.Sleep(40);
        for (int i = 1; i <= steps; i++)
        {
            int cx = c1.X + (c2.X - c1.X) * i / steps;
            int cy = c1.Y + (c2.Y - c1.Y) * i / steps;
            PostMessageW(target, 0x0200, (IntPtr)1, (IntPtr)((cy << 16) | (cx & 0xFFFF)));
            Thread.Sleep(15);
        }
        PostMessageW(target, 0x0202, IntPtr.Zero, lp2);
        info["method"] = "wm_drag";
        info["posted"] = true;
        return info;
    }

    public static Dictionary<string, object> MessageWheel(int x, int y, int delta)
    {
        PTSTRUCT pt; pt.X = x; pt.Y = y;
        IntPtr hit = WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return null;
        IntPtr root = GetAncestor(hit, 2);
        if (root == IntPtr.Zero) root = hit;
        IntPtr target = FindClickTarget(root, pt);
        var info = TargetInfo(target);
        IntPtr wparam = (IntPtr)((delta & 0xFFFF) << 16);
        IntPtr lparam = (IntPtr)((y << 16) | (x & 0xFFFF));
        info["posted"] = PostMessageW(target, 0x020A, wparam, lparam); // WM_MOUSEWHEEL
        info["method"] = "wm_mousewheel";
        return info;
    }


    // ---- keyboard ----
    public static void KeyTap(ushort vk)
    {
        SendKeyInput(vk, '\0', false);
        Thread.Sleep(5);
        SendKeyInput(vk, '\0', true);
    }

    public static void KeyCombo(ushort[] down)
    {
        foreach (ushort vk in down) { SendKeyInput(vk, '\0', false); Thread.Sleep(10); }
        for (int i = down.Length - 1; i >= 0; i--) { SendKeyInput(down[i], '\0', true); Thread.Sleep(10); }
    }

    public static void TypeText(string text)
    {
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { KeyTap(0x0D); continue; }
            SendKeyInput(0, c, false);
            Thread.Sleep(6);
            SendKeyInput(0, c, true);
            Thread.Sleep(4);
        }
    }

    static void SendKeyInput(ushort vk, char uni, bool up)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = 1; // INPUT_KEYBOARD
        if (vk != 0)
        {
            inp[0].U.ki.wVk = vk;
            inp[0].U.ki.wScan = 0;
            inp[0].U.ki.dwFlags = up ? KE_KEYUP : 0;
        }
        else
        {
            inp[0].U.ki.wVk = 0;
            inp[0].U.ki.wScan = uni;
            inp[0].U.ki.dwFlags = KE_UNICODE | (up ? KE_KEYUP : 0);
        }
        if (SendInput(1, inp, Marshal.SizeOf(typeof(INPUT))) == 0) throw new Exception("SendInput failed");
    }

    // ---- screen / windows ----
    public static int[] Metrics()
    {
        return new int[] {
            GetSystemMetrics(SM_XVIRT), GetSystemMetrics(SM_YVIRT),
            GetSystemMetrics(SM_CXVIRT), GetSystemMetrics(SM_CYVIRT) };
    }

    public static List<Dictionary<string, object>> Windows()
    {
        var list = new List<Dictionary<string, object>>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLengthW(h);
            if (len == 0) return true;
            StringBuilder t = new StringBuilder(len + 1);
            int got = GetWindowTextW(h, t, t.Capacity);
            if (got == 0) return true;
            StringBuilder cn = new StringBuilder(256);
            GetClassNameW(h, cn, 256);
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            var d = new Dictionary<string, object>();
            d["handle"] = h.ToInt64();
            d["title"] = t.ToString();
            d["class"] = cn.ToString();
            d["pid"] = pid;
            d["rect"] = string.Format("{0},{1},{2},{3}", r.Left, r.Top, r.Right, r.Bottom);
            d["minimized"] = IsIconic(h);
            d["maximized"] = IsZoomed(h);
            list.Add(d);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool FocusWindow(IntPtr h)
    {
        if (!IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
        bool ok = false;
        try
        {
            IntPtr fg = GetForegroundWindow();
            uint fgPid = 0, hPid = 0;
            uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
            uint hThread = GetWindowThreadProcessId(h, out hPid);
            uint cur = GetCurrentThreadId();
            bool a1 = false, a2 = false;
            if (fgThread != 0 && fgThread != cur) a1 = AttachThreadInput(cur, fgThread, true);
            if (hThread != 0 && hThread != cur) a2 = AttachThreadInput(cur, hThread, true);
            BringWindowToTop(h);
            ok = SetForegroundWindow(h);
            if (a1) AttachThreadInput(cur, fgThread, false);
            if (a2) AttachThreadInput(cur, hThread, false);
        }
        catch { }
        if (!ok)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 0x0002, UIntPtr.Zero);
            ok = SetForegroundWindow(h);
        }
        return ok;
    }

    public static RECT GetRect(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) throw new Exception("GetWindowRect failed");
        return r;
    }

    // ---- capture ----
    static byte[] BmpToPng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    public static byte[] CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new Exception("invalid region size");
        using (var bmp = new Bitmap(w, h))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            return BmpToPng(bmp);
        }
    }

    public static byte[] CaptureWindow(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) throw new Exception("GetWindowRect failed");
        int w = r.Right - r.Left;
        int hgt = r.Bottom - r.Top;
        if (w <= 0 || hgt <= 0) throw new Exception("window has no size");
        using (var bmp = new Bitmap(w, hgt))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(h, hdc, 2 /* PW_RENDERFULLCONTENT */)) throw new Exception("PrintWindow failed");
                }
                finally { g.ReleaseHdc(hdc); }
            }
            return BmpToPng(bmp);
        }
    }

    // ---- clipboard ----
    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    public static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("OpenClipboard failed");
        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return "";
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return "";
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            string s = Marshal.PtrToStringUni(p);
            GlobalUnlock(h);
            return s;
        }
        finally { CloseClipboard(); }
    }

    public static void SetClipboardText(string s)
    {
        if (s == null) s = "";
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("OpenClipboard failed");
        try
        {
            EmptyClipboard();
            IntPtr g = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((s.Length + 1) * 2));
            if (g == IntPtr.Zero) throw new Exception("GlobalAlloc failed");
            IntPtr p = GlobalLock(g);
            if (p == IntPtr.Zero) { GlobalFree(g); throw new Exception("GlobalLock failed"); }
            char[] arr = s.ToCharArray();
            Marshal.Copy(arr, 0, p, arr.Length);
            Marshal.WriteByte(p, s.Length * 2, 0);
            Marshal.WriteByte(p, s.Length * 2 + 1, 0);
            GlobalUnlock(g);
            if (SetClipboardData(CF_UNICODETEXT, g) == IntPtr.Zero)
            {
                GlobalFree(g);
                throw new Exception("SetClipboardData failed");
            }
        }
        finally { CloseClipboard(); }
    }
}
