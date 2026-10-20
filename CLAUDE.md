# CCWrapper

A native Windows GUI (C# / .NET 10, WPF) around the real Claude Code TUI: sessions as tabs, one account per window. What it
does for users is in [README.md](README.md); this file is how to work on it.

## Build, test, verify

```powershell
dotnet build CCWrapper.sln
dotnet test
.\src\CCWrapper\bin\Debug\net10.0-windows\CCWrapper.exe --smoke   # terminal self-test: exit 0
```

- **A running CCWrapper locks `bin\Debug`.** Build and test with `-c Release` instead. Never kill the user's CCWrapper:
  they may be talking to you through it.
- **Verification is tests, `--smoke` and targeted live checks.** No UI automation: no screenshots, synthetic keystrokes or
  WebView2 remote debugging. Antivirus behavior shields flag those and quarantine the build.
- **Live checks against real accounts:** scratch sessions only, and never a prompt that spends the user's plan usage. A check
  that needs the real account (rare) deletes its own sessions afterwards.
- **A scratch `claude` never gets the user's config folder.** Start it with `CLAUDE_CONFIG_DIR` = a fresh temp dir,
  `ANTHROPIC_BASE_URL=http://127.0.0.1:9`, a dummy `ANTHROPIC_API_KEY` (answer Yes at "use this API key?") and
  `ClaudeLaunch.ParentSessionVars` stripped; delete the temp dir and its cwd when the check ends. Otherwise its sessions
  pile up in the session list and `.claude.json`.
- **New non-trivial logic leaves a test.** `ThemeLoadTests` builds every view with `App.xaml` loaded, so a missing theme
  key fails the tests instead of the app.

## Layout

| Folder | What |
|---|---|
| `src/CCWrapper` (root) | `Program.cs`: the Velopack hook runs before WPF starts |
| `src/CCWrapper/Core` | Accounts and paths, `AppState`, the `LiveSession` contract the views bind to; `Housekeeping` (start-up deletes by age: pasted images, orphaned status line files); `DraftStore` (compose-box drafts, `drafts-<letter>.json`, saved on exit, before an update restart and when the app is deactivated with an update it knows of; another window's update restart can still lose the drafts of a window that has not seen the update); `AppUpdate` (auto-update), `AppPrefs` (`updateCheck`, `browserTool`) and `CompactPrefs` (`app.json`: each rewrites only its own key) |
| `src/CCWrapper/Data` | Read-only parsers for Claude Code's files (registry, transcripts, file history, `.claude.json`) and for what `claude plugin`/`mcp` print; `SlashCommands` (the `/` list, with enabled plugins' commands and skills), `UsageStats` (the usage popup's token counts); `SettingsData`, the Settings window's load-edit-save of `settings.json` and `CLAUDE.md`; `ClaudeJson`, locked updates of `.claude.json` |
| `src/CCWrapper/Terminal` | ConPTY (`PtySession`), WebView2 + xterm.js (`TerminalHost`, `wwwroot/`), launch, paste, `UsageProbe`, `ProcessTree`, `ClaudeCli` (hidden `claude plugin`/`mcp` runs), `BrowserMcp`/`BrowserPipe` (the Browser tool: MCP handler and the window's named pipe) |
| `src/CCWrapper/Views` | WPF windows and controls; pure logic lives in static helpers (`ShellModel`, `BarFormat`, `TuiInput`) |
| `src/CCWrapper/Theme.xaml` | Every color and custom style; `App.xaml` recolors Fluent's own brushes |
| `src/CCWrapper.StatusRelay` | The silent relay, two jobs: the statusLine command, and with `--browser <pipe>` the stdio MCP server of the Browser tool (native AOT in releases; only the statusLine command is launched by 8.3 short path, as it is a shell command string, so install paths with spaces work; the `--browser` MCP command is the full path, spawned without a shell) |
| `scripts/`, `.github/workflows` | `pack.ps1` builds the Velopack release; `ci.yml` tests, `release.yml` publishes |
| Root `*.md`, `THIRD-PARTY-NOTICES.txt` | PRIVACY, TERMS, CODE_SIGNING_POLICY, SECURITY: the policies the About window and SignPath point to |
| `tests/CCWrapper.Tests` | xUnit, synthetic fixtures only (never real transcripts) |

## Release

- **Tag `vX.Y.Z` → `release.yml` → a draft GitHub release; a human publishes it.** Setup.exe, Portable.zip, nupkg, `releases.win.json`,
  `SHA256SUMS.txt` and a build attestation. SignPath steps run only once the `SIGNPATH_API_TOKEN` secret exists.
- **`scripts\pack.ps1 -Version X` locally needs the MSVC build tools** (`vcvars64` and `vswhere` on `PATH`) for the AOT relay.
- **Velopack's apply force-stops every process in the install folder,** so `AppUpdate` hands off on exit only when it is the
  last CCWrapper of that install. Each account window is its own process.
- **No request before the first-run yes.** `AppUpdate` checks only in an installed copy and only when `AppPrefs` says so;
  a dev build and "no" make no network call (PRIVACY.md says so too, keep them in step).

## Rules, and why

- **Read-only over Claude Code, but for two windows and one delete.** Read only files Claude Code already writes. Never read
  `.credentials.json`, never call Anthropic APIs. The app only launches the unmodified `claude.exe`, which keeps it
  inside the usage policy. One deletion besides: the right-click Delete of a session removes its transcript
  `<id>.jsonl` and its `<id>\` folder (`SessionCatalog.Delete`), only after a confirm and never while a claude process
  or tab has the session; it refuses any path outside the account's `projects\<dir>\<id>.jsonl`.
- **The browser tool drives only the Browser panel's WebView2** (its own user-data folder) through in-process CDP, one WebView2 per tab on the panel's one profile. Never the
  terminal's WebView2, never a remote debugging port. The pipe is `CurrentUserOnly`, and the only launch flags it adds are
  `--mcp-config` and `--append-system-prompt`. `browserTool` off: new tabs get no flags and `BrowserMcp` refuses calls from open ones.
  Hardened: each tools/call has a 60 s limit, `navigate`, `new_tab` and `window.open` (which opens a tab only when user-initiated, i.e. within Chromium's few seconds of activation after a click, a CDP click from `click` included, max 20, else dropped; `window.close()` closes only such pop-up tabs, and `DropControlsCloseHandler` stops WPF's WebView2 closing the whole window on it) pass only http, https, blob,
  about:blank and data (`BrowserPanel.SchemeAllowed`), every page tool but `navigate` refuses while the front tab shows any other page (`OnUi` captures the front tab once per call and runs both checks, before and after the body, on that tab, whatever got it
  there: history, the address bar; `tabs` lists such a tab as "(not a web page)", without title or URL), and `devtools` is an allow-list by domain (`BrowserMcp.DevToolsRefusal`) that denies cookies,
  downloads, local files, new-document scripts, history navigation and the whole Input domain (keyboard, mouse, touch, drag: it would paste the clipboard). `evaluate` runs with the page's own access.
  (PRIVACY.md and the README say so too, keep them in step).
- **Changes go through Claude Code's own CLI.** Plugins & connectors runs `claude plugin …` and `claude mcp …` hidden
  (`ClaudeCli`), only after the user's click, with a confirm before anything is removed. These commands never call the
  model.
- **The Settings window writes `settings.json` (user, project, local) and the `CLAUDE.md` files,** only on Save,
  through `SettingsFile`/`TextFile` (`Data/SettingsData`). A save writes the whole object back so unknown keys survive,
  refuses a file that changed on disk since it was read, and backs up the old one. `/config`'s switches belong there
  too: Claude Code reads them from `settings.json` first and from `.claude.json` only as a leftover of older versions.
- **`.claude.json` is written only through `ClaudeJson.Update`,** for the per-folder server switch of Plugins &
  connectors (`disabledMcpServers`, as `/mcp disable`). Every running `claude` rewrites this file, each time under a
  `.claude.json.lock` folder and on a fresh read: take that lock, re-read, change, write back. Never write a copy read
  earlier.
- **Undocumented formats change.** The session registry (`sessions\*.json`), transcript records, file history and
  `cachedUsageUtilization` are internal and change with Claude Code releases. Parse them only in `Data/`, treat every
  field as optional, and show "—" when one is missing.
- **Never type into a permission prompt.** Anything that sends text or keys to the TUI goes through `TuiInput`. It
  re-reads the registry right before sending and fails closed. A slash command goes in as pastes
  of at most 500 characters and one line break each: Claude Code folds a paste over 800 characters or of 4 lines or more into `[Pasted text]` instead of running it.
- **WPF cannot draw over WebView2.** Never overlay WPF on the terminal. Collapse it and show a WPF panel in its cell, or
  put the UI in the terminal page (`wwwroot`).
- **Child `claude` processes must not inherit a parent Claude Code session.** `ClaudeLaunch` strips its environment
  variables (`ParentSessionVars`). Do the same when a test starts `claude` from inside a Claude Code session: otherwise
  that `claude` never registers.
- **Ending a user's `claude` needs their yes.** Only "Resume here" does it, after a confirm (`ProcessTree.End`).
- **Hidden `/usage` checks are a side effect to keep small.** Each one adds a line to that project's prompt history, so
  respect `UsageProbe`'s minimum gap.
- **Colors only through `Theme.xaml` keys,** and only the Segoe Fluent Icons font for icons. Keep it light: hover and
  pressed states, no heavy animation, and every animation respects `SystemParameters.ClientAreaAnimation`.

## Workflow

- `main` is protected: work on a branch and open a pull request. Never push to `main` or force-push.
- Commit messages: no `Co-Authored-By` or other AI attribution trailers.
- Keep README's feature list and test badge in step with behavior changes, and the policy files in step with what the app sends.
