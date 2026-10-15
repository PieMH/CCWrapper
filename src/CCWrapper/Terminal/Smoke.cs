namespace CCWrapper.Terminal;

/// <summary><c>CCWrapper.exe --smoke</c>: round-trips <c>cmd /c echo</c> through ConPTY into xterm.js.</summary>
public static class Smoke
{
    /// <summary>Returns the process exit code: 0 on success.</summary>
    public static Task<int> RunAsync() => Task.FromResult(1); // TODO(T2): implement; 1 = not implemented yet
}
