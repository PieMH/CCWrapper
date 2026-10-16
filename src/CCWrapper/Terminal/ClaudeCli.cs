using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CCWrapper.Core;

namespace CCWrapper.Terminal;

/// <summary>One finished run of a claude subcommand. <see cref="Output"/> and <see cref="Error"/> are stdout and stderr
/// without ANSI codes.</summary>
public sealed record CliResult(int ExitCode, string Output, string Error, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>stdout then stderr, or a note when there was neither.</summary>
    public string Text => TimedOut ? "Timed out." : (Output + "\n" + Error).Trim() is { Length: > 0 } t ? t : "(no output)";

    /// <summary>The first non-empty line of <see cref="Text"/>: what a status line shows.</summary>
    public string Line => Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
}

/// <summary>Runs claude.exe subcommands (<c>plugin …</c>, <c>mcp …</c>) hidden, under the window's account. They change
/// Claude Code's own files the way /plugin and /mcp do, without a session and without calling the model, so the user's
/// open sessions are never typed into.</summary>
public static class ClaudeCli
{
    /// <summary><c>claude &lt;args&gt;</c> with the account's env, in <paramref name="cwd"/> (the project and local scopes
    /// belong to that folder; the user profile when it is null or gone). stdin gets <paramref name="stdin"/> and is then
    /// closed, so a question claude would ask reads end of input instead of waiting. After <paramref name="timeout"/> or on
    /// <paramref name="cancel"/> the process tree is killed. The exe is a parameter only so a test can run something quick.</summary>
    public static async Task<CliResult> RunAsync(Account account, string? cwd, IReadOnlyList<string> args, TimeSpan timeout,
        string? stdin = null, CancellationToken cancel = default, string? exe = null)
    {
        var psi = new ProcessStartInfo(exe ?? ClaudeLaunch.FindClaude())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, // claude writes UTF-8 (…, √, ×)
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = cwd is not null && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (key, value) in ClaudeLaunch.Env(account, Guid.Empty, Environment.GetEnvironmentVariables())) psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        catch (IOException) { } // it exited before reading: its output says why

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(cts.Token); // a child left running keeps the pipes open after claude exits
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // ended meanwhile
            if (cancel.IsCancellationRequested) throw;
            return new CliResult(-1, "", "", TimedOut: true);
        }
        return new CliResult(process.ExitCode, Plain(await stdout), Plain(await stderr));
    }

    static string Plain(string text) => Regex.Replace(text, @"\x1b\[[0-9;?]*[A-Za-z]", "").Trim();

    /// <summary>How a command reads in the status strip: <c>claude plugin disable x --scope user</c>, arguments with spaces quoted.</summary>
    public static string Display(IEnumerable<string> args) =>
        "claude " + string.Join(' ', args.Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a));
}
