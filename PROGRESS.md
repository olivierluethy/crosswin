# CrossWin — live checklist and handoff

This is the single source of truth for ongoing work. Update it during implementation, not just at the end. Check off items only when evidence exists. Claude Code should reorder or refine tasks autonomously when findings demand it; avoid creating phase-plan documents.

## Mission status
- [ ] Establish the actual macOS/Linux host environment and supported architectures; record constraints.
- [ ] Inspect current state of .NET WindowsDesktop SDK, WinForms, WPF, Wine/Wine Mono, CrossOver, and viable execution backends; record concrete findings and sources in ARCHITECTURE.md.
- [ ] Select small reproducible open-source WinForms and WPF sample projects with clear licenses.
- [ ] Build a minimal CrossWin CLI that inspects a C# project and reports framework, OS/architecture, dependencies and candidate backend.
- [ ] Implement the simplest viable real WinForms launch path on the current Mac, with useful logs and diagnostics.
- [ ] Investigate WPF launch feasibility empirically; implement a real path if possible, otherwise identify the exact missing APIs/runtime/rendering pieces and smallest next compatibility component.
- [ ] Add Linux support for the viable backend, with older x86_64 hardware and dependency requirements documented.
- [ ] Provide one-command setup and launch workflow with friendly errors and no Windows/Visual Studio prerequisite for supported cases.
- [ ] Deliver a runnable user-testing milestone, clear instructions and known limitations.
- [ ] After user feedback, fix observed defects and add narrowly targeted regression tests.

## Current handoff
**Status:** Repository instructions scaffolded; no compatibility implementation or runtime verification has yet been performed.

**Next action on Start:** Read CLAUDE.md and skill. Inspect local Mac architecture, installed runtimes, toolchains and project structure. Research actual backend feasibility, record evidence briefly, then immediately implement the first working CLI slice. Continue autonomously through checklist.

**Evidence/commands:** None yet.

**Blockers:** None identified; unknowns must be investigated, not treated as proof of impossibility.

**Decisions:** Original WPF/WinForms code must remain the target; no mandatory migration. Fast feedback and user-led milestone testing preferred.

## Work log
- 2026-11-02: Initial mission, autonomous workflow and handoff checklist established.
