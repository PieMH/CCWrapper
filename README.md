# CCWrapper

**A lightweight native Windows GUI around the real Claude Code terminal. One account per window, as many windows as you like.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)
![Tests](https://img.shields.io/badge/tests-188%20passing-brightgreen)

Claude Code in a bare PowerShell window works, but it's clumsy. There's no search, you can't paste screenshots or files with Ctrl+V, you can't see your other sessions, and context, usage, file changes and subagents are hard to keep an eye on. If you also juggle **several Claude Code accounts** (work and personal, for example), it gets worse.

CCWrapper keeps Claude Code exactly as it is and builds a Claude Desktop-style app around it. The chat, permission prompts, slash commands and menus all stay Claude Code's own TUI, so every Claude Code update works the day it ships.

```
┌ CCWrapper │ ● C · Personal Pro  ⇄ Switch │ GitHub: octocat ────────────────────────┐
├────────────────────┬─────────────────────────────────────┬─────────────────────────┤
│ Search sessions... │ [find in chat ____ 3/12 ↑↓] Ctrl+F │ Files │ Agents │ Browser │
│ Settings ▾         │                                     │ ▾ src/                  │
│ Customize          │   the real Claude Code TUI,         │     App.cs     +12 −3   │
│ + New session      │   with your account's own settings  │     New.cs     +40  A   │
│ ────────────────── │                                     │ ▾ assets/               │
│ ● my-api      now  │                                     │     logo.png  +1.2 KB   │
│ ◉ docs-site    2m  │ ┌ compose ────────────────────────┐ │                         │
│ ○ scratch      3d  │ │ multi-line prompt, paste, drop  │ │                         │
│                    │ └───────────────────────── Send ──┘ │                         │
├────────────────────┴─────────────────────────────────────┴─────────────────────────┤
│ opus ▾ │ high ▾ │ auto ▾ │ ctx 202k/1M 20% │ in 1.2M out 48k $16.7 │ 5h 58% wk 12% │
└────────────────────────────────────────────────────────────────────────────────────┘
 ● working   ◉ needs your input   ○ open / idle
```

## Features

**Start screen**
- A "Who's working?" picker shows one card per Claude Code account, with its email, plan, and 5-hour and weekly usage bars.
- Below the cards is a list of recent sessions from every account, each with its account badge. Click one to resume it.

**One account per window**
- Each window runs one account with that account's own settings. To run accounts side by side, open more windows.
- **Switch** in the top bar restarts the window as another account. Its sessions stay resumable.
- `CCWrapper.exe --account b` skips the picker, so you can make one shortcut per account.
- The taskbar badge shows each window's account letter and color.

**Sidebar**
- **Session search:** typing filters by title and project. Enter searches the full text of every transcript of the account.
- **Session list:** every session of the account, including ones started outside the app, newest first, with live status dots.
  - Click a session to switch to it, or to resume it in its original folder.
  - A session that's running somewhere else opens as a read-only live transcript.
- **Settings menu:** opens `settings.json`, your `CLAUDE.md` or the config folder, or runs `/config`, `/permissions`, `/hooks` or `/memory`.
- **Customize page:** lists your plugins, skills, MCP servers and connectors in one place. Changes go through Claude Code's own `/plugin` and `/mcp`.
- **New session:** pick a folder, plus optionally a model, effort, permission mode and name. Unchanged fields keep the account's defaults.

**The terminal, improved**

| Key | What it does |
|---|---|
| Shift+Enter | New line instead of submit |
| Ctrl+V | Screenshot → `[Image #1]` · files, folders and videos copied in Explorer → their paths · text → normal paste |
| Drag & drop | Inserts the dropped files' paths |
| Ctrl+C | Copies when text is selected, otherwise interrupts (Ctrl+Shift+C always copies) |
| Right-click | Copies the selection, or pastes when nothing is selected |
| Ctrl+F | Find in the chat, with highlights and a match count. "Search full transcript" covers the whole session. |
| Links | `localhost` links open in the built-in browser, all others in your default browser |

**Compose box** (optional, under the terminal)
- A real multi-line editor: Enter sends, Shift+Enter adds a line, and Up recalls earlier prompts.
- Pasted screenshots become image files.
- It never types into a permission prompt or a dialog. Those you always answer in the terminal.

**Right panel**
- **Files:** a tree of every file Claude changed this session, with `+/−` lines or the size change for binaries. Click a file to see all of it with the changes highlighted.
- **Agents:** this session's subagents, running or finished, with model, effort, context and tokens. Click one to see its live transcript.
- **Browser:** a built-in WebView2 browser for your dev server.

**Bottom bar**
- Model, effort and permission-mode switches that drive Claude Code's own `/model`, `/effort` and Shift+Tab.
- Meters for context window, session tokens and cost, and 5-hour and weekly plan usage.

## Requirements

- **Windows 10 (1809 or later) or Windows 11.** Developed and tested on Windows 11.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** to build.
- **WebView2 Runtime.** It's already installed on Windows 11; otherwise get it from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).
- **Claude Code, native install**, so that `claude.exe` is on your `PATH`:
  ```powershell
  irm https://claude.ai/install.ps1 | iex
  ```
  The npm package (`claude.cmd`) and launcher shims (Scoop, Chocolatey) aren't supported yet.

## Install

There's no prebuilt release yet, so build it from source:

```powershell
git clone https://github.com/PieMH/CCWrapper.git
cd CCWrapper
dotnet build CCWrapper.sln -c Release
.\src\CCWrapper\bin\Release\net10.0-windows\CCWrapper.exe
```

> [!IMPORTANT]
> Keep CCWrapper in a folder **without spaces** in its path (for example `C:\Tools\CCWrapper`, not `C:\Program Files\...`). The status-line relay is called by path, and a path with spaces breaks it.

Optional: copy the `net10.0-windows` folder wherever you like, and pin `CCWrapper.exe` to the taskbar or Start menu. Make one shortcut per account with `--account <id>` added to the target.

## Accounts

CCWrapper finds accounts by their Claude Code config folders. No setup is needed in the app itself.

| Folder | Account id | How Claude Code is started |
|---|---|---|
| `%USERPROFILE%\.claude` | `a` | `CLAUDE_CONFIG_DIR` unset (the default login) |
| `%USERPROFILE%\.claude-<id>` (must contain `.claude.json`) | `<id>` | `CLAUDE_CONFIG_DIR` set to that folder |

To add a second account, log in once with its own config folder:

```powershell
$env:CLAUDE_CONFIG_DIR = "$HOME\.claude-b"
claude            # then /login with the other account
```

Restart CCWrapper and account **B** shows up in the picker. The folder suffix is the id, so `.claude-work` becomes `--account work`.

## Usage

```powershell
CCWrapper.exe                 # account picker + recent sessions
CCWrapper.exe --account c     # open straight into account C
CCWrapper.exe --smoke         # terminal self-test, exits 0 on success
```

A few tips:
- ● means Claude is working and ◉ means it's waiting for you. When a background session of the window needs you, the taskbar button flashes.
- When a session's process exits, the terminal offers **Resume** or **Close**.
- The model and effort switches send Claude Code's own `/model` and `/effort`, which **save the choice as that account's default**. That's Claude Code's behavior, not the app's.
- Accounts using Claude Code's fullscreen renderer (`"tui": "fullscreen"`) stay fullscreen. Ctrl+F then searches the visible screen, and "Search full transcript" searches the whole session.

## Privacy and safety

CCWrapper is a viewer and a launcher. It doesn't touch your logins.

- **It reads only files Claude Code already writes:** each account's `.claude.json` (email, plan, cached usage), `settings.json`, transcripts, session status files, file-history backups, and plugin and skill lists.
- **It never reads** `.credentials.json`, never calls Anthropic APIs, and never edits your settings.
- **It writes only to** `%LOCALAPPDATA%\CCWrapper`: pasted images, the browser profiles, and the live status data described next.
- **The status line:** if an account has no `statusLine` of its own, each session gets a tiny silent relay (`CCWrapper.StatusRelay.exe`) through `--settings`, which feeds the bottom bar. If you have your own status line, it's left alone.
- **It makes no network calls of its own.** The Browser panel loads only what you open in it.
- Every session is the unmodified `claude.exe`, run under your own login.

## Known limits

- **Undocumented files.** Session status files, transcript records and file-history backups are internal to Claude Code and can change with any update. If they do, the affected field shows "—" instead of the app crashing.
- **Files panel** only sees edits made with Claude's Edit and Write tools, not files changed by shell commands.
- **Weekly usage on Team accounts** comes from Claude Code's cached value (shown with its age), because the live status line reports it for Pro and Max only.
- **Memory:** each open session is a full `claude.exe` (about 200–300 MB), so close the ones you don't need.
- **Antivirus:** the exe is unsigned. If your antivirus flags `CCWrapper.exe`, add an exception for that one file.

## How it was made

CCWrapper was built in about a day, almost entirely by Claude Code agents:

1. **The idea.** It started from one long prompt describing the problem. Claude Code brainstormed it with me: wrapper or chat clone, which stack, which layout, how to respect each account's settings. That produced a detailed design and build plan.
2. **The build.** An orchestrator agent (the agents-team plugin for Claude Code) split the plan into 7 tasks, each owning its own files.
   - Up to 3 worker agents ran in parallel, each in its own git worktree.
   - **Claude Opus** wrote the skeleton and the terminal core (ConPTY ↔ xterm.js bridge).
   - **Claude Sonnet** wrote the data readers, the window and the panels.
   - The orchestrator re-ran build, tests and the smoke test before merging each task.
3. **The review.** An independent QA agent reviewed the whole change over several rounds and found more than 30 issues. The most important fixes keep the app from ever typing into a permission prompt by accident. They went into 5 fix tasks (F1–F5), and the last round passed.
4. **The scars.** An antivirus behavior shield killed the agents' own UI-test scripts (screenshots, synthetic keystrokes, WebView2 remote debugging) halfway through. From then on, verification was unit tests and a headless smoke test only.

Every step is in the history: `git log --first-parent` shows the merge of each task (T1–T7, F1–F5).

**Stack**
- C# / .NET 10 + WPF;
- [xterm.js](https://xtermjs.org/) 6 (the VS Code terminal) in WebView2;
- [Porta.Pty](https://www.nuget.org/packages/Porta.Pty) for ConPTY;
- [DiffPlex](https://github.com/mmanela/diffplex) for diffs;
- xUnit, with 188 tests on synthetic fixtures.

## Development

```powershell
dotnet build CCWrapper.sln
dotnet test
.\src\CCWrapper\bin\Debug\net10.0-windows\CCWrapper.exe --smoke
```

```
src/CCWrapper/             WPF app
  Core/                    accounts, paths, the LiveSession contract
  Data/                    read-only parsers for Claude Code's files (all tolerant of unknown fields)
  Terminal/                ConPTY session, WebView2 + xterm.js host, paste handling
  Views/                   picker, main window, sidebar, panels, bottom bar, compose box
src/CCWrapper.StatusRelay/ silent statusLine relay → %LOCALAPPDATA%\CCWrapper\live
tests/CCWrapper.Tests/     xUnit tests
```

Issues and pull requests are welcome.

## License

[GPL-3.0](LICENSE).

Bundled third-party code: xterm.js and its addons (MIT, license files in `src/CCWrapper/Terminal/wwwroot/vendor`). NuGet dependencies: Porta.Pty (MIT), DiffPlex (Apache-2.0), Microsoft WebView2 SDK.

---

*CCWrapper is an unofficial community project. It isn't affiliated with or endorsed by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic.*
