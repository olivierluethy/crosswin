# CrossWin — live checklist and handoff

This is the single source of truth for ongoing work. Update it during implementation, not just at the end. Check off items only when evidence exists. Claude Code should reorder or refine tasks autonomously when findings demand it; avoid creating phase-plan documents.

## Mission status
- [x] Establish the actual macOS/Linux host environment and supported architectures; record constraints. (Apple M5 Max, macOS 27.2; see ARCHITECTURE.md evidence E1)
- [x] Inspect current backends; record findings in ARCHITECTURE.md. (Wine 11.21 via Gcenx build + self-contained Windows .NET runtime chosen; Homebrew wine casks disabled since 2026-09-01)
- [x] Own minimal samples: `samples/HelloWinForms`, `samples/HelloWpf` (net8.0-windows, part of this repo).
- [x] Third-party WPF: microsoft/WPF-Samples (MIT) CalculatorDemo and WPFGallery (.NET 10, Fluent) run and respond to input (E6).
- [ ] Third-party WinForms sample (e.g. dotnet/winforms samples or a small MIT app) through `./crosswin run`.
- [x] Icon-font gap: `inspect` flags Segoe MDL2 Assets / Segoe Fluent Icons; `~/.crosswin/fonts` is installed and registered on `run` (E9). No license-compatible drop-in with identical code points known.
- [x] PassMasterSuite runs with all icons after two app commits (missing using; embedded Fluent System Icons subset) — E8.
- [x] Text glyph gaps and Linux FailFast: host font replacements via Wine `Fonts\Replacements` (E10); PassMasterSuite text correct on macOS and Linux.
- [ ] Linux/Xvfb synthetic input unreliable (E11): click navigated in one run, not in the next; tooltip popup suspected. Reproduce with `CROSSWIN_AGENT_TRACE=1` + xwd, try moving the cursor first / explicit MOUSEEVENTF_MOVE.
- [x] Minimal CLI: `./crosswin inspect` reports framework, TFM, UI stack, platform, packages, risks and a verdict.
- [x] Real WinForms launch path on this Mac with logs (`./crosswin run`), verified interactively incl. clicks/typing (E3).
- [x] WPF launch feasibility: **works with WPF software rendering** for the sample (XAML, binding, gradients, text input) (E4). GPU path (`--gpu`) launches but is not visually verified.
- [ ] Verify WPF `--gpu` (D3D9 → wined3d → Vulkan/MoltenVK) visually on screen; needs a human look or host screen-recording permission.
- [x] Linux: verified in Docker linux/amd64 Ubuntu 24.04 + distro Wine 9.0 + Xvfb (E7); repeatable via `tests/linux`. Font prerequisite (`fonts-wine`) detected by `doctor`.
- [ ] Linux on physical older x86_64 hardware / real desktop session (X11 and Wayland/XWayland).
- [x] One-command setup/launch workflow: `./crosswin setup`, `./crosswin run <repo>`; private .NET SDK, no sudo, no Windows/Visual Studio.
- [x] .NET Framework (net4x / legacy csproj) in `crosswin run` (E13): legacy projects translated in the work copy, built with the .NET SDK, run on Wine Mono (bundled on macOS, auto-installed on Linux); Prefer32Bit fallback; verified on Linux with screenshots + xdotool input.
- [ ] snap/input for .NET Framework apps (no startup hooks in Mono): host capture on Linux (xwd/xdotool), macOS needs Screen Recording permission or a Mono-side agent.
- [ ] Visually verify .NET Framework apps on macOS (only process/log evidence so far).
- [x] Deliver a runnable user-testing milestone, clear instructions and known limitations (README quick start; fresh-home setup verified).
- [ ] User acceptance test of the milestone on their own apps.
- [ ] After user feedback, fix observed defects and add narrowly targeted regression tests.

