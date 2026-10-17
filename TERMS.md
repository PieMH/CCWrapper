# Terms of use

**CCWrapper** ("the app") is a Windows GUI around Anthropic's Claude Code.
These terms are effective from 17 October 2026 and apply when you install or use the app. The app is free of charge.

Publisher: [LEGAL NAME], an individual developer resident in Spain (GitHub: [PieMH](https://github.com/PieMH)).
Contact: [CONTACT EMAIL]

## 1. The licence is the GPL

The app is free software under the **GNU General Public License, version 3** (GPLv3). Copyright (C) 2026 [LEGAL NAME]. The full text is in [LICENSE](LICENSE). You may run, study, change and share the app under that licence. The source code of each release is the tag of the same name at <https://github.com/PieMH/CCWrapper>.

These terms do not restrict any right the GPLv3 gives you. If these terms and the GPLv3 conflict, the GPLv3 wins. These terms only cover what the GPLv3 leaves open: how the app relates to other services, what it does on your computer, and what the publisher is and is not responsible for.

Third-party components keep their own licences. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## 2. Unofficial, and not affiliated with Anthropic

- CCWrapper is an independent, unofficial community project.
- It is **not affiliated with, endorsed by or sponsored by Anthropic**.
- "Claude" and "Claude Code" are trademarks of Anthropic, PBC. They are used here only to say what the app works with. The app does not use Anthropic's logos.

## 3. You need Claude Code, and you follow Anthropic's terms

- The app launches the unmodified `claude.exe` that you installed. It does not replace, patch or bypass Claude Code.
- Claude Code, your login and your plan are between you and Anthropic. You must follow Anthropic's terms and usage policies. The app does not change them and does not give you any right under them.
- The app never reads `.credentials.json` and never calls Anthropic's APIs itself.
- The app also runs hidden `claude` commands for you: `claude "/usage"` to show your plan usage, and `claude plugin` and `claude mcp` after your click. Anthropic decides what its terms allow, and they can change. Read them.
- The publisher is not responsible for what you or Claude Code do in a session, for Anthropic's decisions about your account, or for changes in Claude Code that stop the app from working.

## 4. What the app changes on your computer

- The app mostly reads files that Claude Code writes.
- It changes Claude Code's files only when you act, and keeps a backup of the old version first:
  - **Save** in the Settings window writes the `settings.json` and `CLAUDE.md` files you changed.
  - A server's folder switch in Plugins & connectors changes `.claude.json` for that folder.
  - Plugin and MCP changes are made by Claude Code's own `claude plugin` and `claude mcp` commands, after your click.
- It ends a running `claude` process only after you confirm ("Resume here").
- It writes its own files in `%LOCALAPPDATA%\CCWrapper`. See the [privacy policy](PRIVACY.md).
- Claude Code can run commands and edit files on your computer. That is Claude Code's behaviour. You are responsible for the permissions you give it.

## 5. Updates

- An installed copy can check GitHub for a newer version, but only if you say yes when the app asks you the first time it runs.
- With the check on, the app downloads a newer version and installs it when you close the app or choose to restart.
- You can turn the check off in **About** at any time. You can still update by hand by downloading the new installer from GitHub Releases.
- What the check sends is described in the [privacy policy](PRIVACY.md).

## 6. No warranty

The app is provided **"as is"**, free of charge, **without warranty of any kind**, as set out in sections 15 and 16 of the GPLv3. Use it at your own risk. The publisher does not promise that it works without errors, fits your purpose, or keeps working with every version of Claude Code or Windows.

### What this does not take away

Nothing in these terms excludes or limits liability that the law does not allow to be excluded or limited. In particular, the publisher does not exclude liability:

- for intent (dolo) or gross negligence (culpa grave) (Spanish Civil Code, art. 1102 and the case law on it);
- for death or personal injury;
- under mandatory product-liability rules;
- where you are a consumer, for any right that the Spanish Consumer Law (Real Decreto Legislativo 1/2007, TRLGDCU) or other mandatory law of your country of residence gives you and that cannot be waived.

Subject to that, and to the extent the law allows, the publisher is not liable for loss of data, lost profits or indirect damage that comes from using or not being able to use the app. Keep backups of work that matters.

## 7. Your use, and the name

You are responsible for following the law and Anthropic's terms when you use the app. This is not a condition of the GPLv3 licence, which puts no limit on what you may do with the code.

The name "CCWrapper" is not licensed for versions that you change (GPLv3, section 7(e)). Call a modified version something else, and do not suggest that Anthropic or the publisher endorses it.

## 8. Governing law and courts

- These terms are governed by Spanish law.
- If you are a consumer, you keep the protection of the mandatory rules of the law of your country of habitual residence, you may bring a claim before the courts of that country, and a claim against you will be brought only there.
- Otherwise, the courts of the publisher's place of residence in Spain have jurisdiction, unless the law requires another court.

## 9. Changes to these terms

The publisher may change these terms, for example when the law, Anthropic's rules or the app's behaviour change. A new version is published in this repository with a new effective date and applies to releases published after it. A version you already have stays under the terms it came with. Earlier versions stay in the repository history. A change never reduces the rights the GPLv3 gives you.

## 10. Contact

[LEGAL NAME] - [CONTACT EMAIL] - GitHub: [PieMH](https://github.com/PieMH)

See also: [PRIVACY.md](PRIVACY.md), [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md), [SECURITY.md](SECURITY.md).
