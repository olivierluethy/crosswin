# CrossWin

**Run and test existing Windows WPF and WinForms applications on macOS and Linux — without migrating their UI frameworks.**

CrossWin is an early-stage open-source compatibility and developer-workflow project. Its mission is to make it possible to clone an existing C# WPF or WinForms repository, run the *real* app, interact with its interface, inspect changes and iterate quickly on macOS or Linux.

> **Current status:** Research and implementation have not yet established working WPF or WinForms compatibility. This repository is an autonomous-development scaffold, not a working runner. Do not assume your application will run yet.

## Start development with Claude Code

Clone the repository and open Claude Code in its root:

```bash
git clone https://github.com/olivierluethy/crosswin.git
cd crosswin
claude
```

Then enter just:

```text
Start
```

Claude Code should read [CLAUDE.md](CLAUDE.md), the [live checklist](PROGRESS.md), the [architecture ledger](ARCHITECTURE.md) and the [CrossWin skill](.claude/skills/crosswin-development/SKILL.md), then investigate and implement autonomously. You still control permissions, execution time and available tokens. A single prompt does not guarantee completion or continued work after the session ends.

## Priorities
- Genuine execution of existing WPF and WinForms applications, not a new editor or mandatory migration.
- macOS (Intel and Apple Silicon) and Linux, including modest older systems where feasible.
- One-command workflows, clear errors and minimal setup.
- Small implementation increments and a durable handoff checklist.
- Honest, evidence-backed compatibility claims.

## Documentation
- [CLAUDE.md](CLAUDE.md) — autonomous agent instructions.
- [PROGRESS.md](PROGRESS.md) — checklist and resume point.
- [ARCHITECTURE.md](ARCHITECTURE.md) — architecture, constraints and experiments.

## License
License to be selected before distributing implementation code or third-party components. Respect licenses of all dependencies.
