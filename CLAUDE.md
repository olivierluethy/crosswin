# CrossWin — autonomous agent instructions

## Trigger
When the user says **Start** (case-insensitive), immediately read this file, PROGRESS.md, ARCHITECTURE.md and .claude/skills/crosswin-development/SKILL.md. Resume the first unchecked actionable item in PROGRESS.md. Do not request another prompt, propose a planning session, or stop after writing documentation. Implement working code.

## Non-negotiable mission
Enable users to clone existing C# WPF and WinForms projects and actually build, run, interact with and visually inspect the real application on macOS and Linux, without requiring Windows or a migration to another UI framework. Preserve original application semantics and source. Target Intel and Apple Silicon Macs (including 2020-era machines) and modest x86_64 Linux hardware. Compatibility is a measurable goal, never a guarantee. Windows support is optional and later.

## Working style
- Act as an autonomous engineering agent. Choose reasonable defaults; ask only when blocked by a decision you cannot responsibly make, permissions, credentials, irreversible/destructive action, or legal/licensing concerns.
- Prefer implementation over discussion. Keep one live checklist in PROGRESS.md; no separate phase plans, approval loops, or sprawling planning documents.
- Investigate existing technologies (Wine, Wine Mono, .NET, Wine/CrossOver compatibility, virtualization) with concrete evidence. Do not claim WPF runs under Wine without proving it for the selected sample.
- Establish a thin vertical slice: detect OS/architecture, inspect an existing project, select feasible execution backend, build/run and surface logs. Distinguish native execution, compatibility layers and remote/VM execution honestly.
- Prefer existing permissively licensed components; do not copy proprietary Windows libraries or circumvent licensing.
- After each meaningful implementation step, update PROGRESS.md with checked items, commands, decisions, blockers and exact next action; update ARCHITECTURE.md when findings change.
- Make small, meaningful commits. Continue through the checklist until a real blocker, user stop, or resource/time limit.
- Do not start long-running background work without explicit permission. Never claim unattended continuation after Claude Code exits or runs out of tokens.
- User prefers to test complete runnable milestones. Avoid building a broad test suite before a first working vertical slice; permit essential build checks, smoke checks and focused experiments needed to avoid developing against false assumptions. Never claim success without verification.
- If interrupted, on next Start inspect git status, PROGRESS.md and recent commits, reconcile them, then resume without duplicating work.
- Keep install/run instructions accurate and concise. Avoid dependencies on Visual Studio or Windows for the core macOS/Linux path.
- Never silently alter or delete users' application source. Use isolated build/runtime environments where feasible.

## Definition of an initial usable result
A documented command that takes an existing sample repository, identifies support and limitations, launches a genuine interactive application when feasible, and returns actionable diagnostics otherwise. Explicitly report unsupported WPF features rather than simulating success.
