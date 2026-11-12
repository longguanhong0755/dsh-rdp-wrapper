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
using System.Windows.Automation;

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
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
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

    // Clipboard-assisted paste: used for non-ASCII text (CJK input via
    // VK_PACKET is unreliable when an IME is active). The previous clipboard
    // text is restored when it was not empty.
    public static void PasteText(string text)
    {
        string saved = "";
        try { saved = GetClipboardText(); } catch { }
        SetClipboardText(text);
        KeyCombo(new ushort[] { 0x11, 0x56 });
        Thread.Sleep(400);
        try { if (saved != "") SetClipboardText(saved); } catch { }
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

    // Full-screen capture with numbered boxes drawn on top of detected UI
    // elements (set-of-marks style). boxes is a flat x,y,w,h array in screen
    // coordinates; labels are the element indices shown to the model.
    public static byte[] CaptureAnnotated(int x, int y, int w, int h, int[] boxes, string[] labels, bool marks)
    {
        if (w <= 0 || h <= 0) throw new Exception("invalid region size");
        using (var bmp = new Bitmap(w, h))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                if (marks && boxes != null)
                {
                    using (var pen = new Pen(Color.FromArgb(255, 230, 60, 40), 1.4f))
                    using (var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var bg = new SolidBrush(Color.FromArgb(215, 200, 30, 20)))
                    using (var fg = new SolidBrush(Color.White))
                    {
                        int n = boxes.Length / 4;
                        for (int i = 0; i < n; i++)
                        {
                            int bx = boxes[i * 4] - x;
                            int by = boxes[i * 4 + 1] - y;
                            int bw = boxes[i * 4 + 2];
                            int bh = boxes[i * 4 + 3];
                            if (bw <= 0 || bh <= 0) continue;
                            if (bx + bw < 0 || by + bh < 0 || bx >= w || by >= h) continue;
                            g.DrawRectangle(pen, bx, by, bw, bh);
                            if (labels == null || i >= labels.Length) continue;
                            string lb = labels[i];
                            if (lb == null || lb.Length == 0) continue;
                            SizeF sz = g.MeasureString(lb, font);
                            float lw = sz.Width + 6, lh = sz.Height + 2;
                            float lx = bx; if (lx + lw > w) lx = w - lw; if (lx < 0) lx = 0;
                            float ly = by; if (ly + lh > h) ly = h - lh; if (ly < 0) ly = 0;
                            g.FillRectangle(bg, lx, ly, lw, lh);
                            g.DrawString(lb, font, fg, lx + 3, ly + 1);
                        }
                    }
                }
            }
            return BmpToPng(bmp);
        }
    }

    // Coarse screen signature used to detect "UI settled down" and "screen
    // changed" without shipping pixels around. Sampling a grid is enough for
    // dialogs, menus, page transitions and text appearing.
    static long ScreenSignature(int x, int y, int w, int h)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        using (var bmp = new Bitmap(w, h))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            const int cols = 48;
            const int rows = 27;
            unchecked
            {
                long hash = -3750763034362895579L;
                for (int r = 0; r < rows; r++)
                {
                    int py = (int)((long)r * (h - 1) / (rows - 1));
                    for (int c = 0; c < cols; c++)
                    {
                        int px = (int)((long)c * (w - 1) / (cols - 1));
                        Color col = bmp.GetPixel(px, py);
                        hash ^= (uint)(col.R | (col.G << 8) | (col.B << 16));
                        hash *= 1099511628211L;
                    }
                }
                return hash;
            }
        }
    }

    public static Dictionary<string, object> WaitScreen(string mode, int x, int y, int w, int h, int stableMs, int timeoutMs)
    {
        if (w <= 0 || h <= 0) throw new Exception("invalid wait region size");
        if (stableMs < 100) stableMs = 100;
        if (timeoutMs < 100) timeoutMs = 100;
        int start = Environment.TickCount;
        long baseSig = ScreenSignature(x, y, w, h);
        long last = baseSig;
        int lastChange = start;
        bool ok = false;
        string reason = "";
        while (true)
        {
            int now = Environment.TickCount;
            int elapsed = unchecked(now - start);
            if (mode == "change")
            {
                if (last != baseSig) { ok = true; reason = "changed"; break; }
            }
            else
            {
                if (last != baseSig) { baseSig = last; lastChange = now; }
                else if (unchecked(now - lastChange) >= stableMs) { ok = true; reason = "stable"; break; }
            }
            if (elapsed >= timeoutMs) { reason = "timeout"; break; }
            Thread.Sleep(120);
            last = ScreenSignature(x, y, w, h);
        }
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["ok"] = ok;
        d["mode"] = mode;
        d["waitedMs"] = unchecked(Environment.TickCount - start);
        d["reason"] = reason;
        return d;
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

// =====================================================================
// UI Automation layer: exposes the desktop as a compact, numbered list of
// actionable elements so an agent can act by index instead of guessing
// pixel coordinates from screenshots (Windows-MCP / Agent-S style).
// Snapshots are cached in this process; rdp_state / uia list reset the
// cache, uia find / uia wait append to it, so returned indices stay valid.
// =====================================================================
public static class Uia
{
    static readonly List<AutomationElement> _cache = new List<AutomationElement>();
    static readonly List<string> _cacheDesc = new List<string>();
    static readonly object _sync = new object();

    class Snap
    {
        public List<AutomationElement> Els = new List<AutomationElement>();
        public List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();
        public int Total;
        public bool Truncated;
    }

    static CacheRequest NewRequest()
    {
        CacheRequest req = new CacheRequest();
        req.Add(AutomationElement.NameProperty);
        req.Add(AutomationElement.AutomationIdProperty);
        req.Add(AutomationElement.ClassNameProperty);
        req.Add(AutomationElement.ControlTypeProperty);
        req.Add(AutomationElement.BoundingRectangleProperty);
        req.Add(AutomationElement.IsEnabledProperty);
        req.Add(AutomationElement.IsOffscreenProperty);
        req.Add(AutomationElement.HasKeyboardFocusProperty);
        req.Add(AutomationElement.IsPasswordProperty);
        req.Add(ValuePattern.ValueProperty);
        req.Add(TogglePattern.ToggleStateProperty);
        req.Add(SelectionItemPattern.IsSelectedProperty);
        req.Add(ExpandCollapsePattern.ExpandCollapseStateProperty);
        req.Add(InvokePattern.Pattern);
        req.Add(ValuePattern.Pattern);
        req.Add(TogglePattern.Pattern);
        req.Add(SelectionItemPattern.Pattern);
        req.Add(ExpandCollapsePattern.Pattern);
        req.Add(ScrollItemPattern.Pattern);
        req.TreeFilter = Automation.ControlViewCondition;
        req.AutomationElementMode = AutomationElementMode.Full;
        return req;
    }

    static object CP(AutomationElement el, AutomationProperty p, object dflt)
    {
        try { return el.GetCachedPropertyValue(p); }
        catch { return dflt; }
    }

    static string StrProp(AutomationElement el, AutomationProperty p)
    {
        object v = CP(el, p, null);
        if (v == null) return "";
        return Convert.ToString(v).Trim();
    }

    static string TypeName(AutomationElement el)
    {
        object v = CP(el, AutomationElement.ControlTypeProperty, null);
        ControlType ct = v as ControlType;
        if (ct == null) return "";
        string pn = ct.ProgrammaticName;
        int dot = pn.LastIndexOf('.');
        return dot >= 0 ? pn.Substring(dot + 1) : pn;
    }

    static string TypeNameLive(AutomationElement el)
    {
        try
        {
            string pn = el.Current.ControlType.ProgrammaticName;
            int dot = pn.LastIndexOf('.');
            return dot >= 0 ? pn.Substring(dot + 1) : pn;
        }
        catch { return ""; }
    }

    static System.Windows.Rect RectOf(AutomationElement el)
    {
        object v = CP(el, AutomationElement.BoundingRectangleProperty, null);
        if (v is System.Windows.Rect) return (System.Windows.Rect)v;
        return System.Windows.Rect.Empty;
    }

    static bool HasCachedPattern(AutomationElement el, AutomationPattern p)
    {
        try { object o; return el.TryGetCachedPattern(p, out o); }
        catch { return false; }
    }

    static bool ActionableType(string t)
    {
        switch (t.ToLowerInvariant())
        {
            case "button": case "checkbox": case "radiobutton": case "combobox":
            case "edit": case "document": case "hyperlink": case "listitem":
            case "menuitem": case "tabitem": case "treeitem": case "slider":
            case "spinner": case "splitbutton": case "dataitem": case "headeritem":
            case "thumb":
                return true;
            default:
                return false;
        }
    }

    // Scrollbar internals (arrow buttons, thumb) add noise to every list and
    // are rarely what an agent wants to click; mouse wheel scrolling is the
    // better primitive.
    static bool InsideScrollBar(AutomationElement el)
    {
        try
        {
            AutomationElement p = TreeWalker.ControlViewWalker.GetParent(el);
            for (int i = 0; i < 2 && p != null; i++)
            {
                if (p.Current.ControlType == ControlType.ScrollBar) return true;
                p = TreeWalker.ControlViewWalker.GetParent(p);
            }
        }
        catch { }
        return false;
    }

    static Dictionary<string, object> Describe(AutomationElement el, int idx)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["i"] = idx;
        string t = TypeName(el);
        d["type"] = t;
        bool pwd = (bool)CP(el, AutomationElement.IsPasswordProperty, false);
        string name = StrProp(el, AutomationElement.NameProperty);
        if (pwd) d["password"] = true;
        if (name.Length > 0) d["name"] = name;
        if (!pwd)
        {
            object vv = CP(el, ValuePattern.ValueProperty, null);
            string val = vv == null ? "" : Convert.ToString(vv).Trim();
            if (val.Length > 0 && val != name)
            {
                if (val.Length > 160) val = val.Substring(0, 160);
                d["value"] = val;
            }
        }
        System.Windows.Rect r = RectOf(el);
        d["x"] = (int)Math.Round(r.Left);
        d["y"] = (int)Math.Round(r.Top);
        d["w"] = (int)Math.Round(r.Width);
        d["h"] = (int)Math.Round(r.Height);
        if (!(bool)CP(el, AutomationElement.IsEnabledProperty, true)) d["enabled"] = false;
        if ((bool)CP(el, AutomationElement.HasKeyboardFocusProperty, false)) d["focused"] = true;
        if (HasCachedPattern(el, SelectionItemPattern.Pattern))
        {
            object sel = CP(el, SelectionItemPattern.IsSelectedProperty, null);
            if (sel is bool && (bool)sel) d["selected"] = true;
        }
        if (HasCachedPattern(el, TogglePattern.Pattern))
        {
            object tg = CP(el, TogglePattern.ToggleStateProperty, null);
            if (tg is ToggleState)
            {
                ToggleState ts = (ToggleState)tg;
                d["toggle"] = ts == ToggleState.On ? "on" : (ts == ToggleState.Off ? "off" : "mixed");
            }
        }
        if (HasCachedPattern(el, ExpandCollapsePattern.Pattern))
        {
            object ex = CP(el, ExpandCollapsePattern.ExpandCollapseStateProperty, null);
            if (ex is ExpandCollapseState)
            {
                ExpandCollapseState es = (ExpandCollapseState)ex;
                if (es == ExpandCollapseState.Expanded) d["expanded"] = true;
                else if (es == ExpandCollapseState.Collapsed) d["expanded"] = false;
            }
        }
        if (!d.ContainsKey("name"))
        {
            string aid = StrProp(el, AutomationElement.AutomationIdProperty);
            if (aid.Length > 0) d["id"] = aid;
            else
            {
                string cls = StrProp(el, AutomationElement.ClassNameProperty);
                if (cls.Length > 0) d["class"] = cls;
            }
        }
        return d;
    }

    static string Desc(Dictionary<string, object> d)
    {
        string t = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "?";
        string n = d.ContainsKey("name") ? Convert.ToString(d["name"]) : "";
        return n.Length > 0 ? t + " '" + n + "'" : t;
    }

    static AutomationElement RootFor(long handle)
    {
        if (handle != 0) return AutomationElement.FromHandle(new IntPtr(handle));
        IntPtr fg = W32.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            // Focus can sit on a child control (an Edit after typing). Always
            // describe its top-level window, otherwise FindAll(Descendants)
            // starts from a leaf and returns nothing.
            IntPtr root = W32.GetAncestor(fg, 2); // GA_ROOT
            if (root != IntPtr.Zero) fg = root;
            try { return AutomationElement.FromHandle(fg); }
            catch { }
        }
        return AutomationElement.RootElement;
    }

    static Snap Collect(AutomationElement root, bool interactiveOnly, int max, string role, string query)
    {
        AutomationElementCollection all;
        CacheRequest req = NewRequest();
        req.Push();
        try { all = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition); }
        finally { req.Pop(); }

        List<AutomationElement> els = new List<AutomationElement>();
        HashSet<string> seen = new HashSet<string>();
        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement el = all[i];
            try
            {
                if ((bool)CP(el, AutomationElement.IsOffscreenProperty, false)) continue;
                System.Windows.Rect r = RectOf(el);
                if (r.Width < 1 || r.Height < 1) continue;
                string t = TypeName(el);
                if (t.Length == 0) continue;
                bool actionable = ActionableType(t)
                    || HasCachedPattern(el, InvokePattern.Pattern)
                    || HasCachedPattern(el, TogglePattern.Pattern)
                    || HasCachedPattern(el, SelectionItemPattern.Pattern)
                    || HasCachedPattern(el, ExpandCollapsePattern.Pattern);
                if (actionable && InsideScrollBar(el)) continue;
                if (interactiveOnly && !actionable) continue;
                if (!interactiveOnly && !actionable && (t == "Pane" || t == "Group")) continue;
                if (!interactiveOnly && !actionable && t == "ScrollBar") continue;
                if (!string.IsNullOrEmpty(role) && !string.Equals(t, role, StringComparison.OrdinalIgnoreCase)) continue;
                string name = StrProp(el, AutomationElement.NameProperty);
                string val = "";
                object vv = CP(el, ValuePattern.ValueProperty, null);
                if (vv != null) val = Convert.ToString(vv);
                if (!string.IsNullOrEmpty(query))
                {
                    if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                        val.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (!interactiveOnly && name.Length == 0 && val.Length == 0 && !actionable) continue;
                string key = t + "|" + name + "|" + val + "|" + ((int)r.Left) + "," + ((int)r.Top) + "," + ((int)r.Width) + "x" + ((int)r.Height);
                if (!seen.Add(key)) continue;
                els.Add(el);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        Snap s = new Snap();
        s.Total = els.Count;
        els.Sort(delegate(AutomationElement a, AutomationElement b)
        {
            System.Windows.Rect ra = RectOf(a);
            System.Windows.Rect rb = RectOf(b);
            int ya = (int)ra.Top, yb = (int)rb.Top;
            if (Math.Abs(ya - yb) > 12) return ya.CompareTo(yb);
            int xa = (int)ra.Left, xb = (int)rb.Left;
            if (xa != xb) return xa.CompareTo(xb);
            return ya.CompareTo(yb);
        });
        if (max > 0 && els.Count > max)
        {
            s.Truncated = true;
            els.RemoveRange(max, els.Count - max);
        }
        s.Els = els;
        for (int i = 0; i < els.Count; i++) s.Items.Add(Describe(els[i], i));
        return s;
    }

    static AutomationElement Cached(int idx)
    {
        lock (_sync)
        {
            if (idx < 0 || idx >= _cache.Count)
                throw new Exception("element index " + idx + " is not in the current snapshot; call rdp_state (or rdp_uia action=list) again");
            return _cache[idx];
        }
    }

    static string CachedDesc(int idx)
    {
        lock (_sync)
        {
            if (idx < 0 || idx >= _cacheDesc.Count) return "";
            return _cacheDesc[idx];
        }
    }

    static void VerifyAlive(AutomationElement el, string what)
    {
        try { System.Windows.Rect r = el.Current.BoundingRectangle; }
        catch (Exception e)
        {
            throw new Exception(what + " is no longer available (the UI changed); call rdp_state again [" + e.GetType().Name + "]");
        }
    }

    static bool IsAscii(string s)
    {
        foreach (char c in s)
        {
            if (c == '\t' || c == '\n' || c == '\r') continue;
            if (c < 0x20 || c > 0x7E) return false;
        }
        return true;
    }

    static bool TryCurrent(AutomationElement el, AutomationPattern p, out object o)
    {
        o = null;
        try { return el.TryGetCurrentPattern(p, out o); }
        catch { return false; }
    }

    static void FocusWindowOf(AutomationElement el)
    {
        try
        {
            AutomationElement cur = el;
            for (int i = 0; i < 12 && cur != null; i++)
            {
                int h = 0;
                try { h = cur.Current.NativeWindowHandle; }
                catch { }
                if (h != 0)
                {
                    W32.FocusWindow(new IntPtr(h));
                    Thread.Sleep(50);
                    return;
                }
                AutomationElement par = TreeWalker.ControlViewWalker.GetParent(cur);
                if (par == null || par == AutomationElement.RootElement) return;
                cur = par;
            }
        }
        catch { }
    }

    static void RealClick(AutomationElement el, bool dbl)
    {
        object p;
        try { if (el.TryGetCurrentPattern(ScrollItemPattern.Pattern, out p)) ((ScrollItemPattern)p).ScrollIntoView(); }
        catch { }
        FocusWindowOf(el);
        System.Windows.Rect r;
        try { r = el.Current.BoundingRectangle; }
        catch (Exception e) { throw new Exception("element disappeared before clicking: " + e.Message); }
        int cx = (int)Math.Round(r.Left + r.Width / 2);
        int cy = (int)Math.Round(r.Top + r.Height / 2);
        W32.MouseMoveAbs(cx, cy);
        Thread.Sleep(40);
        W32.MouseClick("left", dbl);
    }

    static AutomationElement Resolve(int index, string text, string role, long handle, bool interactiveOnly, out int usedIndex)
    {
        usedIndex = -1;
        if (index >= 0)
        {
            AutomationElement el = Cached(index);
            string desc = CachedDesc(index);
            VerifyAlive(el, "element " + index + (desc.Length > 0 ? " (" + desc + ")" : ""));
            usedIndex = index;
            return el;
        }
        if (string.IsNullOrEmpty(text))
            throw new Exception("provide 'i' (element index from rdp_state) or 'text' to match by name");
        Snap s = Collect(RootFor(handle), interactiveOnly, 4000, role, text);
        if (s.Els.Count == 0)
            throw new Exception("no element matched text '" + text + "' (call rdp_state to see the current elements)");
        int best = 0;
        for (int k = 0; k < s.Els.Count; k++)
        {
            if (string.Equals(StrProp(s.Els[k], AutomationElement.NameProperty), text, StringComparison.OrdinalIgnoreCase)) { best = k; break; }
        }
        AutomationElement found = s.Els[best];
        lock (_sync)
        {
            usedIndex = _cache.Count;
            _cache.Add(found);
            _cacheDesc.Add(Desc(s.Items[best]));
        }
        return found;
    }

    // ---------------- public API (called from rdp-bridge.ps1) ----------------

    public static Dictionary<string, object> List(long handle, bool interactiveOnly, int max, string role)
    {
        if (max <= 0) max = 200;
        Snap s = Collect(RootFor(handle), interactiveOnly, max, role, null);
        lock (_sync)
        {
            _cache.Clear();
            _cacheDesc.Clear();
            for (int i = 0; i < s.Els.Count; i++)
            {
                _cache.Add(s.Els[i]);
                _cacheDesc.Add(Desc(s.Items[i]));
            }
        }
        Dictionary<string, object> res = new Dictionary<string, object>();
        res["elements"] = s.Items;
        res["total"] = s.Total;
        res["truncated"] = s.Truncated;
        return res;
    }

    public static Dictionary<string, object> Find(long handle, string text, string role, bool interactiveOnly, int max)
    {
        if (string.IsNullOrEmpty(text)) throw new Exception("uia find requires 'text'");
        if (max <= 0) max = 50;
        Snap s = Collect(RootFor(handle), interactiveOnly, 4000, role, text);
        List<Dictionary<string, object>> outItems = new List<Dictionary<string, object>>();
        lock (_sync)
        {
            for (int i = 0; i < s.Els.Count && outItems.Count < max; i++)
            {
                int ni = _cache.Count;
                _cache.Add(s.Els[i]);
                Dictionary<string, object> d = s.Items[i];
                d["i"] = ni;
                _cacheDesc.Add(Desc(d));
                outItems.Add(d);
            }
        }
        Dictionary<string, object> res = new Dictionary<string, object>();
        res["elements"] = outItems;
        res["count"] = outItems.Count;
        res["total"] = s.Total;
        return res;
    }

    public static Dictionary<string, object> Get(int index)
    {
        AutomationElement el = Cached(index);
        VerifyAlive(el, "element " + index);
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["i"] = index;
        d["type"] = TypeNameLive(el);
        try
        {
            d["name"] = el.Current.Name;
            bool pwd = el.Current.IsPassword;
            if (pwd) d["password"] = true;
            object vp;
            if (!pwd && el.TryGetCurrentPattern(ValuePattern.Pattern, out vp))
            {
                string v = ((ValuePattern)vp).Current.Value;
                if (v != null && v.Length > 0) d["value"] = v;
            }
            // Document controls (multiline TextBox, Word, browsers) expose no
            // ValuePattern; TextPattern is how you read back typed content.
            object txp;
            if (!pwd && !d.ContainsKey("value") && el.TryGetCurrentPattern(TextPattern.Pattern, out txp))
            {
                try
                {
                    string txt = ((TextPattern)txp).DocumentRange.GetText(4000);
                    if (!string.IsNullOrEmpty(txt)) d["text"] = txt;
                }
                catch { }
            }
            object tp;
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out tp))
                d["toggle"] = ((TogglePattern)tp).Current.ToggleState.ToString().ToLowerInvariant();
            object ep;
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ep))
                d["expanded"] = ((ExpandCollapsePattern)ep).Current.ExpandCollapseState == ExpandCollapseState.Expanded;
            object sp;
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sp))
                d["selected"] = ((SelectionItemPattern)sp).Current.IsSelected;
            object selp;
            if (el.TryGetCurrentPattern(SelectionPattern.Pattern, out selp))
            {
                try
                {
                    AutomationElement[] picked = ((SelectionPattern)selp).Current.GetSelection();
                    if (picked != null && picked.Length > 0)
                    {
                        string[] names = new string[picked.Length];
                        for (int si = 0; si < picked.Length; si++)
                        {
                            try { names[si] = picked[si].Current.Name; } catch { names[si] = ""; }
                        }
                        d["selection"] = string.Join(", ", names);
                    }
                }
                catch { }
            }
            System.Windows.Rect r = el.Current.BoundingRectangle;
            d["x"] = (int)Math.Round(r.Left);
            d["y"] = (int)Math.Round(r.Top);
            d["w"] = (int)Math.Round(r.Width);
            d["h"] = (int)Math.Round(r.Height);
            d["enabled"] = el.Current.IsEnabled;
            d["focused"] = el.Current.HasKeyboardFocus;
        }
        catch (Exception e)
        {
            throw new Exception("could not read element " + index + ": " + e.Message);
        }
        return d;
    }

    public static Dictionary<string, object> Click(int index, string text, string role, long handle, bool doubleClick)
    {
        int usedIndex;
        AutomationElement el = Resolve(index, text, role, handle, true, out usedIndex);
        string t = TypeName(el);
        string name = StrProp(el, AutomationElement.NameProperty);
        bool enabled = true;
        try { enabled = el.Current.IsEnabled; } catch { }
        if (!enabled) throw new Exception("element is disabled: " + (name.Length > 0 ? t + " '" + name + "'" : t));

        string via;
        if (doubleClick)
        {
            RealClick(el, true);
            via = "mouse";
        }
        else
        {
            object pat;
            object ep;
            bool collapsed = false;
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ep))
                collapsed = ((ExpandCollapsePattern)ep).Current.ExpandCollapseState == ExpandCollapseState.Collapsed;
            if (t == "MenuItem" && collapsed && ep != null)
            {
                ((ExpandCollapsePattern)ep).Expand();
                via = "expand";
            }
            else if (TryCurrent(el, InvokePattern.Pattern, out pat))
            {
                ((InvokePattern)pat).Invoke();
                via = "invoke";
            }
            else if (TryCurrent(el, TogglePattern.Pattern, out pat))
            {
                ((TogglePattern)pat).Toggle();
                via = "toggle";
            }
            else if (TryCurrent(el, SelectionItemPattern.Pattern, out pat))
            {
                ((SelectionItemPattern)pat).Select();
                via = "select";
            }
            else if (TryCurrent(el, ExpandCollapsePattern.Pattern, out pat))
            {
                if (((ExpandCollapsePattern)pat).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    ((ExpandCollapsePattern)pat).Expand();
                    via = "expand";
                }
                else
                {
                    ((ExpandCollapsePattern)pat).Collapse();
                    via = "collapse";
                }
            }
            else
            {
                RealClick(el, false);
                via = "mouse";
            }
        }
        Thread.Sleep(120);
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["clicked"] = true;
        d["i"] = usedIndex;
        d["type"] = t;
        if (name.Length > 0) d["name"] = name;
        d["via"] = via;
        if (doubleClick) d["double"] = true;
        return d;
    }

    public static Dictionary<string, object> SetValue(int index, string text, string role, long handle, string value, bool submit, bool clear)
    {
        int usedIndex;
        AutomationElement el = Resolve(index, text, role, handle, true, out usedIndex);
        string t = TypeName(el);
        string name = StrProp(el, AutomationElement.NameProperty);
        string via = "";
        if (!clear)
        {
            object pat;
            if (TryCurrent(el, ValuePattern.Pattern, out pat))
            {
                ValuePattern vp = (ValuePattern)pat;
                try
                {
                    if (!vp.Current.IsReadOnly)
                    {
                        vp.SetValue(value);
                        via = "value";
                    }
                }
                catch { }
            }
        }
        if (via.Length == 0)
        {
            FocusWindowOf(el);
            try { el.SetFocus(); }
            catch (Exception e) { throw new Exception("could not focus the element for typing: " + e.Message); }
            Thread.Sleep(80);
            if (clear)
            {
                W32.KeyCombo(new ushort[] { 0x11, 0x41 });
                Thread.Sleep(60);
            }
            if (IsAscii(value))
            {
                W32.TypeText(value);
                via = "keys";
            }
            else
            {
                W32.PasteText(value);
                via = "paste";
            }
        }
        if (submit)
        {
            try { FocusWindowOf(el); el.SetFocus(); } catch { }
            Thread.Sleep(60);
            W32.KeyTap(0x0D);
            Thread.Sleep(120);
        }
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["set"] = true;
        d["i"] = usedIndex;
        d["type"] = t;
        if (name.Length > 0) d["name"] = name;
        d["via"] = via;
        d["chars"] = value.Length;
        if (submit) d["submitted"] = true;
        return d;
    }

    public static Dictionary<string, object> Focus(int index, string text, string role, long handle)
    {
        int usedIndex;
        AutomationElement el = Resolve(index, text, role, handle, true, out usedIndex);
        string name = StrProp(el, AutomationElement.NameProperty);
        FocusWindowOf(el);
        el.SetFocus();
        Thread.Sleep(60);
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["focused"] = true;
        d["i"] = usedIndex;
        d["type"] = TypeName(el);
        if (name.Length > 0) d["name"] = name;
        return d;
    }

    public static Dictionary<string, object> WaitForElement(long handle, string text, string role, int timeoutMs, bool gone)
    {
        if (string.IsNullOrEmpty(text)) throw new Exception("wait element requires 'text'");
        if (timeoutMs < 100) timeoutMs = 100;
        int start = Environment.TickCount;
        while (true)
        {
            AutomationElement match = null;
            Snap s = null;
            try
            {
                s = Collect(RootFor(handle), false, 4000, role, text);
                if (s.Els.Count > 0) match = s.Els[0];
            }
            catch (Exception) { }
            int elapsed = unchecked(Environment.TickCount - start);
            if (gone)
            {
                if (match == null)
                {
                    Dictionary<string, object> g = new Dictionary<string, object>();
                    g["found"] = true;
                    g["gone"] = true;
                    g["waitedMs"] = elapsed;
                    return g;
                }
            }
            else if (match != null)
            {
                int idx;
                lock (_sync)
                {
                    idx = _cache.Count;
                    _cache.Add(match);
                    _cacheDesc.Add(Desc(s.Items[0]));
                }
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["found"] = true;
                d["i"] = idx;
                d["type"] = TypeName(match);
                string name = StrProp(match, AutomationElement.NameProperty);
                if (name.Length > 0) d["name"] = name;
                d["waitedMs"] = elapsed;
                return d;
            }
            if (elapsed >= timeoutMs)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["found"] = false;
                d["gone"] = gone;
                d["waitedMs"] = elapsed;
                return d;
            }
            Thread.Sleep(300);
        }
    }
}
