# CrossWin — autonomous agent instructions

## Trigger
When the user says **Start** or **Continue** (case-insensitive), immediately read this file, PROGRESS.md, ARCHITECTURE.md and .claude/skills/crosswin-development/SKILL.md. Inspect git status and existing progress, then resume the next prioritized actionable unchecked item in PROGRESS.md, using issue #6 as the product north star and #2–#5 as referenced requirements. Do not reset completed work. Do not request another prompt, propose a planning session, or stop after writing documentation. Implement working code.

## Current product direction and existing implementation
CrossWin already has verified WPF/WinForms functionality. Preserve it. Issue #6 coordinates the expanded agent-native web, mobile, desktop and real-device vision; #2–#5 contain detailed implementation requirements. Do not interpret this as permission to restart or replace the existing architecture. Prioritize small end-to-end verified slices, including a fast physical Android device bridge and web inspection, subject to host/device availability. Use a shared, bounded, privacy-aware diagnostics/session interface; favor native UI hierarchy, logs, accessibility and batched interactions over repetitive screenshots, while retaining visual verification where necessary. A real device requires user authorization; iOS signing/platform restrictions and app-store publication are separate concerns.

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
- If interrupted, on next Start or Continue inspect git status, PROGRESS.md and recent commits, reconcile them, then resume without duplicating work.
- Keep install/run instructions accurate and concise. Avoid dependencies on Visual Studio or Windows for the core macOS/Linux path.
- Never silently alter or delete users' application source. Use isolated build/runtime environments where feasible.

## Definition of an initial usable result
A documented command that takes an existing sample repository, identifies support and limitations, launches a genuine interactive application when feasible, and returns actionable diagnostics otherwise. Explicitly report unsupported WPF features rather than simulating success.

## Superpowers and skill routing
- Use the installed **Superpowers** skill suite whenever a relevant skill exists. At session start discover available skills rather than assuming exact names or that a plugin is installed. Follow their instructions while keeping the CrossWin mission and autonomous workflow authoritative.
- For uncertain architecture or meaningful alternatives, use Superpowers brainstorming briefly and autonomously; select a recommendation and implement, without asking routine approval questions or generating sprawling phase plans.
- For any user-facing GUI or website, use an available frontend-design skill to produce distinctive, accessible, responsive design. No GUI is required for the initial CLI vertical slice.
- Use relevant debugging, systematic investigation, code review and verification skills as appropriate. Skill use is a means to ship working software, not an excuse for ceremony.
- If the Superpowers plugin or a named skill is missing, record the missing dependency and continue using the principles in the local skill; do not block progress solely on a plugin.

## Adaptive testing escalation
- Start with essential build/smoke checks, not a broad test suite. **Never claim a fix works solely because code was changed.**
- Track repeated failures by *root symptom*, not number of user messages. On the second failed attempt, stop repeating guesses and gather new evidence. On the third unresolved attempt at the same issue, automatically switch to hands-on reproduction and instrumented debugging before asking the user to test again.
- Select tools for the actual target: Playwright for web UIs; native app automation, screenshots, runtime logs, process tracing and appropriate platform tooling for desktop apps. Playwright alone does not generally automate arbitrary WPF/WinForms windows.
- If the environment cannot reproduce the issue, explain the exact limitation and request the minimum evidence or permission needed; do not falsely declare it fixed.
- Record symptom, attempts, reproduction command, evidence and resolution in PROGRESS.md; add a focused regression test when it prevents a repeat.

## Reusable project bootstrap
- Keep the agent workflow generic where possible: a mission, one live checklist, concise architecture notes, skill routing and evidence-based debugging. For a different project, adapt mission, acceptance criteria, runtime/test tools and technical constraints rather than blindly copying CrossWin-specific instructions.

## Continued work and linked roadmap
On **Start** or **Continue**, inspect git status, PROGRESS.md, ARCHITECTURE.md and issues #2–#6. Issue #6 is the overarching vision; #2–#5 specify features. Preserve all completed WPF/WinForms implementation and checks; do not restart or rebuild the project from scratch. Continue the next feasible unchecked task, using evidence-based verification. Prioritize agent-native web diagnostics, fast authorized Android physical-device testing and existing runtime reliability according to dependencies. Use structured logs, UI/accessibility trees and interaction traces before costly screenshot loops, but retain visual verification for visual bugs. Do not bypass device trust prompts or claim iOS execution or store publishing without verified support.
