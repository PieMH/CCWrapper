# CCWrapper: a lightweight Windows GUI around Claude Code, one account per window

## Context

You run 3 Claude Code accounts on this PC (profile functions in `Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1`):

| Letter | Account | Config dir | Global config (`.claude.json`) |
|---|---|---|---|
| A | GUFO team (admin) | `~\.claude` (CLAUDE_CONFIG_DIR **unset**) | `~\.claude.json` |
| B | GUFO team (owner) | `~\.claude-b` | `~\.claude-b\.claude.json` |
| C | Personal Pro | `~\.claude-c` | `~\.claude-c\.claude.json` |

Claude Desktop keeps running its one account as today. For the other accounts you work in bare PowerShell, which is clumsy: no search, no Ctrl+V for images or files, no session overview, hard to see context and usage, file changes or subagents, awkward multi-line input and scrollback.

**Outcome:** a lightweight native app that wraps the terminal where Claude Code runs, unchanged and with each account's own settings, and adds a Desktop-like GUI around it. **Each window is one account.** Open several windows to run several accounts in parallel, next to Claude Desktop.

**Closest prior art:** Termexo (Tauri, Windows, one config dir per account) and Pane (Electron, xterm.js, diff and browser panels). Both are worth 5 minutes for ideas. We build our own because you want .NET and this exact layout.

## Decisions (from the brainstorm)

1. **Terminal wrapper, not a chat clone.** Claude Code's own TUI stays the source of truth for chat, permissions, menus and slash commands. Every CC update works immediately.
2. **Respect each account's settings.**
   - The app does not change how Claude Code behaves. If an account uses fullscreen mode, the app shows fullscreen.
   - **Launch flags:** the New-session dialog defaults to the account's `model`, `effortLevel` and `defaultMode`, and passes a flag only when you change one.
   - **Status line:** the only thing the app adds is a hidden statusLine relay, and only when the account has no statusLine of its own. With one, the relay is skipped, and the bar falls back to transcript data and cached usage.
3. **The app reads, it never writes.**
   - It reads only files Claude Code already writes, plus the documented statusLine JSON.
   - It never edits your settings, never reads `.credentials.json` and never calls Anthropic APIs. It only launches the unmodified `claude.exe` under your own logins, which keeps it within the usage policy.
4. **One account per window.**
   - **Startup:** an account picker plus a list of recent sessions marked by account.
   - **Switching:** "Switch account" in the top bar restarts the window with the other account. Running sessions in it end and stay resumable; you get a confirmation first if any is busy.
   - **Several accounts:** open another window.
   - **Shortcuts:** `CCWrapper.exe --account b` skips the picker, so you can make one Start-menu shortcut per account.
   - **Taskbar:** the window title and taskbar overlay badge show the account letter and color.
5. **GitHub is shown, not switched.** The top bar shows the account the current repo uses, or `none` outside git.

### Classic vs fullscreen

You asked what I meant. Claude Code can draw itself in a terminal in two ways:
- **Classic** (default; B and C use it): it prints the conversation like normal command output. The terminal keeps the history, so you scroll with the scrollbar, select with the mouse, and Ctrl+F searches the whole chat.
- **Fullscreen** (`"tui": "fullscreen"`, set on account A): it takes over the window like a full-screen app (think vim) and redraws in place without flicker. It does its own scrolling and text selection, and the terminal only ever holds the current screen.

Since we respect settings, A stays fullscreen in the app. The only effect is that Ctrl+F in A finds text on the visible screen. For the whole chat, the find bar offers "Search full transcript", which opens the session's transcript view with matches highlighted. Shift+drag still selects text in A.

## Layout (your spec)

