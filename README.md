# CrossWin

**Run and test existing Windows WPF and WinForms applications on macOS and Linux — without migrating their UI frameworks.**

CrossWin is an early-stage open-source compatibility and developer-workflow project. Its mission is to make it possible to clone an existing C# WPF or WinForms repository, run the *real* app, interact with its interface, inspect changes and iterate quickly on macOS or Linux.

> **Current status (2026-11-03):** Early but working. Modern .NET (6+) WinForms and WPF apps build on macOS and run interactively under Wine. Verified on an Apple Silicon Mac with small sample apps only — your app may hit Wine gaps. .NET Framework 4.x apps are not supported yet. Linux x86_64 is verified in a container (Ubuntu 24.04, Wine 9.0), not yet on physical hardware.

## Quick start (macOS, Apple Silicon or Intel)

```bash
git clone https://github.com/olivierluethy/crosswin.git && cd crosswin
./crosswin setup                       # private .NET SDK + Wine + prefix under ~/.crosswin (no sudo)
./crosswin run samples --project HelloWpf
./crosswin run /path/to/your/repo      # or: --project <Name> if it contains several apps
```

Apple Silicon needs Rosetta 2 once: `softwareupdate --install-rosetta --agree-to-license`.
On Linux (x86_64) install Wine 9+ and its fonts first, e.g. `sudo apt install wine wine64 fonts-wine` (`./crosswin setup` prints the command).

Other commands:

| Command | Purpose |
|---|---|
| `./crosswin doctor` | Check host, Rosetta, .NET SDK, Wine, prefix |
| `./crosswin inspect <repo>` | Framework, target, risky APIs and a support verdict — before building |
| `./crosswin run <repo> --snap-after 3` | Run and save a PNG of the app windows after 3 s |
| `./crosswin snap` | Screenshot the running app |
| `./crosswin input "click 76 88" "wait 700" "type hello"` | Real mouse/keyboard input; X/Y are pixels in the `snap` image |

How it works: CrossWin copies your repo to `~/.crosswin/work/…` (your files are never modified), builds a self-contained `win-x64` publish with the .NET SDK on macOS/Linux, and runs the real `.exe` under Wine. WPF uses software rendering by default (`--gpu` to try hardware rendering, unverified). First start of an app takes ~10–15 s.

Missing Windows fonts (e.g. the icon font `Segoe MDL2 Assets`): copy the `.ttf` from a Windows installation you are licensed for (`C:\Windows\Fonts\segmdl2.ttf`) into `~/.crosswin/fonts/`; `run` installs and registers it in the Wine prefix. CrossWin does not ship Microsoft fonts.

Known limitations: WPF shows the Windows "Classic" theme for unstyled controls; WebView2/WebBrowser, COM components, Direct3D interop and native third-party DLLs are flagged by `inspect` and likely to fail; .NET Framework 4.x is reported as unsupported. See [ARCHITECTURE.md](ARCHITECTURE.md) for evidence.

## Mobile apps (Flutter, React Native, Kotlin) — no Android Studio, no Xcode

```bash
./crosswin mobile preview ~/code/my-flutter-app     # browser: iPhone, iPad, Pixel, Galaxy, moto side by side
./crosswin mobile setup                            # once: Android emulator from Google's command-line tools (~2 GB)
./crosswin mobile run ~/code/my-app --device galaxy-s24   # real Android in the emulator (or --phone over USB)
```

- **preview** (Flutter): the app compiled for the web inside device frames with each device's size, safe areas and
  iOS or Android behaviour; every saved file reloads all frames. Photo library, contacts and permissions are answered
  with CrossWin's test data, so pickers work. Each frame has its own phone storage: dart:io File/Directory (via
  IOOverrides), path_provider and saving to the gallery work and are kept on the computer, so drafts and exports
  survive reloads. Not real iOS: ML Kit, notifications, sharing and other native features need the emulator.
- **run / seed** (Android): builds the APK in an isolated copy, reshapes one emulator to the chosen phone
  (`wm size`/`density`), installs it with permissions granted and adds test data once: ~90 generated photos with
  EXIF dates and GPS over several years, short videos, contacts with birthdays, PDFs in Downloads.
  `seed --date 2026-10-31 --locale de-CH --location zurich --photo-access limited --photos 20000` sets device state.
  Your own files in `~/.crosswin/fixtures/user/` (e.g. photos with faces) are added too.
- **input / ui / snap**: `input "tap Skip" "scroll down" "type hello"` taps by visible label; `ui` lists labels;
  `snap` saves a screenshot.
- iOS apps cannot be compiled or simulated off macOS (Apple's SDK and Simulator are macOS-only); the preview's iOS
  behaviour is Flutter's, not Apple's. A Swift/SwiftUI-from-source path is planned.

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
