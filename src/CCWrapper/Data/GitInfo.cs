using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace CCWrapper.Data;

/// <param name="PushAccount">The GitHub account this repo pushes as; null when unknown.</param>
public sealed record GitInfo(bool InRepo, string? PushAccount, string? UserName, string? UserEmail)
{
    public static readonly GitInfo None = new(false, null, null, null);
    const int TimeoutMs = 5000;

    /// <summary>The top bar text: "none" outside a git work tree, "—" inside one when the account cannot be found.</summary>
    public string Label => !InRepo ? "none" : PushAccount ?? "—";

    /// <summary>Asks git (and, last, gh) about <paramref name="cwd"/>. Never throws except on cancellation; a failing or hung tool just yields less.</summary>
    public static async Task<GitInfo> ReadAsync(string? cwd, CancellationToken ct = default)
    {
        if (!Directory.Exists(cwd)) return None;
        if (await Run("git", ["rev-parse", "--is-inside-work-tree"], cwd, ct) != "true") return None;

        var credential = Run("git", ["config", "--get", "credential.https://github.com.username"], cwd, ct);
        var origin = Run("git", ["config", "--get", "remote.origin.url"], cwd, ct);
        var name = Run("git", ["config", "--get", "user.name"], cwd, ct);
        var email = Run("git", ["config", "--get", "user.email"], cwd, ct);
        var account = await credential ?? UrlUser(await origin) ?? await Run("gh", ["config", "get", "-h", "github.com", "user"], cwd, ct);
        return new GitInfo(true, account, await name, await email);
    }

    // Only the user part: https://user:token@github.com/... must never leak its token.
    static string? UrlUser(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Split(':')[0] is { Length: > 0 } user
            ? Uri.UnescapeDataString(user) : null;

    /// <summary>Trimmed stdout when the tool exits 0 with output; null on any failure, missing exe or timeout.</summary>
    static async Task<string?> Run(string exe, string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutMs);
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return null;
            p.BeginErrorReadLine(); // drain stderr so a chatty tool cannot block on a full pipe
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 && output.Trim() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; } // exe not installed
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // our timeout fired
        finally
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            p?.Dispose();
        }
    }
}