```
┌ CCWrapper │ ● C · Personal Pro  ⇄ Switch │ GitHub: PieMH ──────────────────────┐
├──────────────────┬─────────────────────────────────────┬─────────────────────────┤
│ 🔍 Search sessions│ [find in chat ____ 3/12 ↑↓] (Ctrl+F)│ Files │ Agents │ Browser │
│ ⚙ Settings  ▾    │                                     │ ▾ src/                  │
│ ✦ Customize      │  real Claude Code TUI               │    App.cs     +12 −3    │
│ ＋ New session    │  (account's own renderer, Ctrl+V    │    New.cs     +40   A   │
│ ──────────────── │   smart paste, drag&drop,           │ ▾ assets/               │
│ ● CCWrapper   now│   Shift+Enter newline)              │    logo.png  +1.2 KB    │
│ ◉ AIforBus…   2m │ ┌ compose ────────────────────────┐ │                         │
│ ○ n8n-job…    3d │ │ multi-line prompt, paste, drop  │ │                         │
│                  │ └─────────────────────────── Send ┘ │                         │
├──────────────────┴─────────────────────────────────────┴─────────────────────────┤
│ opus 5.5 ▾ │ effort xhigh ▾ │ mode auto ▾ │ ctx 202k/1M 20% │ in 1.2M out 48k $16.7 │ 5h 58% · wk 12% │
└────────────────────────────────────────────────────────────────────────────────────┘
● working   ◉ needs input   ○ open/idle   (no dot) closed
```

## Feature spec (v1)

**Start screen.** A "Who's working?" picker with 3 cards: letter avatar and color, email and org/plan from `oauthAccount`, and 5-hour and weekly usage bars. Below the cards is a recent-sessions list across all accounts, each row with its account badge, title, project and age (IDE style). Clicking a card opens the window as that account. Clicking a session opens that account and resumes it.

**Top bar.** It holds three things:
- the app name;
- the account (letter, color, name) with **Switch**, which brings back the picker and restarts the window as the chosen account;
- the GitHub label for the active session's cwd: `none` outside a git work tree. Otherwise the push account, taken from `credential.https://github.com.username`, else the username in the origin URL, else `gh config get -h github.com user`. The tooltip shows `user.name <user.email>`.

**Left bar, top to bottom:**
- **Search box.** It filters titles and projects instantly. Enter runs a full-text search over this account's transcripts and shows matching sessions with a snippet.
- **Settings menu.** Items: open `settings.json`, open user `CLAUDE.md`, open the config folder, and run `/config`, `/permissions`, `/hooks` or `/memory` in the active session.
- **Customize.** A page listing plugins (installed and enabled), skills (user and plugin), MCP servers (user, project and plugin) and connectors (`claude mcp list` output). Its buttons run CC's own `/plugin` or `/mcp` in the session, or open the claude.ai connectors page.
- **New session.** A dialog with a folder (recent projects of this account, or Browse), plus model, effort, permission mode and an optional name.
- **Session list.** Every session of this account, newest first, including ones started outside the app. Each row shows a status dot, title, project and age. The title comes from `custom-title`, else `ai-title`, else `last-prompt`.
  - Clicking a session open in this window shows it.
  - Clicking a closed session resumes it with `--resume <id>` in its original cwd.
  - A session running elsewhere (another window, plain PowerShell, Desktop) opens a read-only transcript view.

**Center: one Claude Code session at a time.** Other open sessions of this account keep running in the background. Key handling:
- **Shift+Enter:** newline, sent as ESC CR, with LF as a fallback.
- **Ctrl+V:**
  - a bitmap is sent as `ESC v`, Claude Code's own Windows image paste, which produces `[Image #N]`;
  - files, folders and videos from Explorer are inserted as quoted paths;
  - text goes in as a bracketed paste.
- **Drag & drop:** files are inserted as quoted paths.
- **Ctrl+C:** copies when text is selected, otherwise interrupts. Ctrl+Shift+C always copies.
- **Right-click:** copies the selection, or pastes when nothing is selected.
- **Ctrl+F:** opens a find bar with highlights, next/previous and a match count, plus "Search full transcript".
- **Links:** localhost links open in the Browser panel, others in the system browser.

When a session exits, an overlay offers Resume or Close.

