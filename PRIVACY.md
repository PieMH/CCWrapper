# Privacy policy

**CCWrapper** ("the app") is an unofficial Windows GUI around Anthropic's Claude Code.
This policy is effective from 17 October 2026.

Publisher: Pietro Dondi, an individual developer resident in Spain (GitHub: [PieMH](https://github.com/PieMH)).
Contact: pdondiw@gmail.com

## In short

- The publisher receives no data from the app. There is no telemetry, no analytics, no user account and no server of the publisher.
- The app keeps its working files on your own computer.
- The app talks to the network in only a few cases. Each is listed below, with who receives the data.

## Who is responsible for what

The publisher does not receive, store or see your personal data through the app: the app has no server, no account and no telemetry of the publisher. So the publisher does not process personal data from the use of the app, and acts as neither a controller nor a processor under the GDPR (Regulation (EU) 2016/679) or the Spanish LOPDGDD (Organic Law 3/2018). The publisher sees only the aggregate download counts that GitHub shows to the owner of a repository.

The services the app connects to are run by other parties, who decide for themselves what they do with the data they receive. Each is an independent controller under its own privacy terms, and may process data outside the EEA:

- GitHub (downloads and update check).
- Anthropic (Claude Code, your login and your usage).
- Microsoft (the Edge WebView2 Runtime).
- The websites and MCP servers you choose to open or connect to.

If you write to the publisher (see Contact), the publisher handles your message and email address only to answer you, and deletes them when they are no longer needed. The legal basis is the publisher's legitimate interest in answering you (art. 6.1.f GDPR). They are not shared with anyone, except the email provider that carries them. Messages on GitHub (issues, pull requests) are public and stored by GitHub: do not put personal data in them.

## What leaves your computer

### 1. GitHub: download and update check

- You download the installer from GitHub Releases. GitHub sees your IP address and the usual request data. GitHub's [privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) applies.
- An installed copy can check GitHub Releases for a newer version. It does so only if you say yes: the app asks you the first time it runs, before any request, and you can turn the check on or off in **About** at any time. The choice is saved as `updateCheck` in `%LOCALAPPDATA%\CCWrapper\app.json`. Your consent is the legal basis (art. 6.1.a GDPR; art. 5(3) of the ePrivacy Directive, as Spanish law applies it). The switch withdraws it.
- With the check on, the app checks when it opens, downloads a newer version if there is one, and installs it when you close the app or choose to restart. Turning the check off drops a downloaded update that is still waiting.
- A check is a normal web request. GitHub sees your IP address and the usual request data, such as a user agent, which can include the app version.
- Only installed copies check. A copy built from source does not.
- The app sends no identifier, no account data and no files with the check.

### 2. Anthropic, through Claude Code

- Every session in the app is the unmodified `claude.exe` that you installed. It runs under your own login and under your own agreement with Anthropic. What it sends to Anthropic is covered by Anthropic's terms and privacy policy, not by this one.
- The app never reads `.credentials.json`. The app never calls Anthropic's APIs itself.
- To show plan usage, the app runs a hidden `claude "/usage"` through Claude Code. Claude Code contacts Anthropic as it does for any `/usage`, and saves the result in `.claude.json`.
- The Plugins & connectors window runs Claude Code's own `claude plugin` and `claude mcp` commands, only after you click. Those commands may contact plugin marketplaces and servers.

### 3. Websites you open

- The Browser panel is a WebView2 browser. It loads only what you open in it, or what Claude opens in it (next point). Those sites see what any website sees, and may set cookies.
- Claude can use the Browser panel: each session gets it as a local MCP server (`ccwrapper-browser`), unless you turn it off in Settings → CCWrapper (`browserTool` in `app.json`). CCWrapper itself sends nothing new. The pages load only when Claude or you open them, in the panel's profile for the account, which is stored on your computer in `%LOCALAPPDATA%\CCWrapper\WebView2\browser-<letter>`, so those sites see your logins there, and what Claude runs in a page (the `evaluate` and `devtools` tools) acts with them. `devtools` refuses cookie access, downloads, local files, all simulated input (keyboard, mouse, touch and drag, so it cannot paste your clipboard), the page history and scripts that persist across pages. The panel has tabs, all in that one profile; a pop-up opened by a click opens a new tab (at most 20), one a page opens on its own is dropped (except in the few seconds after a click, Claude's clicks included, as in any browser), and a page's `window.close()` closes only a pop-up tab. Claude opens only `http`, `https`, `blob`, `about:blank` and `data` addresses, not local files, and a pop-up to any other address is dropped. Its tools act on the tab in front, can list, open, switch and close tabs (a tab that is not a web page is listed without title or address), and refuse to work on a page that is not a web address (for example a `file:` page you opened in the address bar yourself). All of this holds in every tab. The tool talks to its CCWrapper window through a named pipe on your own computer that only your Windows user can open. What Claude Code does with a page's text, screenshots or script results is covered in section 2, and Claude Code asks your permission per tool.
- A link in the terminal opens in the Browser panel if it points to your own computer (`localhost`), and in your default browser otherwise. Only `http` and `https` links open.
- Each account has its own browser profile, stored on your computer (see below).

### 4. Microsoft Edge WebView2 Runtime

The app shows its terminal and browser with the Microsoft Edge WebView2 Runtime. Microsoft's runtime may send diagnostic data to Microsoft, depending on your Windows privacy settings. The publisher does not control this.

If the runtime is missing on your PC, the installer (`Setup.exe`) downloads it from Microsoft and installs it. This happens during installation, before the app asks about update checks, and the update-check switch does not cover it. Microsoft sees your IP address and the usual request data, and Microsoft's [privacy statement](https://privacy.microsoft.com/privacystatement) applies. If the runtime is already there, nothing is downloaded. Windows 11 already includes the runtime.

### 5. MCP servers

**Check health** in Plugins & connectors runs `claude mcp list`. It connects to each of your MCP servers, and starts the local ones, to see if they answer. The servers see that connection. You choose the servers, and Claude Code does the connecting.

### What the app does not do

- It does not send crash reports.
- It does not collect usage statistics.
- It does not use advertising or tracking services.
- It shows your sessions and searches them on your computer only. It sends them nowhere.

## What is stored on your computer

### The app's own folders

| Where | What |
|---|---|
| `%LOCALAPPDATA%\PieMH.CCWrapper` | The installed program. |
| `%LOCALAPPDATA%\CCWrapper\paste` | Images you pasted into the terminal. |
| `%LOCALAPPDATA%\CCWrapper\WebView2` | Browser profiles of the terminal and of the Browser panel, one per account: cookies, cache, site data. |
| `%LOCALAPPDATA%\CCWrapper\backups` | Earlier versions of files you saved from the app. |
| `%LOCALAPPDATA%\CCWrapper\live` | Live status data (model, context, cost, usage) from the status-line relay, one small file per tab. |
| `%LOCALAPPDATA%\CCWrapper\drafts-<letter>.json` | The unsent text of each session's compose box, one file per account: session id, text and cursor position, at most 100 drafts. Written only when the app exits, when it restarts to update, and whenever the app loses focus while an update is ready to install (so a restart started from another window cannot lose them), and read when a session is reopened. Not encrypted. Not removed when you delete a session. If two windows of one account are open, the one that exits last wins. |
| `%LOCALAPPDATA%\CCWrapper\app.json` | The app's own settings: `updateCheck`, `browserTool` (whether Claude may use the Browser panel) and the bottom bar's `compact` hint (on/off, threshold, prompt). |

These files stay on your computer. The publisher cannot see them.

### Claude Code's files

- The app **reads** files that Claude Code already writes: each account's `.claude.json` (email, plan, cached usage), `settings.json`, transcripts, session status files, file-history backups, and plugin and skill lists. The usage popup (click the usage meters) adds up token counts from the transcripts, and the `/` list in the compose box reads the installed plugins' commands and skills. Both stay on your computer. It also asks `git` (and `gh`) about the folder you work in: branch, upstream, remote, your git user name and email, and your GitHub account name. It shows them in the top bar and sends them nowhere.
- The app **writes** to Claude Code's files only when you act:
  - **Save** in the Settings window writes the `settings.json` and `CLAUDE.md` files you changed.
  - A server's folder switch in Plugins & connectors changes the list of turned-off servers for that folder in `.claude.json`.
  - **Delete** on a session's right-click menu, after you confirm, removes that session's transcript (`<id>.jsonl`) and its `<id>` folder for good. There is no backup, and a session open anywhere is refused.
- Before each Save or switch write, the app keeps the old version in `%LOCALAPPDATA%\CCWrapper\backups`. It does not overwrite a file that changed on disk since it read it.

## Delete your data

1. Uninstall from **Windows Settings > Apps > Installed apps**. This removes the program.
2. Uninstalling does **not** remove `%LOCALAPPDATA%\CCWrapper`. To delete it, close the app, type `%LOCALAPPDATA%\CCWrapper` in File Explorer's address bar, and delete the folder. Browser cookies, pasted images, drafts and backups go with it.
3. Claude Code's own folders (for example `%USERPROFILE%\.claude`) belong to Claude Code. The app does not remove them.

## Children

The app is not directed at children.

## Your rights and complaints

Because the publisher holds no personal data from the app, there is normally nothing to access, correct or delete. If you think the publisher holds data about you, write to pdondiw@gmail.com. You have the rights of arts. 15 to 22 GDPR (access, correction, erasure, restriction, portability, objection), and the publisher will answer within one month.

You may complain to the Spanish data protection authority, the Agencia Española de Protección de Datos (AEPD): <https://www.aepd.es>. You may also complain to the authority of your own EU country.

## Changes

If the app starts to send other data, this policy changes first, and the release notes say so. The date at the top is the date of the current version. Earlier versions are in the repository history.

## Contact

Pietro Dondi - pdondiw@gmail.com - GitHub: [PieMH](https://github.com/PieMH)

See also: [TERMS.md](TERMS.md), [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md), [SECURITY.md](SECURITY.md).
