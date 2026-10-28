namespace CCWrapper.Core;

/// <summary>Busy/Waiting/Idle mirror <c>sessions\&lt;pid&gt;.json</c> "status"; Exited = the claude.exe of this tab ended.</summary>
public enum SessionStatus { Unknown, Busy, Waiting, Idle, Exited }

/// <summary>Input side of one terminal tab; implemented by the terminal (T2).</summary>
public interface ITerminalChannel
{
    /// <summary>Inserts text into CC's input as one paste: newlines do not submit, no Enter is added.</summary>
    void SendText(string text);

    /// <summary>Writes a raw VT input sequence as-is, e.g. "\r" (Enter) or "\x1b[Z" (Shift+Tab).</summary>
    void SendKeys(string keys);
}

/// <summary>One Claude Code tab in this window: the contract views bind to.
/// PropertyChanged may fire off the UI thread (WPF marshals scalar bindings).</summary>
public sealed class LiveSession(Account account) : Observable
{
    /// <summary>Passed to claude.exe as CCW_TAB; names the relay's live\&lt;TabId&gt;.json.</summary>
    public Guid TabId { get; } = Guid.NewGuid();
    public Account Account { get; } = account;

    public string? SessionId { get; set => Set(ref field, value); }
    public string? Cwd { get; set => Set(ref field, value); }

    /// <summary>Name of the git worktree the session runs in (<c>--worktree</c>); "…" while launched with the flag but not yet
    /// seen in its folder; null when it runs in no worktree. The tab shows a mark while it is set.</summary>
    public string? Worktree { get; set => Set(ref field, value); }

    /// <summary>Launched as a new session with <c>--worktree</c>: Claude Code moved it into the worktree, so its folder
    /// (and transcript project) is the worktree root. A session that only runs inside a worktree keeps its launch folder.</summary>
    public bool WorktreeLaunch { get; set; }

    /// <summary>Set at launch (<see cref="ClaudePaths.Transcript"/>), later refreshed from the status line.</summary>
    public string? TranscriptPath { get; set => Set(ref field, value); }
    public string? Title { get; set => Set(ref field, value); }
    public SessionStatus Status { get; set => Set(ref field, value); }

    /// <summary>Registry "waitingFor" while <see cref="Status"/> is Waiting (a dialog or permission prompt is open).</summary>
    public string? WaitingFor { get; set => Set(ref field, value); }
    public StatusLineData? StatusLine { get; set => Set(ref field, value); }

    /// <summary>Latest "permission-mode" record of the transcript, e.g. default, acceptEdits, plan, auto.</summary>
    public string? PermissionMode { get; set => Set(ref field, value); }

    /// <summary>Process id of this tab's claude.exe (matches <c>sessions\&lt;pid&gt;.json</c>); null before it starts and after it exits.</summary>
    public int? Pid { get; set => Set(ref field, value); }

    /// <summary>Attached by the terminal (T2) once the tab's PTY runs; null before and after.</summary>
    public ITerminalChannel? Channel { get; set => Set(ref field, value); }

    /// <summary>False when no terminal is attached, so the caller can keep the text.</summary>
    public bool SendText(string text)
    {
        if (Channel is not { } c) return false;
        c.SendText(text);
        return true;
    }

    /// <summary>False when no terminal is attached.</summary>
    public bool SendKeys(string keys)
    {
        if (Channel is not { } c) return false;
        c.SendKeys(keys);
        return true;
    }
}