## Expansion roadmap — linked to issues (existing work remains intact)
North star: [#6](https://github.com/olivierluethy/crosswin/issues/6). Requirements: [#2](https://github.com/olivierluethy/crosswin/issues/2) diagnostics, adapters, hot reload, sessions; [#3](https://github.com/olivierluethy/crosswin/issues/3) screenshot/video; [#4](https://github.com/olivierluethy/crosswin/issues/4) zero-config onboarding; [#5](https://github.com/olivierluethy/crosswin/issues/5) desktop UX, device rotation, traces. Inspect each issue before implementation.

- [ ] Reconcile existing code, commits, verified capabilities and #2–#6. Preserve checked WPF/WinForms items; identify next feasible working slice. (#6)
- [ ] Implement a shared, privacy-aware agent session/diagnostics API with structured errors, bounded logs, target identity, UI tree and interaction history; verify with one actual backend. (#2, #5, #6)
- [ ] Web adapter: launch/attach to React/Next/Vite or generic local web server, stream console/network/DOM/accessibility diagnostics and user interaction traces; reproduce and verify a real failure without manually writing Playwright tests. (#6)
- [ ] Android Real Device Bridge: discover USB-connected device via ADB, show authorized/offline/unauthorized state; guide developer options, USB debugging and trust prompts without bypassing permissions. Install/launch an app, read logcat and UI hierarchy, execute fast batched gestures, correlate events to errors. (#2, #6)
- [ ] Virtual-to-real continuity: replay supported test steps on an authorized physical device and compare errors, media behavior and latency. Do not promise process-state transfer. (#6)
- [ ] Capture and benchmark screenshots/video, device connection, reload, gesture-to-diagnosis, CPU/RAM and image loading. Avoid needless screenshot loops; use visual checks when needed. (#3, #5, #6)
- [ ] Non-coder UX: one-click target selection, honest runtime labels, desktop shell feasibility, managed dependencies and understandable permissions. (#4, #5, #6)
- [ ] Investigate iOS physical-device requirements and restrictions, distinguishing real testing from App Store/Google Play publication. (#2, #6)

**Next Start or Continue:** inspect git status and prior evidence, choose the next actionable unchecked vertical slice and implement it. Keep existing WPF/WinForms acceptance items active. If no authorized real Android device is attached, verify discovery/error handling and document the pending physical acceptance test rather than claiming success. After each meaningful change record the linked issue, commands, evidence, commit and exact next action.


## Product UX, design system and demo — linked to #4, #5, #6
- [ ] Audit current UI and distinguish shipped Web/Flutter/Android features from plans; preserve functioning backends. Document first-run journey and performance baseline. (#4, #6)
- [ ] Define an **original CrossWin style guide** (type scale, spacing, colors, icons, states, accessibility) inspired by frictionless products without copying Grok Bot branding. Use Superpowers brainstorming/frontend-design skills. (#4, #5, #6)
- [ ] Implement a guided next-action flow: open project, auto-detect, sensible single default device, run, preview, debug, optionally connect physical device; no unnecessary decisions. (#4, #5)
- [ ] Share UI components/design tokens between web and native desktop shell where practical; headless sessions remain usable and attaching a preview must not restart a session. (#5, #6)
- [ ] Support optional side-by-side device previews, quick target switch, rotation, and isolated simultaneous sessions with truthful capability labels and resource limits. (#2, #5)
- [ ] Measure first-preview latency, reload, diagnostic-event latency, CPU/RAM and compare native diagnostics with Playwright/CDP; only replace tools where benchmarked improvement is real. (#2, #6)
- [ ] Publish a GitHub Pages landing/documentation/demo site using the same style guide; clearly distinguish interactive frontend demos from actual emulator execution, which needs a runtime/backend. (#4, #6)
- [ ] Verify one actual Flutter app end-to-end with available hardware: agent start, structured failure, verified fix, human-visible preview and optional real-device run. Do not mark complete without evidence. (#2, #5, #6)

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
- **"Flaky" nav clicks in PassMasterSuite** — false alarm: my zsh loop used `set -- $v`, which does not word-split in zsh, so the agent got `click` without coordinates. With bash, 3/3 navigation clicks worked. Lesson: drive multi-step input from bash, and read `input` output instead of discarding it.
- **Icons in PassMasterSuite buttons still boxes after the font switch** — the global implicit TextBlock style (Segoe UI) applied to the TextBlock generated for string Button content. Fixed in the app with a local TextBlock style in the IconButton template.
- **Hyphen and superscripts as boxes** — FontSubstitutes ignored by WPF; Wine `Fonts\Replacements` works (E10).
- **Linux: PassMasterSuite "agent not answering" after click** — app had died: WPF FailFast because no "Arial" exists (fallback for unknown "Consolas"). Found via agent trace (`CROSSWIN_AGENT_TRACE=1`) + app.log. Fixed by core font replacements.
- **Linux: snap showed old view** — window-DC stale on X11 for WPF; proven with xwd root comparison (E11). Fixed with X11 host recapture.
- **Own test-script errors (3x)** — zsh `set --` without word splitting; `||` + `&` launch construct; wait-loop pattern `crosswin:` matching "crosswin: building CLI". Each produced misleading "failures". Use bash scripts and precise end markers.
- **User font not visible to WPF** — copy only: not listed; registry entry under CurrentVersion\Fonts fixes it (E9).
- **One `run` exited with 143 after 0.3 s** right after `wine reg delete`/`pkill`; immediate retry fine. Unexplained, watch for recurrence.
- **WinForms: 2 quick clicks counted once** — double-click semantics, same as Windows. Added `wait <ms>` input command.

## Work log
- 2026-11-03: .NET Framework support in `crosswin run`; samples/LegacyWinForms (old csproj, packages.config, Prefer32Bit, resx); Linux test script tests/linux/netfx.sh.
- 2026-11-03: Fresh-home check (`CROSSWIN_HOME=<empty dir> ./crosswin setup`): SDK 10 + Wine download/verify + prefix + agent in 2:31 min; HelloWpf run + 2 clicks + snap correct.
- 2026-11-02: Initial mission, autonomous workflow and handoff checklist established. Compared SwissHacks monorepo agent guidance; added Superpowers routing and debugging escalation.
- 2026-11-03: Rosetta 2 installed (user approved). Private .NET 8 + 10 SDKs, Wine 11.21, prefix. Samples, agent, CLI (`doctor/setup/inspect/run/snap/input`) implemented and verified end to end on macOS arm64.

## Agent reconstruction and reference-comparison compatibility — #6
- [ ] Research actual Reverse Engineer Anything (REA/RAE) repository and supported output/agent interfaces; do not invent an integration API. Keep CrossWin independent of the reconstruction tool.
- [ ] Define an optional reference-comparison contract: authorized reference screenshots/traces and implementation session, with explicit platform/viewport/state metadata.
- [ ] Expose compact UI-tree/layout/navigation/behavioral diffs and reproducible interaction steps to external agents through the existing agent session API, rather than requiring manual screenshot comparisons.
- [ ] Demonstrate an authorized sample app reconstruction feedback loop: change → CrossWin run → compare reference vs implementation → structured mismatch → verify correction; benchmark iteration time.
- [ ] Document boundaries: no DRM bypass, credential extraction, unauthorized proprietary assets or assumption of access to closed-source internals.

## Delivery discipline — #6 (apply to every future unchecked slice)
- [ ] At next Continue: inspect git status and the current verified backend, select **one** high-value acceptance scenario and record its baseline before implementing.
- [ ] Validate the riskiest assumption with a bounded spike; document actual device/tool availability and fallback rather than guessing.
- [ ] Implement the smallest complete path, verify actual execution and regression safety, measure latency/resources when relevant, and commit.
- [ ] Keep issue links, observed evidence, current blocker and the exact next runnable command in the handoff. Preserve previously checked items.

## Non-developer acceptance gate — #4, #6
- [ ] Validate that a first-time non-developer can launch CrossWin, open a supported sample project and reach a working preview using only guided GUI actions; record blockers and time-to-first-preview.
- [ ] Ensure no Claude Code, agent account, terminal command or developer jargon is required for the core supported user journey.
- [ ] Provide clear plain-language instructions for unavoidable permissions and unsupported platforms, with advanced controls progressively disclosed.

## Post-baseline autonomous performance optimization — #5, #6
- [ ] After one complete reliable GUI and agent test loop works, benchmark repeated cold/warm launch, interaction, diagnostics and time-to-verified-fix; record median/p95, resource cost and flakiness.
- [ ] Profile actual slow stages, inventory third-party tools and tune their supported configurations before considering alternatives or forks.
- [ ] Run controlled A/B comparisons for faster interaction/inspection paths on representative web/Flutter/Android targets; verify total loop speed, not isolated command speed.
- [ ] Apply one reversible optimization at a time; retain only measured gains that pass regression, novice usability, security and maintainability checks. Record before/after and rollback path.

## Agent-neutral MCP interoperability — #2, #4, #6
- [ ] Define a small optional MCP **server** adapter over the standalone CrossWin engine; document actual host compatibility, including verifying Grok Bot rather than assuming support.
- [ ] Demonstrate one complete agent loop: discover → start/attach session → inspect UI → batch actions → assert state → retrieve diagnostics; same session optionally visible in GUI.
- [ ] Benchmark action-to-observation latency and setup friction against direct API, avoiding unsupported claims that MCP itself accelerates execution.
- [ ] Preserve fully functional GUI-only operation; document permissions, session isolation, sensitive logs and alternative local API for hosts without MCP.

## Multi-agent shared test infrastructure — #2, #5, #6
- [ ] Define agent/session IDs, git revision/worktree, target screen/flow, device profile and structured test results in MCP and local APIs.
- [ ] Demonstrate two agents testing separate routes or branches through one CrossWin server with isolated mutable state, correct diagnostics and no cross-session interference.
- [ ] Implement bounded session scheduling, fair queues, exclusive mutable-device leases, timeouts, cancellation and resource-aware warm reuse.
- [ ] Add semantic navigation/assertions, revision-safe caching and test-failure classification (app bug vs stale assertion vs infrastructure).
- [ ] Load-test 1/5/20/100 simultaneous MCP clients; publish actual throughput, queue times, p50/p95 and hardware usage. Do not equate clients with 100 emulator instances.
- [ ] Present a simple human-readable overview of active agents, tests, results and blockers without exposing novice users to orchestration jargon.

## Intelligent test orchestration and trust — after isolated multi-agent baseline
- [ ] Stamp test evidence with git revision/worktree, build hash, device/runtime profile, fixtures and test version; visibly mark stale or unverified results.
- [ ] Implement conservative change-impact selection and targeted reruns; fall back to broader regression tests when dependency coverage is uncertain.
- [ ] Deduplicate identical concurrent test requests and safely reuse fingerprint-matched results; record invalidation decisions.
- [ ] Add deterministic replay, isolated fixtures, flaky-test detection, actionable cross-agent regression notifications and a plain-language project status.
- [ ] Compare optimized selection against periodic full regression runs; report saved time, missed regressions, false invalidations and queue/resource costs.
