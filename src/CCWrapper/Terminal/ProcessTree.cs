using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CCWrapper.Terminal;

/// <summary>Who started whom, from one Toolhelp snapshot of the running processes.</summary>
public static class ProcessTree
{
    /// <summary>pid → parent pid of every running process; empty when the snapshot fails.</summary>
    public static Dictionary<int, int> Parents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return parents;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return parents;
    }

    /// <summary>"PowerShell (pid 51708, since 15:07)": where a claude process runs, named by the program that started it.</summary>
    public static string Describe(int pid)
    {
        string? parent = null;
        DateTime? since = null;
        try { using var p = Process.GetProcessById(pid); since = p.StartTime; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        if (Parents().TryGetValue(pid, out var ppid))
            try { using var pp = Process.GetProcessById(ppid); parent = pp.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { } // its terminal is gone
        return $"{HostName(parent)} (pid {pid}{(since is { } s ? $", since {s:HH:mm}" : "")})";
    }

    /// <summary>What the user calls the program that started a claude process.</summary>
    public static string HostName(string? process) => process?.ToLowerInvariant() switch
    {
        "powershell" or "pwsh" => "PowerShell",
        "cmd" => "Command Prompt",
        "windowsterminal" => "Windows Terminal",
        "ccwrapper" => "a CCWrapper window",
        "code" => "VS Code",
        "bash" or "mintty" => "Git Bash",
        null or "" => "another terminal",
        _ => process,
    };

    /// <summary>Ends a process and everything it started, as closing its terminal would. False when it cannot be ended.</summary>
    public static bool End(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }

    /// <summary>True when <paramref name="ancestor"/> started <paramref name="pid"/>, directly or through a few launchers.
    /// The depth limit also ends a loop that pid reuse can make in the parent links.</summary>
    public static bool IsDescendant(IReadOnlyDictionary<int, int> parents, int pid, int ancestor)
    {
        for (var depth = 0; depth < 4 && parents.TryGetValue(pid, out var parent) && parent != pid; depth++, pid = parent)
            if (parent == ancestor) return true;
        return false;
    }

    const uint SnapProcess = 0x2;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
