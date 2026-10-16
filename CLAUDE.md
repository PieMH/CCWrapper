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
- **Live checks against real accounts:** scratch sessions only, and never a prompt that spends the user's plan usage.
- **New non-trivial logic leaves a test.** `ThemeLoadTests` builds every view with `App.xaml` loaded, so a missing theme
  key fails the tests instead of the app.

## Layout

| Folder | What |
|---|---|
| `src/CCWrapper/Core` | Accounts and paths, `AppState`, the `LiveSession` contract the views bind to |
| `src/CCWrapper/Data` | Read-only parsers for Claude Code's files (registry, transcripts, file history, `.claude.json`) |
| `src/CCWrapper/Terminal` | ConPTY (`PtySession`), WebView2 + xterm.js (`TerminalHost`, `wwwroot/`), launch, paste, `UsageProbe`, `ProcessTree` |
| `src/CCWrapper/Views` | WPF windows and controls; pure logic lives in static helpers (`ShellModel`, `BarFormat`, `TuiInput`) |
| `src/CCWrapper/Theme.xaml` | Every color and custom style; `App.xaml` recolors Fluent's own brushes |
| `src/CCWrapper.StatusRelay` | The silent statusLine relay |
| `tests/CCWrapper.Tests` | xUnit, synthetic fixtures only (never real transcripts) |

## Rules, and why

- **Read-only over Claude Code.** Read only files Claude Code already writes. Never read `.credentials.json`, never call
  Anthropic APIs, never edit the user's settings. The app only launches the unmodified `claude.exe`, which keeps it
  inside the usage policy.
- **Undocumented formats change.** The session registry (`sessions\*.json`), transcript records, file history and
  `cachedUsageUtilization` are internal and change with Claude Code releases. Parse them only in `Data/`, treat every
  field as optional, and show "—" when one is missing.
- **Never type into a permission prompt.** Anything that sends text or keys to the TUI goes through `TuiInput`. It
  re-reads the registry right before sending and fails closed.
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
- Keep README's feature list and test badge in step with behavior changes.