**Compose box** (under the terminal):
- a multi-line editor: Enter sends, Shift+Enter adds a newline;
- smart paste and drop; pasted bitmaps are saved as temp PNGs and inserted as paths;
- Up arrow recalls history;
- sending is blocked while a dialog or permission prompt is open, which you answer in the terminal.

**Right panel: Files, Agents, Browser.**
- **Files.** A tree of the files Claude changed in this session, each with +/− lines (text) or a size delta (binary). The base is Claude Code's checkpoint backup from before its first edit. Clicking a file opens a viewer with the whole current file and the changed lines highlighted inline (DiffPlex).
- **Agents.** This session's subagents, running ones first, each with type and description, model, effort, context (last turn) and output tokens. Clicking one opens a live read-only transcript.
- **Browser.** WebView2 with an address bar, back, forward and reload.

**Bottom bar.**
- **Switches:**
  - **Model ▾** sends `/model <alias>`.
  - **Effort ▾** sends `/effort <level>`.
  - **Mode ▾** presses Shift+Tab until the mode recorded in the transcript matches.
  - All three are disabled while a dialog or permission prompt is open.
- **Meters:**
  - context: tokens, window and percent;
  - session tokens (in/out) and cost;
  - 5-hour and weekly plan usage, with "as of" in the tooltip when it comes from the cache.

**Attention.** The taskbar flashes when a background session of this window starts waiting for input. Its dot in the list turns ◉.

## Data sources (all local; read-only)

| Need | Source |
|---|---|
| Accounts, labels | `~\.claude` + `~\.claude-*` dirs; `<global>.oauthAccount` (emailAddress, displayName, organizationName/Type) |
| Session status | `<cfg>\sessions\<pid>.json`: sessionId, cwd, name, status busy/waiting/idle, waitingFor. Pid alive-check. |
| Live per-session data | statusLine JSON (documented) via relay → `%LOCALAPPDATA%\CCWrapper\live\<tabGuid>.json`: session_id, transcript_path, workspace.current_dir, model, effort.level, cost.*, context_window.* (size, used_percentage, totals), rate_limits (Pro/Max only) |
| Plan usage fallback | `<global>.cachedUsageUtilization` (fetchedAtMs, five_hour/seven_day.utilization, resets_at). Can be days old, e.g. A's is from 09/10. |
| Session list/titles | `<cfg>\projects\<enc-cwd>\<id>.jsonl` (head for cwd, tail ~64 KB for `ai-title`/`last-prompt`), `<id>\custom-title.json`, file mtime. Cache by (path, size, mtime). |
| Prompt history | `<cfg>\history.jsonl` (display, project, sessionId, timestamp) |
| Permission mode | latest `permission-mode` record in the transcript |
| Changed files | `file-history-snapshot` / `file-history-delta` records (trackingPath, backupFileName, version); backups in `<cfg>\file-history\<id>\<backupFileName>`; `backupFileName:null` = new file |
| Subagents | `<id>\subagents\agent-*.meta.json` (agentType, description, toolUseId) + `agent-*.jsonl` (assistant `message.model`, `effort`, `message.usage`, `stop_reason`) |
| Customize page | `<cfg>\settings.json` enabledPlugins, `<cfg>\plugins\installed_plugins.json`, `<cfg>\skills\*`, plugin `skills\*\SKILL.md`, `<global>.mcpServers` + `projects[cwd].mcpServers`, `claude mcp list` |

**Launch command per session.**
- **Environment:** account env (A: unset `CLAUDE_CONFIG_DIR`; B/C: set it), plus `CCW_TAB=<guid>` and `COLORTERM=truecolor`.
- **Command line:** `claude.exe [--session-id <new uuid> | --resume <id>] [flags only if changed] [-n name] [--settings <relay.json>]`.
- **The relay settings file** is `{"statusLine":{"type":"command","command":"C:/…/CCWrapper.StatusRelay.exe"}}`. The path is unquoted, with forward slashes and no spaces, so it runs whether CC uses Git Bash or PowerShell. It is passed only when the account has no statusLine. Its output is empty, so the TUI looks as it does today.
- **The child environment block** is built explicitly: the parent env copied, then account keys set or removed. That way A never inherits a stray `CLAUDE_CONFIG_DIR`.

