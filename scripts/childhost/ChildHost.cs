// =====================================================================
// dsh-rdp-wrapper : child-session host (Windows Forms, C# 5)
//
// Hosts the built-in MsRdpClient10 ActiveX control and connects it to
// localhost with the extended property ConnectToChildSession=true.
// That is the same mechanism BetterGI uses for its "desktop clone":
// Windows creates a real RDP child session for the *current* interactive
// user without asking for a password, while the console session keeps
// running untouched.
//
// After the connection is up the host can start the in-session bridge
// (scripts\bridge\rdp-bridge.ps1) inside the child session through a
// temporary scheduled task and TASK_RUN_USE_SESSION_ID. The host then
// stays alive, because the child session lives exactly as long as this
// RDP connection.
//
// ASCII-only source.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private const string RdpClientClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";
    private const int TaskActionExecute = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskRunUseSessionId = 0x4;
    private const uint NoChildSessionId = uint.MaxValue;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr serverHandle, uint sessionId, bool wait);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    // ---- process-in-child-session launch ----
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int pid, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existing, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token, string applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token, int logonFlags, string applicationName, string commandLine,
        uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int bufferLength,
        IntPtr previousState, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public int LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int SE_PRIVILEGE_ENABLED = 0x2;

    [STAThread]
    private static int Main(string[] args)
    {
        Options o = Options.Parse(args);
        Log.Init(o.LogPath);
        Log.Write("child host starting, pid=" + Process.GetCurrentProcess().Id);

        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Global\\dsh-rdp-child-host", out createdNew))
        {
            if (!createdNew)
            {
                Log.Write("another child host is already running");
                Status.Write(o.StatusPath, false, 0, "already-running");
                return 2;
            }

            try
            {
                SetProcessDPIAware();
                CleanupStaleChildSession();
                CleanupStaleArtifacts(o);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (HostForm form = new HostForm(o))
                {
                    form.Shown += delegate { form.BeginConnect(); };
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("fatal: " + ex);
                Status.Write(o.StatusPath, false, 0, "fatal: " + ex.Message);
                return 1;
            }
        }
    }

    private static void CleanupStaleChildSession()
    {
        try
        {
            uint stale;
            if (!WTSGetChildSessionId(out stale) || stale == NoChildSessionId)
            {
                return;
            }
            Log.Write("stale child session " + stale + ", logging it off");
            WTSLogoffSession(IntPtr.Zero, stale, false);
            Thread.Sleep(1500);
        }
        catch (Exception ex)
        {
            Log.Write("stale child session cleanup failed: " + ex.Message);
        }
    }

    // Remove tasks and port files left behind by a host that was killed
    // instead of shutting down cleanly. Runs while holding the single-instance
    // mutex, before this host registers its own task or bridge.
    private static void CleanupStaleArtifacts(Options o)
    {
        try
        {
            string tasksDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
            List<string> staleTasks = new List<string>();
            if (Directory.Exists(tasksDir))
            {
                foreach (string f in Directory.GetFiles(tasksDir, "dsh-rdp-child-bridge-*"))
                {
                    staleTasks.Add(Path.GetFileName(f));
                }
            }

            if (staleTasks.Count > 0)
            {
                Type schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                object svcObj = Activator.CreateInstance(schedulerType);
                dynamic svc = svcObj;
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                foreach (string name in staleTasks)
                {
                    try
                    {
                        root.DeleteTask(name, 0);
                        Log.Write("stale task deleted: " + name);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("stale task delete failed (" + name + "): " + ex.Message);
                    }
                }
            }

            string portsDir = Path.GetDirectoryName(o.StatusPath);
            if (!string.IsNullOrEmpty(portsDir) && Directory.Exists(portsDir))
            {
                foreach (string f in Directory.GetFiles(portsDir, "bridge-session-*.json"))
                {
                    try
                    {
                        File.Delete(f);
                        Log.Write("stale port file deleted: " + Path.GetFileName(f));
                    }
                    catch { }
                }
            }

            if (File.Exists(o.StopFile))
            {
                File.Delete(o.StopFile);
            }
        }
        catch (Exception ex)
        {
            Log.Write("stale artifact cleanup failed: " + ex.Message);
        }
    }

    internal sealed class Options
    {
        public int Width = 1920;
        public int Height = 1080;
        public int RdpPort = 3389;
        public int ExitAfterSec = 0;
        public string LogPath = "";
        public string StatusPath = "";
        public string LaunchCommand = "";
        public string LaunchArguments = "";
        public string LaunchWorkingDir = "";
        public string BridgePath = "";
        public string LauncherPath = "";
        public string StopFile = "";

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string v = (i + 1 < args.Length) ? args[i + 1] : "";
                switch (a)
                {
                    case "--width": o.Width = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
                    case "--height": o.Height = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
                    case "--port": o.RdpPort = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
                    case "--exit-after": o.ExitAfterSec = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
                    case "--log": o.LogPath = v; i++; break;
                    case "--status": o.StatusPath = v; i++; break;
                    case "--launch": o.LaunchCommand = v; i++; break;
                    case "--launch-args": o.LaunchArguments = v; i++; break;
                    case "--launch-dir": o.LaunchWorkingDir = v; i++; break;
                    case "--bridge": o.BridgePath = v; i++; break;
                    case "--launcher": o.LauncherPath = v; i++; break;
                    case "--stop-file": o.StopFile = v; i++; break;
                }
            }
            if (string.IsNullOrEmpty(o.LogPath))
            {
                o.LogPath = Path.Combine(Path.GetTempPath(), "dsh-rdp-childhost.log");
            }
            if (string.IsNullOrEmpty(o.StatusPath))
            {
                o.StatusPath = Path.Combine(Path.GetTempPath(), "dsh-rdp-childhost.json");
            }
            if (string.IsNullOrEmpty(o.StopFile))
            {
                o.StopFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "dsh-rdp-wrapper", "ports", "childhost.stop");
            }
            return o;
        }
    }

    internal sealed class HostForm : Form
    {
        private readonly Options _o;
        private RdpAx _ax;
        private System.Windows.Forms.Timer _timer;
        private DateTime _started;
        private bool _connectedLogged;
        private uint _childSessionId;
        private bool _launched;

        public HostForm(Options o)
        {
            _o = o;
            _started = DateTime.UtcNow;

            Text = "dsh-rdp-child-host";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(320, 180);
            BackColor = Color.Black;

            _ax = new RdpAx();
            _ax.Dock = DockStyle.Fill;
            Controls.Add(_ax);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += OnTick;
            FormClosing += OnClosing;
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                _timer.Stop();
                if (_childSessionId != 0)
                {
                    Log.Write("logoff child session " + _childSessionId);
                    WTSLogoffSession(IntPtr.Zero, _childSessionId, false);
                    try { File.Delete(BridgePortFile(_childSessionId)); } catch { }
                }
                DeleteBridgeTask();
                try { File.Delete(_o.StopFile); } catch { }
                Status.Write(_o.StatusPath, false, _childSessionId, "stopped");
            }
            catch (Exception ex)
            {
                Log.Write("logoff failed: " + ex.Message);
            }
        }

        public void BeginConnect()
        {
            try
            {
                object ocx = _ax.Ocx;
                Log.Write("activex ocx acquired");

                Rdp.Com.Set(ocx, "Server", "localhost");
                Rdp.Com.Set(ocx, "DesktopWidth", _o.Width);
                Rdp.Com.Set(ocx, "DesktopHeight", _o.Height);
                Rdp.Com.Set(ocx, "ColorDepth", 32);
                Rdp.Com.Set(ocx, "ConnectingText", "dsh agent session");
                Rdp.Com.Set(ocx, "DisconnectedText", "dsh agent session closed");

                object secured = Rdp.Com.Get(ocx, "SecuredSettings2");
                if (secured != null)
                {
                    Rdp.Com.Set(secured, "KeyboardHookMode", 1);
                    Rdp.Com.Set(secured, "AudioRedirectionMode", 2);
                }

                object advanced = Rdp.Com.Get(ocx, "AdvancedSettings7");
                if (advanced != null)
                {
                    Rdp.Com.Set(advanced, "RDPPort", _o.RdpPort);
                    Rdp.Com.Set(advanced, "EnableCredSspSupport", true);
                    Rdp.Com.Set(advanced, "EnableWindowsKey", 1);
                    Rdp.Com.Set(advanced, "SmartSizing", true);
                }

                object connectToChild = true;
                IMsRdpExtendedSettings ext = (IMsRdpExtendedSettings)ocx;
                ext.set_Property("ConnectToChildSession", ref connectToChild);
                Log.Write("ConnectToChildSession=true");

                Rdp.Com.Invoke(ocx, "Connect");
                Log.Write("Connect() called");

                _timer.Start();
            }
            catch (Exception ex)
            {
                Log.Write("connect setup failed: " + ex);
                Status.Write(_o.StatusPath, false, 0, "connect setup failed: " + ex.Message);
                Close();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                int connected = 0;
                try
                {
                    object v = Rdp.Com.Get(_ax.Ocx, "Connected");
                    connected = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    Log.Write("read Connected failed: " + ex.Message);
                }

                if (connected == 1)
                {
                    if (!_connectedLogged)
                    {
                        _connectedLogged = true;
                        Log.Write("RDP connected");
                    }

                    uint id;
                    if (WTSGetChildSessionId(out id) && id != NoChildSessionId)
                    {
                        _childSessionId = id;
                        if (!_launched)
                        {
                            _launched = true;
                            _connectedAt = DateTime.UtcNow;
                            Log.Write("child session id=" + id);
                            Status.Write(_o.StatusPath, true, id, "connected");
                        }
                        if (!_bridgeStarted)
                        {
                            if (File.Exists(BridgePortFile(id)))
                            {
                                _bridgeStarted = true;
                                Log.Write("bridge port file is ready");
                                Status.Write(_o.StatusPath, true, id, "bridge-ready");
                                DeleteBridgeTask();
                            }
                            else if (string.IsNullOrEmpty(_taskName))
                            {
                                CreateBridgeTask(id);
                            }
                            else if ((DateTime.UtcNow - _lastLaunchAttempt).TotalSeconds >= 2)
                            {
                                _lastLaunchAttempt = DateTime.UtcNow;
                                string err;
                                if (!RunTaskInSession(id, out err))
                                {
                                    Log.Write("RunEx attempt " + _runExCount + " failed: " + err);
                                }
                            }
                        }
                    }
                }

                if (File.Exists(_o.StopFile))
                {
                    Log.Write("stop file present, shutting down");
                    try { File.Delete(_o.StopFile); } catch { }
                    Close();
                    return;
                }

                if (_o.ExitAfterSec > 0 &&
                    (DateTime.UtcNow - _started).TotalSeconds > _o.ExitAfterSec)
                {
                    Log.Write("exit-after reached");
                    Close();
                }
            }
            catch (Exception ex)
            {
                Log.Write("tick failed: " + ex);
            }
        }

        private bool _bridgeStarted;
        private DateTime _lastLaunchAttempt;
        private bool _waitLogged;
        private string _taskName = "";
        private DateTime _connectedAt;
        private int _runExCount;

        private void CreateBridgeTask(uint sessionId)
        {
            if (string.IsNullOrEmpty(_o.BridgePath))
            {
                return;
            }

            string launcher = _o.LauncherPath;
            if (string.IsNullOrEmpty(launcher))
            {
                launcher = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(_o.BridgePath), "..", "childhost", "child-bridge-launcher.ps1"));
            }
            if (!File.Exists(launcher))
            {
                Log.Write("launcher not found: " + launcher);
                return;
            }

            try
            {
                string user = WindowsIdentity.GetCurrent().Name;
                string taskName = "dsh-rdp-child-bridge-" + Guid.NewGuid().ToString("N");
                Type schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType == null)
                {
                    Log.Write("Schedule.Service unavailable");
                    return;
                }

                string powershell = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");

                object svcObj = Activator.CreateInstance(schedulerType);
                dynamic svc = svcObj;
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                dynamic td = svc.NewTask(0);

                td.RegistrationInfo.Author = "dsh-rdp-wrapper";
                td.RegistrationInfo.Description = "start dsh bridge inside the RDP child session";
                td.Settings.Enabled = true;
                td.Settings.Hidden = true;
                td.Settings.AllowDemandStart = true;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.ExecutionTimeLimit = "PT0S";
                td.Settings.MultipleInstances = 2; // ignore new instance while one runs
                td.Settings.StartWhenAvailable = true;
                td.Principal.UserId = user;
                td.Principal.LogonType = TaskLogonInteractiveToken;
                td.Principal.RunLevel = TaskRunLevelHighest;

                dynamic action = td.Actions.Create(TaskActionExecute);
                action.Path = powershell;
                action.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                                   + launcher + "\" -Bridge \"" + _o.BridgePath + "\" -SessionId " + sessionId;
                action.WorkingDirectory = Path.GetDirectoryName(_o.BridgePath);

                dynamic task = root.RegisterTaskDefinition(
                    taskName, td, TaskCreate, user, null, TaskLogonInteractiveToken, null);
                _taskName = taskName;
                Log.Write("bridge task registered for session " + sessionId + ": " + taskName);
            }
            catch (Exception ex)
            {
                Log.Write("logon task registration failed: " + ex);
            }
        }

        private void DeleteBridgeTask()
        {
            if (string.IsNullOrEmpty(_taskName))
            {
                return;
            }
            string name = _taskName;
            _taskName = "";
            try
            {
                Type schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                object svcObj = Activator.CreateInstance(schedulerType);
                dynamic svc = svcObj;
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                root.DeleteTask(name, 0);
                Log.Write("bridge task deleted: " + name);
            }
            catch (Exception ex)
            {
                Log.Write("delete logon task failed: " + ex.Message);
            }
        }

        private static string BridgePortFile(uint sessionId)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "dsh-rdp-wrapper", "ports", "bridge-session-" + sessionId + ".json");
        }

        private bool RunTaskInSession(uint sessionId, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(_taskName))
            {
                error = "no task registered";
                return false;
            }
            if (_runExCount >= 15)
            {
                error = "attempt limit reached";
                return false;
            }
            _runExCount++;
            try
            {
                Type schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType == null)
                {
                    error = "Schedule.Service unavailable";
                    return false;
                }
                object svcObj = Activator.CreateInstance(schedulerType);
                dynamic svc = svcObj;
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                dynamic task = root.GetTask(_taskName);
                task.RunEx(null, TaskRunUseSessionId, sessionId, null);
                Log.Write("RunEx session=" + sessionId + " ok");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private void LaunchBridge(uint childSessionId)
        {
            if (_bridgeStarted)
            {
                return;
            }
            if (string.IsNullOrEmpty(_o.LaunchCommand) && !string.IsNullOrEmpty(_o.BridgePath))
            {
                _o.LaunchCommand = "powershell.exe";
                _o.LaunchArguments =
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                    + _o.BridgePath + "\" -Port 0";
                _o.LaunchWorkingDir = Path.GetDirectoryName(_o.BridgePath);
            }
            if (string.IsNullOrEmpty(_o.LaunchCommand))
            {
                return;
            }

            int explorerPid = FindProcessInSession("explorer", childSessionId);
            if (explorerPid == 0)
            {
                if (!_waitLogged)
                {
                    _waitLogged = true;
                    Log.Write("waiting for explorer.exe in session " + childSessionId);
                }
                return;
            }

            string error;
            if (TryLaunchWithToken(explorerPid, out error))
            {
                _bridgeStarted = true;
                Log.Write("bridge process created in session " + childSessionId);
                Status.Write(_o.StatusPath, true, childSessionId, "bridge-launched");
                DeleteBridgeTask();
            }
            else
            {
                Log.Write("token launch failed: " + error);
                Status.Write(_o.StatusPath, false, childSessionId, "token launch failed: " + error);
            }
        }

        private static int FindProcessInSession(string name, uint sessionId)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    if ((uint)p.SessionId == sessionId)
                    {
                        return p.Id;
                    }
                }
                catch { }
            }
            return 0;
        }

        private bool TryLaunchWithToken(int sourcePid, out string error)
        {
            error = "";
            IntPtr hProcess = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr hDup = IntPtr.Zero;
            IntPtr env = IntPtr.Zero;
            try
            {
                EnablePrivilege("SeDebugPrivilege");
                EnablePrivilege("SeAssignPrimaryTokenPrivilege");
                EnablePrivilege("SeIncreaseQuotaPrivilege");

                hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, sourcePid);
                if (hProcess == IntPtr.Zero)
                {
                    error = "OpenProcess: " + Marshal.GetLastWin32Error();
                    return false;
                }
                if (!OpenProcessToken(hProcess, TOKEN_ALL_ACCESS, out hToken))
                {
                    error = "OpenProcessToken: " + Marshal.GetLastWin32Error();
                    return false;
                }
                if (!DuplicateTokenEx(hToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out hDup))
                {
                    error = "DuplicateTokenEx: " + Marshal.GetLastWin32Error();
                    return false;
                }

                CreateEnvironmentBlock(out env, hDup, false);

                string exe = _o.LaunchCommand;
                if (exe.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exe = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                }
                string cmdLine = "\"" + exe + "\" " + _o.LaunchArguments;

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = "winsta0\\default";
                PROCESS_INFORMATION pi;

                uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
                uint sourceSession;
                ProcessIdToSessionId(sourcePid, out sourceSession);
                Log.Write("source pid=" + sourcePid + " session=" + sourceSession
                          + " target=" + _childSessionId);

                bool ok = CreateProcessAsUserW(
                    hDup, null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    env, _o.LaunchWorkingDir, ref si, out pi);
                int firstError = ok ? 0 : Marshal.GetLastWin32Error();
                if (ok)
                {
                    uint actual;
                    ProcessIdToSessionId(pi.dwProcessId, out actual);
                    Log.Write("CreateProcessAsUser pid=" + pi.dwProcessId + " session=" + actual);
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                    if (actual == _childSessionId)
                    {
                        return true;
                    }
                    try { Process.GetProcessById(pi.dwProcessId).Kill(); } catch { }
                    error = "created in wrong session " + actual;
                    return false;
                }

                Log.Write("CreateProcessAsUser failed: " + firstError + ", trying CreateProcessWithTokenW");
                PROCESS_INFORMATION pi2;
                ok = CreateProcessWithTokenW(
                    hDup, 1, null, cmdLine, flags, env, _o.LaunchWorkingDir, ref si, out pi2);
                if (ok)
                {
                    uint actual;
                    ProcessIdToSessionId(pi2.dwProcessId, out actual);
                    Log.Write("CreateProcessWithTokenW pid=" + pi2.dwProcessId + " session=" + actual);
                    CloseHandle(pi2.hThread);
                    CloseHandle(pi2.hProcess);
                    if (actual == _childSessionId)
                    {
                        return true;
                    }
                    try { Process.GetProcessById(pi2.dwProcessId).Kill(); } catch { }
                    error = "fallback created in wrong session " + actual;
                    return false;
                }

                error = "CreateProcessAsUser=" + firstError
                        + " CreateProcessWithTokenW=" + Marshal.GetLastWin32Error();
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (hDup != IntPtr.Zero) CloseHandle(hDup);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            }
        }

        private static void EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out token))
            {
                Log.Write("EnablePrivilege " + name + ": OpenProcessToken " + Marshal.GetLastWin32Error());
                return;
            }
            try
            {
                LUID luid;
                if (!LookupPrivilegeValueW(null, name, out luid))
                {
                    Log.Write("EnablePrivilege " + name + ": LookupPrivilegeValue " + Marshal.GetLastWin32Error());
                    return;
                }
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privilege.Luid = luid;
                tp.Privilege.Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    Log.Write("EnablePrivilege " + name + ": AdjustTokenPrivileges " + Marshal.GetLastWin32Error());
                }
                else if (Marshal.GetLastWin32Error() == 1300)
                {
                    Log.Write("EnablePrivilege " + name + ": not held");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }

    internal sealed class RdpAx : AxHost
    {
        public RdpAx() : base(RdpClientClsid) { }

        public object Ocx
        {
            get
            {
                IntPtr forceCreate = Handle;
                GC.KeepAlive(forceCreate);
                object ocx = GetOcx();
                if (ocx == null)
                {
                    throw new InvalidOperationException("RDP ActiveX not initialized");
                }
                return ocx;
            }
        }
    }

    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMsRdpExtendedSettings
    {
        void set_Property(
            [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    internal static class Rdp
    {
        public static class Com
        {
            public static object Get(object target, string name)
            {
                return target.GetType().InvokeMember(
                    name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
            }

            public static void Set(object target, string name, object value)
            {
                target.GetType().InvokeMember(
                    name, BindingFlags.SetProperty, null, target, new object[] { value }, CultureInfo.InvariantCulture);
            }

            public static object Invoke(object target, string name, params object[] args)
            {
                return target.GetType().InvokeMember(
                    name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);
            }
        }
    }

    internal static class Log
    {
        private static string _path = "";
        private static readonly object Sync = new object();

        public static void Init(string path)
        {
            _path = path;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(
                        _path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " [" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal static class Status
    {
        public static void Write(string path, bool ok, uint childSessionId, string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"ok\":").Append(ok ? "true" : "false");
                sb.Append(",\"childSessionId\":").Append(childSessionId.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"pid\":").Append(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"user\":\"").Append(Json.Escape(WindowsIdentity.GetCurrent().Name)).Append("\"");
                sb.Append(",\"message\":\"").Append(Json.Escape(message)).Append("\"");
                sb.Append(",\"at\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\"");
                sb.Append("}");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    internal static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
