// Claude Code's statusLine command for CCWrapper tabs: copies the statusLine JSON from stdin to
// %LOCALAPPDATA%\CCWrapper\live\%CCW_TAB%.json (temp file + move, so readers never see half a file).
// Prints nothing, so the TUI looks unchanged, and always exits 0.
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
