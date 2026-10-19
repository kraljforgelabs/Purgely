using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Purgely.Core;

public enum UninstallPhase { Starting, Running, Finished }

/// <summary>A snapshot of what the official uninstaller is doing, reported about twice a second.</summary>
public sealed record UninstallStatus(
    UninstallPhase Phase,
    TimeSpan Elapsed,
    int Processes,          // uninstaller processes still running (including helpers it started)
    bool HasWindow,         // any of them shows a window the user can answer
    bool LooksStuck,        // no window and no CPU work for a while
    bool StillListed);      // Windows still lists the program

/// <summary>
/// Follows an official uninstaller from start to finish – including the helper processes it hands over to
/// (many uninstallers copy themselves to Temp and exit at once) and uninstallers that run silently without
/// any window (e.g. OneDrive). It never kills anything by itself: <see cref="StopUninstaller"/> is only called
/// after the user chose "force uninstall".
/// </summary>
public sealed class UninstallMonitor
{
    private readonly InstalledProgram _program;
    /// <summary>Tracked processes: PID together with its start time, so a reused PID is never mistaken for ours.</summary>
    private readonly Dictionary<int, DateTime> _tracked = new();
    private readonly object _lock = new();
    private DateTime _goneSince = DateTime.MaxValue;

    /// <summary>Programs an uninstaller may start that are not part of the uninstall (feedback page, shell restart).</summary>
    private static readonly HashSet<string> NeverTracked = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "msedge", "chrome", "firefox", "opera", "brave", "iexplore", "vivaldi", "werfault", "conhost",
        "msedgewebview2", "searchhost", "applicationframehost",
    };
    private readonly DateTime _started = DateTime.Now;
    private TimeSpan _lastCpu;
    private DateTime _lastActivity = DateTime.Now;

    private readonly bool _quiet;

    /// <summary>How the uninstaller was started when quiet mode was used ("" = normal, with its own window).</summary>
    public string QuietMethod { get; private set; } = "";

    public UninstallMonitor(InstalledProgram program, bool quiet = false)
    {
        _program = program;
        _quiet = quiet;
    }

    /// <summary>Starts the official uninstaller (quietly when asked and possible). Throws if it is missing or can't start.</summary>
    public void Start()
    {
        Process? process;
        if (_quiet && UninstallCommand.RunQuiet(_program) is { } quiet)
        {
            process = quiet.Process;
            QuietMethod = quiet.Method;
        }
        else process = UninstallCommand.Run(_program);
        if (process is not null)
        {
            try { lock (_lock) _tracked[process.Id] = process.StartTime; } catch (Exception) { /* exited instantly */ }
        }
    }

    /// <summary>Watches until the uninstaller and all its helpers have exited, or until cancelled.</summary>
    public async Task<UninstallStatus> WatchAsync(IProgress<UninstallStatus> progress, CancellationToken cancel)
    {
        var quietSince = DateTime.Now;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var status = await Task.Run(Sample, cancel);
            progress.Report(status);

            if (status.Processes > 0) quietSince = DateTime.Now;
            if (status.StillListed) _goneSince = DateTime.MaxValue;
            else if (_goneSince == DateTime.MaxValue) _goneSince = DateTime.Now;
            // Finished when nothing has run for 3 s (some uninstallers start the next step with a small gap),
            // at once when the program is no longer listed and nothing runs, or 20 s after the program left the
            // list even if a helper lingers (e.g. a "thank you" window).
            var quiet = DateTime.Now - quietSince;
            if ((status.Processes == 0 && (quiet > TimeSpan.FromSeconds(3) || !status.StillListed)) ||
                DateTime.Now - _goneSince > TimeSpan.FromSeconds(20))
                return status with { Phase = UninstallPhase.Finished };
            await Task.Delay(400, cancel);
        }
    }

    private UninstallStatus Sample()
    {
        lock (_lock)
        {
            // Follow children: a process counts as ours only if its parent is one of ours (same PID AND start time)
            // and it started after that parent.
            var all = ProcessTable();
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent, exe) in all)
                {
                    if (_tracked.ContainsKey(pid) || !_tracked.TryGetValue(parent, out var parentStart)) continue;
                    if (NeverTracked.Contains(Path.GetFileNameWithoutExtension(exe))) continue;
                    var start = StartTimeOf(pid);
                    if (start is null || start < parentStart || !SameProcess(parent, parentStart)) continue;
                    _tracked[pid] = start.Value;
                    added = true;
                }
            } while (added);

            var alive = 0;
            var window = false;
            var cpu = TimeSpan.Zero;
            foreach (var (pid, started) in _tracked.ToList())
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited || p.StartTime != started) { _tracked.Remove(pid); continue; }
                    alive++;
                    if (p.MainWindowHandle != IntPtr.Zero) window = true;
                    cpu += p.TotalProcessorTime;
                }
                catch (Exception) { _tracked.Remove(pid); }
            }

            if (cpu - _lastCpu > TimeSpan.FromMilliseconds(50) || window) _lastActivity = DateTime.Now;
            _lastCpu = cpu;
            var stuck = alive > 0 && !window && DateTime.Now - _lastActivity > TimeSpan.FromSeconds(90);

            return new UninstallStatus(UninstallPhase.Running, DateTime.Now - _started, alive, window, stuck,
                RegistryPath.Exists(_program.RegistryKey));
        }
    }

    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception) { return null; }
    }

    private bool SameProcess(int pid, DateTime start) => StartTimeOf(pid) is { } s ? s == start : true; // exited parent: still fine

    /// <summary>Names of the uninstaller processes still running (for the force-uninstall confirmation).</summary>
    public List<string> RunningNames()
    {
        lock (_lock)
        {
            var names = new List<string>();
            foreach (var (pid, started) in _tracked)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited && p.StartTime == started) names.Add(p.ProcessName + ".exe");
                }
                catch (Exception) { }
            }
            return names;
        }
    }

    /// <summary>Stops the uninstaller and its helpers – only after the user chose "force uninstall".</summary>
    public int StopUninstaller()
    {
        lock (_lock)
        {
            var stopped = 0;
            foreach (var (pid, started) in _tracked)
            {
                if (pid == Environment.ProcessId) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited || p.StartTime != started) continue; // a different process reused this PID
                    if (NeverTracked.Contains(p.ProcessName)) continue;
                    p.Kill(entireProcessTree: false);
                    stopped++;
                }
                catch (Exception) { }
            }
            return stopped;
        }
    }

    /// <summary>True while Windows Installer is busy with an install or uninstall (the global MSI mutex is held).</summary>
    public static bool WindowsInstallerBusy()
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Global\_MSIExecute", out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // exists, held by the Installer service
        catch (Exception) { return false; }
    }

    // ---------- process table (pid → parent pid) via the Toolhelp snapshot ----------

    private static List<(int Pid, int Parent, string Exe)> ProcessTable()
    {
        var result = new List<(int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
