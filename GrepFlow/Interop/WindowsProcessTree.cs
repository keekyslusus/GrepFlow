namespace GrepFlow.Interop;

public sealed record WindowsProcessInfo(
    uint ProcessId,
    uint ParentProcessId,
    string ImageFileName);

public sealed class WindowsProcessSnapshot
{
    private readonly IReadOnlyDictionary<uint, WindowsProcessInfo> _processes;

    public WindowsProcessSnapshot(IEnumerable<WindowsProcessInfo> processes)
    {
        _processes = processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public bool ContainsImage(string imageFileName)
        => _processes.Values.Any(process => IsImage(process, imageFileName));

    public bool ContainsProcess(uint processId) => _processes.ContainsKey(processId);

    public bool IsDescendant(uint processId, uint ancestorProcessId)
    {
        var visited = new HashSet<uint>();
        var current = processId;
        while (current != 0 && visited.Add(current))
        {
            if (current == ancestorProcessId) return processId != ancestorProcessId;
            if (!_processes.TryGetValue(current, out var process)) return false;
            current = process.ParentProcessId;
        }

        return false;
    }

    public bool HasDescendantImage(uint ancestorProcessId, string imageFileName)
        => _processes.Values.Any(process =>
            IsImage(process, imageFileName) && IsDescendant(process.ProcessId, ancestorProcessId));

    public IReadOnlyList<uint> FindDescendantProcesses(uint ancestorProcessId, string imageFileName)
    {
        if (!ContainsProcess(ancestorProcessId)) return [];

        return _processes.Values
            .Where(process =>
                IsImage(process, imageFileName) && IsDescendant(process.ProcessId, ancestorProcessId))
            .Select(process => process.ProcessId)
            .Distinct()
            .ToArray();
    }

    public bool TryGetProcess(uint processId, out WindowsProcessInfo? process)
        => _processes.TryGetValue(processId, out process);

    public bool TryGetParentProcessId(uint processId, out uint parentProcessId)
    {
        if (_processes.TryGetValue(processId, out var process))
        {
            parentProcessId = process.ParentProcessId;
            return parentProcessId != 0;
        }

        parentProcessId = 0;
        return false;
    }

    private static bool IsImage(WindowsProcessInfo process, string imageFileName)
        => string.Equals(process.ImageFileName, imageFileName, StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsProcessTree
{
    public WindowsProcessSnapshot Capture()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
            return new WindowsProcessSnapshot([]);

        try
        {
            var processes = new List<WindowsProcessInfo>();
            var entry = new NativeMethods.PROCESSENTRY32
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32>(),
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
                return new WindowsProcessSnapshot(processes);

            do
            {
                processes.Add(new WindowsProcessInfo(
                    entry.ProcessId,
                    entry.ParentProcessId,
                    entry.ExecutableFile));
                entry.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32>();
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return new WindowsProcessSnapshot(processes);
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }
}
