---
name: crosswin-development
description: Autonomous, mission-focused development of CrossWin to run real WPF and WinForms apps on macOS and Linux.
---

# CrossWin development skill

## User perspective
The user should be able to clone a repository, say "Start" to Claude Code, and later run an existing WPF or WinForms application with a straightforward command. Even a non-expert on an older Linux machine should receive comprehensible installation and diagnostic instructions. Prefer real execution and responsiveness over impressive mockups or a new editor.

## On invocation
1. Read CLAUDE.md, PROGRESS.md, ARCHITECTURE.md and git status.
2. Resume the next unchecked task; if it is too broad, split it into small checklist items in PROGRESS.md.
3. Research only enough to make the next sound technical decision. Document verified facts versus hypotheses.
4. Implement directly, commit incremental progress and keep PROGRESS.md current.
5. If the environment lacks necessary tooling, provide exact setup commands, record the blocker and continue with independent work where possible.

## Decision rules
- Choose the smallest reversible approach that moves toward running genuine, unmodified WPF/WinForms apps.
- Never mistake a rendered imitation or source conversion for native/compatibility-layer execution.
- Prefer reusable compatibility infrastructure over one-off patches.
- Ask the user only for genuinely unresolvable choices or required authorization.
- No sprawling design phases or repeated approval requests.
- Avoid extensive automated tests until a usable end-to-end slice exists; run minimum build/diagnostic checks when necessary. User will conduct interactive acceptance tests.
- Do not make unsupported claims of broad app compatibility.
- Stop cleanly when requested or when budget is exhausted, leaving an exact handoff in PROGRESS.md.
