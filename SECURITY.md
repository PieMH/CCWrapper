# Security policy

## Supported versions

Only the **latest release** of CCWrapper gets security fixes. If you use an older version, update first and check whether the problem is still there.

## Report a vulnerability

Please report privately. Do **not** open a public issue for a security problem.

1. Go to <https://github.com/PieMH/CCWrapper/security>.
2. Choose **Report a vulnerability** (GitHub's private vulnerability reporting).
3. Fill in the form.

If you cannot use GitHub, write to pdondiw@gmail.com with the subject "CCWrapper security".

### What to include

- The version of CCWrapper, and your Windows version.
- What you found and why it matters.
- Steps to reproduce, or a small proof of concept.
- What you expected and what happened.
- Whether the problem is already public.

Please do not include real credentials, tokens or private transcripts. Use made-up data.

## What to expect

CCWrapper is a one-person project. There is no service-level agreement. The publisher will try, on a best-effort basis, to:

- acknowledge your report within about 7 days;
- tell you whether it is accepted, and what happens next;
- fix confirmed problems in a new release, and credit you if you want.

A fix can take longer. Please give a reasonable time before you disclose a problem in public. If you hear nothing, a short reminder on the report is welcome.

## Scope

In scope:

- The code in this repository and the releases built from it.
- Cases where the app does something its documents say it does not do. For example: it types into a permission prompt, reads `.credentials.json`, sends data to a server, or overwrites a Claude Code file without the user's action.
- Problems in the installer, the update check and the signing process described in [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

Out of scope:

- Bugs in Claude Code, Anthropic's services, Windows, the Edge WebView2 Runtime or other third-party software. Report those to their owners. If a third-party problem needs a fix in CCWrapper, report that part here.
- Websites opened in the Browser panel.
- What Claude Code itself does with the permissions you gave it.
- Attacks that need an attacker who already controls your Windows account or your files.
- Scanner output without a demonstrated problem.

See also: [PRIVACY.md](PRIVACY.md), [TERMS.md](TERMS.md).