Undocumented formats can change with CC auto-updates. All parsing therefore lives in `Data/`. It tolerates unknown records, treats every field as optional, and shows "—" when data is missing.

## Tech stack and dev environment

- **C# / .NET 10 + WPF**, Fluent dark theme.
  - The SDK 10.0.401 is already installed.
  - C++ would be 3–5× the code for the same UI.
  - WinUI 3 needs extra workloads and packaging for no gain here.
- **Terminal: xterm.js 6.0.0** (the VS Code terminal engine) with addons: fit 0.11.0, search 0.16.0, web-links 0.12.0, webgl 0.19.0 (visible terminal only), unicode11 0.9.0, clipboard 0.2.0 (OSC 52, for fullscreen-mode copies).
  - It is vendored into `wwwroot/vendor` and hosted in one WebView2 (runtime 155 installed) holding N terminals.
  - **WebView2 user-data folder:** one per account, so parallel windows don't clash and each account keeps its own Browser cookies.
  - Microsoft.Terminal.Wpf was rejected. It is not on nuget.org and has no search, links, bracketed paste or key hook.
- **ConPTY: `Porta.Pty` 2.2.3**, pinned.
  - MIT license; prefers Microsoft's newer OpenConsole ConPTY and falls back to the one built into Windows.
  - It kills children through a Job Object, so no orphan `claude.exe` remains when a window closes or crashes.
- **Other packages:** `Microsoft.Web.WebView2` (latest stable), `DiffPlex.Wpf`, and xUnit for tests.
- **Bridge rules** (from the research):
  - send PTY output with `PostWebMessageAsString`, coalesced every ~10 ms, through a stateful UTF-8 decoder, with ack-based flow control;
  - debounce resizes and never fit hidden terminals;
  - set `AreBrowserAcceleratorKeysEnabled=false`;
  - avoid WPF overlays on top of WebView2 (the airspace problem). Use real windows or HTML overlays instead.
  - If fullscreen sessions show stale fragments under ConPTY, set `CLAUDE_CODE_ALT_SCREEN_FULL_REPAINT=1`. It is a rendering fix only and changes no behavior.

**Install:**
- **Already present:** VS 2026 Community 18.10 (C++ and Node workloads), .NET SDK 10.0.401, Windows 11 SDK 26100, WebView2 Runtime 155, Git 2.55, gh 2.101, Node 24 (used once to fetch xterm.js) and Claude Code 2.1.299.
- **One addition:** Visual Studio Installer → Modify → **".NET desktop development"**. You need it only to open, design and debug `CCWrapper.sln` in Visual Studio. The agents build with the `dotnet` CLI, which needs nothing more.

## Project layout

```
CCWrapper/
  CCWrapper.sln · .gitignore · docs/PLAN.md (this file)
  src/CCWrapper/                 WPF app (net10.0-windows)
    App.xaml(.cs)                startup: --account <x> or AccountPicker; restart-as-account helper
    Core/                        Account, AccountStore (discovery), ClaudePaths, SessionInfo, LiveSession (the contract panels bind to)
    Data/                        AccountReader, SessionCatalog, SessionSearch, GitInfo, TranscriptReader, FileChanges, SubagentReader, StatusLineFeed
    Terminal/                    PtySession, TerminalHost (WebView2 bridge), PasteHandler, wwwroot/{index.html,terminal.js,terminal.css,vendor/}
    Views/                       AccountPicker, MainWindow, Sidebar, NewSessionDialog, ComposeBox, BottomBar, CustomizePage,
                                 FilesPanel, FileDiffWindow, AgentsPanel, TranscriptWindow, BrowserPanel
  src/CCWrapper.StatusRelay/     console exe: stdin JSON → %LOCALAPPDATA%\CCWrapper\live\%CCW_TAB%.json (atomic write), prints nothing
  tests/CCWrapper.Tests/         xUnit with small synthetic JSONL fixtures (no real transcripts)
```

