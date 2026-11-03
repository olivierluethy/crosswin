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

## Product experience and performance principles
- The inspiration is the frictionless, approachable experience the user associates with Grok Bot; do not copy another product's trademark, distinctive visual identity or protected assets. Build an original CrossWin design system: minimal, confident, accessible, responsive, fast, and consistent across browser and desktop using shared components where feasible.
- Design the user journey around the **next obvious action**: Open project → Detect project → Choose default target → Run/Preview → Inspect → Connect real device when needed. One prominent contextual action, clear progress and understandable blockers. Do not overwhelm new users with framework jargon or five devices by default.
- Support both headless agent operation and an optional human-visible preview attached to the same session, with quick device selection, orientation and multiple isolated targets. Opening the UI must not restart a running session.
- Use available Superpowers brainstorming and frontend-design skills for UX decisions, then implement and visually verify on supported real backends. Prefer one shared design system for web and desktop; test keyboard navigation, screen readers, small windows and loading/error/empty states.
- Make performance observable: time to first preview, incremental update latency, diagnostics latency, CPU/RAM, idle resource use. Use structured diagnostics before screenshot loops, but retain Playwright/CDP or other existing tools when faster and more reliable; benchmark before replacing tools with custom inventions.
- Plan a GitHub Pages documentation/marketing/demo site using the same design system, with an honest interactive demo or prerecorded preview clearly labeled. GitHub Pages cannot itself host arbitrary native emulators or local application runtimes; do not suggest it can.
- Never promise all frameworks, all devices, instant automatic fixes or flawless first-run behavior before proving them. Preserve verified functionality, implement vertical slices and clearly label unavailable features.

## Compatibility with reverse-engineering and UI-reconstruction workflows
CrossWin is **not** a reverse-engineering or application-cloning product. It should be a neutral, fast execution-and-verification layer usable by external agents and tools, including workflows inspired by Reverse Engineer Anything (REA/RAE), when users have the right to inspect and reproduce the target behavior. Do not assume a particular third-party API exists without inspecting its actual repository/documentation.
- Provide an agent-agnostic, documented session API/MCP adapter to open a user's implementation, drive it, inspect UI hierarchy/accessibility tree, read errors, capture snapshots and replay interactions.
- Support optional reference-vs-implementation comparison using user-supplied, authorized reference captures, interaction traces or a running reference application where legally accessible. Compare geometry, navigation, state transitions, responsiveness and runtime errors; distinguish visual similarity from behavioral equivalence.
- Return structured, compact, actionable diffs and reproducible traces to any agent; do not make users repeatedly describe mismatches or manually write Playwright tests.
- Keep source application inspection and execution separate from CrossWin's core mission; do not build license bypass, DRM circumvention, credential extraction or proprietary asset copying.
- Prioritize one verified end-to-end workflow and measure iterations/time-to-match before expanding integrations.

## Execution discipline: maximize verified progress per unit time
- Begin each Continue session with a **brief** reality check: git status, current runtime, PROGRESS.md, relevant issue links, one concrete user-visible acceptance scenario. Do not rewrite the plan or restart completed work.
- Choose the smallest **end-to-end vertical slice** that reduces the biggest real bottleneck. Define one executable acceptance check and a time/latency baseline before editing; ship, test, measure and commit. Do not build speculative frameworks, generalized abstractions or broad multi-platform scaffolding before one real target works.
- Use a **risk-first spike** for uncertain feasibility (e.g., Android ADB device interaction, Flutter startup, web instrumentation, Wine rendering, iOS restrictions). Limit investigation, capture evidence, decide go/no-go/fallback; avoid spending hours polishing an impossible path.
- Make progress **continuously demonstrable**: each milestone has a working command/demo, before/after metrics where relevant, a commit and exact next step. Track verified / partially verified / blocked separately. No green status based on compilation alone.
- Treat human time and AI token consumption as performance budgets. Prefer compact event deltas, UI/accessibility trees, batched device actions and focused screenshots over repeated full-image loops. Keep diagnostic overhead and test flakiness measurable; avoid optimizing against a single lucky run.
- Avoid premature platform breadth: a complete reliable Flutter/Android or web workflow is more valuable than superficial support for ten runtimes. Preserve current WPF/WinForms paths and prevent regressions with focused smoke tests.
- Automate reversible actions; ask for explicit permission when required for device trust, credentials, paid services, publishing, destructive operations or sensitive data. Surface blockers with the shortest actionable user instruction and keep independent work moving.
- End a session with a **handoff contract** in PROGRESS.md: what works, evidence, known limitations, next command, next unchecked issue-linked task. No ambiguous “almost done” claims.

