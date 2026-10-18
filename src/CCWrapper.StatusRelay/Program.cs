// Two jobs for CCWrapper tabs, both silent.
// No arguments: Claude Code's statusLine command. Copies the statusLine JSON from stdin to
// %LOCALAPPDATA%\CCWrapper\live\%CCW_TAB%.json (temp file + move, so readers never see half a file).
// Prints nothing, so the TUI looks unchanged, and always exits 0.
// --browser <pipe>: the stdio MCP server of the Browser tool. Copies bytes between stdin/stdout and the window's named pipe
// until either side ends; CCWrapper.exe speaks MCP on the other end.
using System.IO.Pipes;

if (args is ["--browser", ..]) return Browser.Bridge(args.Length == 2 ? args[1] : "");

string? temp = null;
try
{
    // CCW_TAB names the file: accept only a GUID so it can never point outside live\.
    if (Guid.TryParse(Environment.GetEnvironmentVariable("CCW_TAB"), out var tab))
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCWrapper", "live");
        Directory.CreateDirectory(dir);
        temp = Path.Combine(dir, $"{tab}.{Environment.ProcessId}.tmp");
        using (var file = File.Create(temp))
        using (var stdin = Console.OpenStandardInput())
            stdin.CopyTo(file); // raw bytes: the JSON stays UTF-8 whatever the console code page
        File.Move(temp, Path.Combine(dir, $"{tab}.json"), overwrite: true);
    }
}
catch (Exception)
{
    // never disturb Claude Code: a lost update is replaced by the next one
}
finally
{
    try { if (temp is not null) File.Delete(temp); } // left only when the move failed
    catch (Exception) { }
}
return 0;

static class Browser
{
    const string Prefix = "CCWrapper.browser.";

    public static int Bridge(string name)
    {
        // Only a CCWrapper window's pipe (CCWrapper.browser.<pid>): never a way to reach some other pipe.
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length == Prefix.Length || !name[Prefix.Length..].All(char.IsAsciiDigit))
        {
            Console.Error.WriteLine($"CCWrapper browser: not a CCWrapper pipe name: {name}");
            return 1;
        }
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { pipe.Connect(10_000); } // Claude Code does not retry a server that fails to start
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CCWrapper browser: cannot reach the CCWrapper window ({name}): {ex.Message}");
            return 1;
        }
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        // Raw bytes both ways (no console encoding); whichever side ends first ends the bridge.
        Task.WaitAny(Task.Run(() => stdin.CopyTo(pipe)), Task.Run(() => pipe.CopyTo(stdout)));
        return 0;
    }
}
