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
