# CrossWin — technical reasoning and compatibility ledger

## Problem
Windows Forms and WPF are Windows-targeted desktop UI stacks. CrossWin aims to run *existing* projects on macOS/Linux for interactive developer feedback, without rewriting them into Avalonia, MAUI or a web UI.

## Key distinctions
- Editing C# source cross-platform is already possible; **executing Windows-specific UI frameworks** is the hard problem.
- Modern .NET WindowsDesktop / WPF and WinForms target Windows. A successful cross-platform compile or restored NuGet package does **not** prove that a GUI can run.
- Wine can provide Windows API compatibility, but support for a particular WPF/.NET runtime, renderer, architecture and app must be demonstrated, not assumed.
- Wine Mono, native .NET, Windows .NET Framework and modern .NET are not interchangeable.
- macOS Apple Silicon may require x86 translation/emulation for x86 Windows binaries; 2020 Macs can be Intel or ARM.
- VMs/remote Windows sessions can be useful diagnostic or fallback options, but must be labeled honestly: they do not satisfy a strict 'no Windows required' execution goal.
- Older Linux devices may lack required GPU features, libraries or instruction sets.

## Implemented architecture (verified on macOS arm64, 2026-11-03)
```
./crosswin (bash)  -> private .NET SDK in ~/.crosswin/dotnet, builds src/CrossWin.Cli
crosswin (host .NET CLI)
  inspect  : parses .csproj (no MSBuild eval): TFM, UseWPF/UseWindowsForms, OutputType, platform, packages,
             risky APIs (WebView2, WebBrowser, COM, D3DImage, HwndHost, AllowsTransparency, native P/Invoke)
  run      : mirror repo -> ~/.crosswin/work/<proj>-<hash>/src (user tree read-only)
             dotnet publish -r win-x64 --self-contained -p:EnableWindowsTargeting=true
             wine <app>.exe with WINEPREFIX=~/.crosswin/prefix, DOTNET_STARTUP_HOOKS=CrossWin.Agent.dll
             streams stdout/stderr to terminal + session/app.log, flags unhandled .NET exceptions
  snap/input: file protocol with the in-process agent (session dir)
tools/CrossWin.Agent (net6.0 startup hook, inside the app process under Wine)
  PrintWindow on own top-level windows -> PNG; SendInput for click/type/wait
```
Execution is a **compatibility layer** (Wine), not native and not a VM. The app's own binaries and the official
Windows .NET runtime (from NuGet runtime packs) run unmodified on Wine's Win32 implementation.

## Evidence ledger
- **E1 host** (2026-11-03): Apple M5 Max, macOS 27.2 (26B123), arm64, 48 GB. Rosetta 2 not preinstalled; installed
  with `softwareupdate --install-rosetta --agree-to-license` after user approval. Note: Apple has announced Rosetta
  phase-down after macOS 27 — an arm64-native Wine path will be needed long-term.
- **E2 toolchain**: Homebrew `wine-stable`/`wine@devel` casks disabled 2026-09-01 (Gatekeeper). Used Gcenx
  `wine-devel-11.21-osx64.tar.xz` (https://github.com/Gcenx/macOS_Wine_builds, sha256 9909b486…a0ce), x86_64 + wow64,
  runs via Rosetta. .NET SDK 8.0.427 and 10.0.403 via dotnet-install.sh into ~/.crosswin/dotnet.
  Building `net8.0-windows` WinForms *and WPF* (XAML markup compile) on macOS works with `EnableWindowsTargeting=true`.
- **E3 WinForms** (samples/HelloWinForms, net8.0-windows, self-contained win-x64): window shown ~12–14 s after launch
  (cold), Label/Button/TextBox(placeholder) rendered; SendInput clicks increment counter (3/3 with 700 ms spacing),
  Unicode typing works. Screenshot: in-process PrintWindow.
- **E4 WPF** (samples/HelloWpf): with default hardware rendering, `ContentRendered` fires but in-process PrintWindow
  shows white (D3D surface not captured — inconclusive). With `DisableHWAcceleration=1`: XAML layout, TextBlock,
  Button (Classic theme look — Wine reports no visual style, so WPF uses Classic, not Aero2), TextBox, two-way
  binding, LinearGradientBrush all render correctly; clicks and typing work.
- **E6 third-party WPF** (microsoft/WPF-Samples @ main, MIT, cloned 2026-11-03; all projects `net10.0-windows`,
  built with SDK 10.0.403 honouring the repo's global.json):
  - `CalculatorDemo`: renders (menu, buttons, text boxes, list); 7 × 6 = computed via SendInput → "42", tape shows
    "7 * 6 = 42".
  - `WPFGallery`: .NET 10 Fluent theme in dark mode, images, cards, NavigationView-style tree; clicking "Basic Input"
    navigates. **Gap:** all icon glyphs render as empty boxes — `Segoe Fluent Icons`/`Segoe MDL2 Assets` are
    proprietary Microsoft fonts absent from Wine; no license-compatible drop-in with identical code points known.
    Users who own a Windows licence may copy the font into the prefix (`drive_c/windows/Fonts`); CrossWin must not
    ship it.
  - `inspect` over 23 sample-application projects: 20 Supported, 2 Experimental (MediaElement), 1 library.
- **E5 capture**: cross-process PrintWindow → all-zero bitmap; Graphics.CopyFromScreen → Win32Exception under Wine
  mac driver. In-process PrintWindow works for GDI and WPF-software content.

## Known gaps / open questions
- WPF GPU path (Wine d3d9 → Vulkan → MoltenVK → Metal) not visually verified.
- Theme fidelity: WPF picks Classic theme under Wine; real apps with explicit styles are unaffected.
- .NET Framework 4.x apps: not supported yet (would need Wine Mono or the MS .NET Framework redistributable, and a
  macOS build path for legacy csproj/WinFX targets).
- Linux: code path exists (system `wine` on PATH) but unverified; ARM Linux would need box64/FEX.
- Startup hooks are ignored if an app sets `System.StartupHookProvider.IsSupported=false` (e.g. trimmed/AOT); then
  snap/input are unavailable but the app still runs.

## Novel components
- **CrossWin.Agent** — gap: Wine cannot capture another process's window (PrintWindow/screen DC), and host capture
  needs macOS Screen Recording permission. Alternatives: host `screencapture` (permission prompt), VNC/Xvfb (Linux
  only). Implementation: .NET startup hook, P/Invoke-only (no WinForms/WPF/System.Drawing dependency), PNG encoder,
  SendInput. Scope: .NET 6+ apps. Limits: GPU-rendered (D3D) content not captured.