## Non-negotiable audience and product acceptance
CrossWin is a **standalone product for everyone**, explicitly including non-developers who have never used Claude Code, terminals, Flutter, ADB, Playwright or MCP. This is a core requirement, not an optional future audience or a question to reopen. Take inspiration from the accessibility and ease of use the user associates with Grok Bot, while creating CrossWin's own identity.
- First-run success must be possible through the GUI without an AI agent, terminal, hand-written tests or knowledge of developer terminology. Detect and explain supported project types; provide an obvious next action and plain-language error recovery.
- The engine and UI must function independently of Claude Code and other AI providers. AI agents are optional clients of the same underlying capabilities, never a prerequisite.
- Advanced developer controls may be progressively disclosed, but must not dominate the beginner experience. Validate onboarding with a novice usability scenario and measure time to first successful preview.
- Do not claim that *any* arbitrary application runs without prerequisites: identify unsupported projects/devices and guide users through unavoidable OS permissions or platform SDK constraints.
- Evaluate design, architecture, roadmap and acceptance decisions against this audience requirement before marking features complete.

## Continuous optimization after a working baseline
A passing end-to-end demo is the **start of measured optimization**, not the end of the task. Once the supported user journey works reliably, autonomously profile and improve the critical path (project detection → environment readiness → launch/preview → interaction → diagnostic feedback → fix verification), without sacrificing correctness or novice usability.
- Establish reproducible baselines with multiple runs and record median/p95 latency, CPU/memory, reliability/flakiness and time-to-verified-fix; identify the largest bottleneck from actual traces rather than intuition.
- Inventory current dependencies/tools (emulators, Playwright/CDP, ADB/UIAutomator, capture, process orchestration). Research supported faster paths and configure or tune existing tools before building replacements. Evaluate alternatives through controlled A/B benchmarks on representative projects/devices; prefer lower total workflow time over a fast microbenchmark.
- Optimize in bounded increments: profile → hypothesis → smallest change → repeated benchmark → regression/UX/security check → retain or revert. Document evidence and tradeoffs, including cold vs warm start and physical vs virtual devices.
- Avoid unbounded autonomous refactoring, bespoke forks or replacing mature tools solely for speed claims. Respect licenses and upstream maintenance. Never modify external dependencies or introduce brittle patches without a measured benefit and maintainability review.
- Keep user-visible simplicity, clear progress and cancellation, and truthful reliability claims. Never trade stability, safety or accessibility for headline speed. Stop when improvements are marginal or higher-priority work is blocked.

## Agent interoperability: MCP server, not an agent-specific client
CrossWin should expose an **optional MCP server** as an adapter over the same local engine/API used by the standalone GUI. MCP clients live in compatible AI-agent hosts; CrossWin should not require users to install or understand MCP for manual operation. Validate actual MCP support in each host (including Grok Bot) rather than assuming it.
- Begin with a small stable tool surface: discover projects/devices/sessions; start or attach to a session; inspect compact UI/accessibility state; perform semantic tap/type/scroll/navigation or batched actions; retrieve diagnostics/captures; assert conditions; stop session. Return stable session IDs, structured results, timestamps, capabilities and actionable errors.
- Prefer a local low-latency transport and direct in-process engine calls for the GUI; do not route all internal operations through MCP. Compare MCP vs direct API end-to-end overhead before claiming speed gains. MCP improves interoperability and usability; it is not inherently faster than a native API.
- Support headless agent control and optional user-visible attachment to the *same* session, without duplicating runtime state. Keep explicit user approval for device permissions, sensitive actions and access to private logs.
- Make the MCP adapter discoverable and documented with examples for multiple compatible hosts; never couple core functionality to Claude Code or any single provider. If a host cannot use MCP, offer the same capabilities through a documented local API or supported adapter.
- Benchmark real action-to-observation latency, diagnostic usefulness, setup steps and first-success time for novice users; prioritize reliable end-to-end workflows over a large tool catalog.
