---
name: frontend-design
description: CrossWin-specific guidance for clear, distinctive, accessible user interfaces when a GUI is actually needed.
---

# CrossWin frontend design
Use the installed Anthropic frontend-design skill if available; follow its complete instructions. If unavailable, use these local defaults without blocking implementation.

- Design for users who want to run existing Windows applications quickly, including non-experts on older hardware.
- Prefer simple installation, visible compatibility status, explicit run action, clear diagnostics and discoverable logs.
- Make interface language plain and actionable; never disguise unsupported WPF/WinForms functionality.
- Use coherent typography, spacing, keyboard navigation, accessible contrast, responsive layouts and restrained motion.
- Visually inspect real rendered UI where possible; use screenshots and interaction checks to fix layout defects.
- Avoid generic dashboard decoration and unnecessary UI while the CLI runner is not yet viable.
- Follow CLAUDE.md autonomous decision rules; make small design decisions independently.
