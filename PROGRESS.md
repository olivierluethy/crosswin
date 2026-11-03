# CrossWin — live checklist and handoff

This is the single source of truth for ongoing work. Update it during implementation, not just at the end. Check off items only when evidence exists. Claude Code should reorder or refine tasks autonomously when findings demand it; avoid creating phase-plan documents.

## Mission status
- [x] Establish the actual macOS/Linux host environment and supported architectures; record constraints. (Apple M5 Max, macOS 27.2; see ARCHITECTURE.md evidence E1)
- [x] Inspect current backends; record findings in ARCHITECTURE.md. (Wine 11.21 via Gcenx build + self-contained Windows .NET runtime chosen; Homebrew wine casks disabled since 2026-09-01)
- [x] Own minimal samples: `samples/HelloWinForms`, `samples/HelloWpf` (net8.0-windows, part of this repo).
- [x] Third-party WPF: microsoft/WPF-Samples (MIT) CalculatorDemo and WPFGallery (.NET 10, Fluent) run and respond to input (E6).
- [ ] Third-party WinForms sample (e.g. dotnet/winforms samples or a small MIT app) through `./crosswin run`.
- [ ] Icon-font gap (Segoe Fluent Icons / MDL2 missing): document "bring your own font" path or find a license-compatible substitute.
- [x] Minimal CLI: `./crosswin inspect` reports framework, TFM, UI stack, platform, packages, risks and a verdict.
- [x] Real WinForms launch path on this Mac with logs (`./crosswin run`), verified interactively incl. clicks/typing (E3).
- [x] WPF launch feasibility: **works with WPF software rendering** for the sample (XAML, binding, gradients, text input) (E4). GPU path (`--gpu`) launches but is not visually verified.
- [ ] Verify WPF `--gpu` (D3D9 → wined3d → Vulkan/MoltenVK) visually on screen; needs a human look or host screen-recording permission.
- [x] Linux: verified in Docker linux/amd64 Ubuntu 24.04 + distro Wine 9.0 + Xvfb (E7); repeatable via `tests/linux`. Font prerequisite (`fonts-wine`) detected by `doctor`.
- [ ] Linux on physical older x86_64 hardware / real desktop session (X11 and Wayland/XWayland).
- [x] One-command setup/launch workflow: `./crosswin setup`, `./crosswin run <repo>`; private .NET SDK, no sudo, no Windows/Visual Studio.
- [ ] .NET Framework (net4x / legacy csproj) apps: currently reported as Unsupported; investigate Wine Mono vs. MS .NET Framework redistributable + build path.
- [ ] Deliver a runnable user-testing milestone, clear instructions and known limitations. (README updated; awaiting user test)
- [ ] After user feedback, fix observed defects and add narrowly targeted regression tests.

## Current handoff
**Status:** User-testable milestone. On macOS (Apple Silicon verified) and Linux x86_64 (container verified) `./crosswin run <repo>` builds unmodified modern-.NET WinForms/WPF apps and runs them interactively under Wine; Microsoft's CalculatorDemo and WPFGallery (.NET 10) work. `snap`/`input` give visual inspection and real input.

**Next action on Start:** If the user reported test results, fix those first. Otherwise: third-party WinForms sample; visual check of WPF `--gpu`; icon-font gap; then .NET Framework 4.x investigation (Wine Mono).

**Blockers:** None. GPU-rendered WPF needs a visual check by a human (agent capture only sees GDI/software content).

**Decisions:**
- Backend: Wine (Gcenx macOS build pinned to 11.21 + sha256) running a **self-contained win-x64** publish; no Windows .NET install inside the prefix.
- Build on host with the .NET SDK and `EnableWindowsTargeting=true` (WPF XAML compilation works on macOS).
- Builds happen in `~/.crosswin/work/<project>-<hash>/src`, a one-way mirror of the repo; user source is never written.
- WPF defaults to software rendering (`HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration=1` in the prefix).
- Screenshots/input via `tools/CrossWin.Agent`, a .NET startup hook (`DOTNET_STARTUP_HOOKS`) — app binaries unchanged.

## Debugging record
- **Screenshot of Wine window blank** — (1) cross-process `PrintWindow`: all-zero pixels; (2) `CopyFromScreen`: throws, Wine mac driver can't read screen DC; (3) per-child `PrintWindow`: still zero. Evidence: pixel extrema all 0. Cause: HDC from another process is invalid in Wine's WM_PRINT path. Fix: capture in-process via startup-hook agent. `screencapture` on host needs Screen Recording permission (not granted).
- **WPF capture blank with hardware rendering** — D3D content is not captured by `PrintWindow`; software rendering is captured. Not proof of a rendering failure.
- **`dotnet` SIGKILLed (exit 137) after installing SDK 10 over SDK 8** — signature valid on disk; macOS caches signatures per inode. Fix: copy+move muxer (done in `./crosswin` bootstrap).
- **`setup` hung** — `wineserver -w` waits for all Wine processes, an app was running. Fix: no wait after `reg add`.
- **inspect misclassified WPF-Samples projects** (`UseWpf` lower-case) — MSBuild property names are case-insensitive; parser fixed.
- **Linux: snap timed out** — not an agent bug: app crashed on `new Font("Segoe UI")` without Wine core fonts, and `winedbg --auto` kept the process alive. Fixed with doctor font check, crash hint, winedbg disabled, crash watchdog.
- **Linux: agent snaps wrong** (children offset by non-client area; WPF black) — X11 driver differs from mac driver in PrintWindow. Fixed by reading the window DC, client-area coordinates.
- **WPF `--gpu` capture white** on macOS with both methods — D3D/Vulkan presentation bypasses the window surface; unresolved, needs on-screen human check.
- **WinForms: 2 quick clicks counted once** — double-click semantics, same as Windows. Added `wait <ms>` input command.

## Work log
- 2026-11-02: Initial mission, autonomous workflow and handoff checklist established. Compared SwissHacks monorepo agent guidance; added Superpowers routing and debugging escalation.
- 2026-11-03: Rosetta 2 installed (user approved). Private .NET 8 + 10 SDKs, Wine 11.21, prefix. Samples, agent, CLI (`doctor/setup/inspect/run/snap/input`) implemented and verified end to end on macOS arm64.