## Build plan (run with /agents-team)

**Step 0, which I do right after you approve:**
1. `git init` in `C:\Users\piemo\Projects\CCWrapper`.
2. Add a `.gitignore` (bin/obj/.vs/.agents-team).
3. Copy this plan to `docs/PLAN.md`.
4. Make the initial commit. Per your CLAUDE.md, it gets no AI trailer.

agents-team runs in git mode with worktrees.

**Then you run** (it is explicit-only, so I can't start it myself):
`/agents-team Build CCWrapper v1 as specified in docs/PLAN.md, using its task table --effort high --parallel 3`

Run it in a fresh session in this folder, from the account with the most headroom. Right now that's **B**: C is at 58% of its 5-hour window, and A's last-known weekly usage is 81%.

| ID | Task | Tier | Owns | Depends | Done when |
|---|---|---|---|---|---|
| T1 | Skeleton + contracts: sln, 3 projects, NuGet refs, App startup (`--account`, picker, restart-as-account helper), MainWindow 5-region layout hosting **stub UserControls for every view**, Core models and interfaces (`LiveSession`: Account, SessionId, Cwd, TranscriptPath, Status, StatusLine data, `SendText`/`SendKeys`; `IAppCommands`: ShowTranscript(session, query), OpenInBrowser(url), FlashAttention(). Later tasks implement it, so no task edits another's files), AccountStore discovery, vendored xterm.js files | opus | sln, all csproj, App.*, Core/*, Views/* stubs (ownership then passes on), wwwroot/vendor/*, tests csproj | — | `dotnet build` and `dotnet test` pass; `--account c` opens the empty shell |
| T2 | Terminal core: PtySession (Porta.Pty, account env, args, conditional relay settings), TerminalHost (one WebView2 per window with a per-account data folder, N xterm instances, bridge with flow control, resize debounce, Shift+Enter, Ctrl+C/V rules, right-click, drop, Ctrl+F find bar, link routing, exit overlay), PasteHandler, StatusRelay project, StatusLineFeed, a `--smoke` switch. If Porta.Pty can't set cwd and a full env block, hand-roll ConPTY from the Microsoft recipe instead. | opus | Terminal/*, src/CCWrapper.StatusRelay/*, Data/StatusLineFeed.cs, tests/Terminal/* | T1 | `CCWrapper.exe --smoke` round-trips `cmd /c echo` through ConPTY → xterm and exits 0; PasteHandler tests pass; real `claude.exe` tabs render correctly for C (classic) and A (fullscreen); the relay writes live JSON |
| T3 | Account and session data: AccountReader (oauthAccount, cachedUsageUtilization, settings defaults), SessionCatalog (titles, cwd, mtime cache, running registry, history.jsonl), SessionSearch (streaming scan with raw-line prefilter, whole account or one session), GitInfo, plus tests | sonnet | Data/{AccountReader,SessionCatalog,SessionSearch,GitInfo}.cs, tests/Data/* | T1 | Tests on fixtures pass; listing all of A's ~81 transcripts takes under 1 s once cached |
| T4 | Session detail data: TranscriptReader (incremental tail: model, effort, permission mode, usage, cost-state), FileChanges (base backup vs disk, binary detection, +/− counts), SubagentReader (meta + stats + running/done), plus tests | sonnet | Data/{TranscriptReader,FileChanges,SubagentReader}.cs, tests/Detail/* | T1 | Tests pass, covering new-file, binary and missing-backup cases |
| T5 | Shell wiring: AccountPicker (cards + recent sessions of all accounts), top bar (account, Switch → restart, GitHub label), Sidebar (search, Settings menu, New session, list with status dots, resume, read-only open), NewSessionDialog, session lifecycle and active-session switching, taskbar overlay + flash | sonnet | Views/{AccountPicker,MainWindow,Sidebar,NewSessionDialog}.* | T2, T3 | Manual checklist items 1, 8, 9, 11 pass |
| T6 | Input + bottom bar + customize: ComposeBox, BottomBar (switches and meters, cached-usage fallback), CustomizePage | sonnet | Views/{ComposeBox,BottomBar,CustomizePage}.* | T2, T3, T4 | Manual checklist items 2, 3, 5 pass |
| T7 | Right panel: FilesPanel tree + FileDiffWindow (DiffPlex, whole file inline), AgentsPanel + TranscriptWindow (live tail, search highlight, reused by "Search full transcript"), BrowserPanel | sonnet | Views/{FilesPanel,FileDiffWindow,AgentsPanel,TranscriptWindow,BrowserPanel}.* | T4 | Manual checklist items 4, 6, 7, 10 pass |
| QA | agents-team:qa over the whole diff | opus | — | all | Findings triaged |

Parallel waves: T1 → (T2, T3, T4) → (T5, T6, T7) → QA.

## Verification

- **Automated:** `dotnet build CCWrapper.sln`, `dotnet test`, `src\CCWrapper\bin\Debug\net10.0-windows\CCWrapper.exe --smoke`.
- **Manual end-to-end checklist** (about 10 minutes, done by you after the run):
  1. The picker shows A, B and C with email, plan and usage bars. The recent list has account badges.
  2. Pick C → New session here → type in compose with Shift+Enter newlines → Send. Paste a Win+Shift+S screenshot with Ctrl+V and get `[Image #1]`. Copy a file in Explorer, Ctrl+V, and its path is inserted. Drag a folder in and its path is inserted.
  3. Select text: Ctrl+C copies and doesn't interrupt. With no selection, Ctrl+C interrupts.
  4. Ctrl+F finds and highlights text in C's chat. In an A (fullscreen) session, "Search full transcript" opens the transcript with matches.
  5. Model, effort and mode switches change the TUI. After a reply, ctx, tokens, cost, 5h and weekly all update.
  6. Ask Claude to edit a text file and add an image. Files shows the +/− and the size delta. Clicking the text file shows the whole file with highlights.
  7. Ask for a subagent task. Agents shows it running with model, effort, ctx and tokens. Clicking it shows the live transcript.
  8. Switch → A: the window restarts as A, and A's TUI is fullscreen as configured. Launch `CCWrapper.exe --account b` and B runs in parallel. Taskbar badges tell the windows apart.
  9. Sidebar search for a word from an old prompt finds the session, and clicking it resumes.
  10. Open `http://localhost:…` in the Browser panel. A localhost link clicked in the terminal opens there.
  11. GitHub shows the account inside a repo and `none` outside git.
  12. Close a window: `Get-Process claude` shows none of its sessions left. Claude Desktop is unaffected.

## Risks and limits

- **Undocumented files:** the `sessions\*.json` registry, transcript records, `file-history` and `cachedUsageUtilization` can change with any CC release. All reading is isolated in `Data/`, so a change degrades to "—" instead of crashing.
- **Weekly usage on Team accounts (A, B):** statusLine `rate_limits` covers Pro/Max only, so A and B show the cached value with its age.
- **Files panel:** it only sees edits made with Claude's Edit/Write tools, not files changed from Bash.
- **Memory:** each open session is a full `claude.exe` (~200–300 MB), so close the ones you don't need.
- **Typed commands:** `/model`, `/effort` and Shift+Tab go into the TUI input, so they are disabled while a dialog is open.

**Not in v1:**
- several accounts inside one window (use several windows);
- toast notifications;
- toggling plugins or settings from the GUI (we launch CC's own `/plugin` and `/mcp`);
- a chat-style message view;
- Claude Desktop handoff;
- attaching `claude --bg` background sessions;
- an installer or auto-update.
