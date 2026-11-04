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
  BitBlt from own window DC (fallback PrintWindow) -> PNG of client area; SendInput for click/type/wait
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
- **E7 Linux** (2026-11-03): Docker `linux/amd64` Ubuntu 24.04.5 (emulated on the M5 Max), distro Wine
  `9.0~repack-4build3`, Xvfb 1280x800, SDK 10.0.403 via bootstrap. HelloWinForms and HelloWpf: build, launch (.NET
  runtime up ~1 s after start), clicks, typing and binding verified via host `xwd` root screenshots and agent snaps.
  **Font finding:** without `fonts-wine` (a *Recommends* of the `wine` package) `new Font("Segoe UI", …)` throws
  `ArgumentException: Font '?' cannot be found` (GDI+ has no fallback). `doctor` now fails on missing `tahoma.ttf` and
  `run` prints the fix. **Crash finding:** Wine's `winedbg --auto` kept the crashed process alive; CrossWin now sets
  `WINEDLLOVERRIDES=winedbg.exe=d` and kills a lingering crashed process after 10 s.
  Not yet tested: real older x86_64 hardware, Wayland sessions, distro Wine older than 9.0.
- **E8 PassMasterSuite** (olivierluethy/PassMasterSuite @ 968b329, MIT, net8.0-windows WPF, 2026-11-03):
  - Build fails with CS0246 `ToggleButton` in Views/CrackerView.xaml.cs and Views/GeneratorView.xaml.cs — missing
    `using System.Windows.Controls.Primitives;`. Compiler error in the source (same on Windows), not a CrossWin gap.
    With the using added **only in the isolated work copy**, the app builds and runs: dark custom theme, Home cards,
    Generator (password, slider, toggle chips, strength bar), Checker and Cracker views.
  - Icons: all glyphs come from `Segoe MDL2 Assets` (proprietary) → empty boxes. U+2011 (non-breaking hyphen) also
    renders as a box in the fallback for `Segoe UI`.
  - After two app commits (ec93eea: missing `using`; 58e0937: embedded 3 KB MIT subset of Fluent System Icons +
    IconButton TextBlock style) the unpatched build runs under CrossWin with all icons on Home, Generator, Checker
    and Cracker; navigation clicks 3/3. (An earlier "flaky click" observation was a scripting error, see PROGRESS.md.)
- **E9 user fonts**: a font copied into `C:\windows\Fonts` is *not* seen by WPF under Wine (Fonts.SystemFontFamilies
  from Wine DirectWrite = macOS + Wine fonts only; even a `file:///C:/windows/Fonts/#Family` URI failed). After
  registering it under `HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts` WPF lists and renders it
  (verified with MIT Fluent System Icons). CrossWin now installs+registers every font in `~/.crosswin/fonts` on `run`.
- **E10 font resolution under Wine** (2026-11-03, FontProbe app + PassMasterSuite, macOS and Linux container):
  - Unknown family names ("Segoe UI", "SF Pro Text") render even ASCII "-" as a box on macOS; superscripts ⁴–⁹ are
    boxes in Tahoma/Arial; there is no per-character fallback.
  - `HKLM\...\FontSubstitutes` has no effect on WPF. `HKCU\Software\Wine\Fonts\Replacements` does: the replaced
    family appears in `Fonts.SystemFontFamilies`, and because WPF's fallback chain starts with "Segoe UI"/"Segoe UI
    Symbol", mapping those two also fixes missing glyphs in other fonts (verified: - ⁶ → ✓✔✕✗ ★☆ ▲▼ ½ ≤≥ äöü, regular
    and semibold).
  - **Linux crash:** WPF resolves an unknown family by falling back to "Arial" and calls `Environment.FailFast` if Arial
    is missing ("Unrecoverable system error", `FontFamily.get_FirstFontFamily`, also `TypefaceMap.MapUnresolved
    Characters`). The Ubuntu container has no Arial; PassMasterSuite died on opening the Cracker view ("Consolas").
  - CrossWin now maps Arial/Times New Roman/Courier New/Consolas/Segoe UI(+Light/Semibold/Symbol) to host fonts when the
    host lacks them (macOS: Helvetica Neue, Arial Unicode MS, Menlo; Linux: Liberation/DejaVu/Noto, detected via
    fc-list or font files). Result: no crash on Linux; all PassMasterSuite views render on both hosts.
- **E11 X11 capture staleness**: in the Linux container the agent's window-DC capture of PassMasterSuite still showed
  Home while the X server (xwd root) showed Checker. `snap` on Linux now crops the window from an xwd root capture when
  xwd + ImageMagick exist (method `x11-screen`), else warns. Synthetic clicks under Xvfb navigated in one run and not in
  the next (a tooltip popup was open over the start cursor position) — unresolved.
- **E12 .NET Framework app** (olivierluethy/TourneyMaster @ 52e4002, MIT; legacy csproj, net472 WPF, 2026-11-03):
  - Build on macOS works with a *shadow* SDK-style project in an isolated copy (same Compile/Page/Resource items,
    `TargetFramework=net472`, `UseWPF`, `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3) → real net472
    `Turnierspielplan.exe`.
  - Runtime: the Gcenx Wine build ships **Wine Mono 11.3.0** (`share/wine/mono`); the exe starts under it (process
    alive, only fixme lines, no err). **Not visually verified**: startup hooks do not exist in Mono, cross-process
    window-DC BitBlt returns FALSE, and host `screencapture` lacks Screen Recording permission.
  - Same sources retargeted to net8.0-windows (shadow project) render correctly under CrossWin (bracket, combo boxes,
    "Simulieren"). ComboBox drop-downs (AllowsTransparency popup) are not captured by the agent; one run showed two
    slots filled after a single click, a controlled rerun showed only focus — unresolved.
- **E13 .NET Framework in `crosswin run`** (2026-11-03):
  - `NetFramework.ShadowLegacyProjects` rewrites every non-SDK `.csproj` in the work copy in place (ProjectReferences
    keep resolving): TargetFrameworkVersion → TFM, Compile/Page/ApplicationDefinition/Resource/EmbeddedResource/None/
    Content items with metadata, framework and HintPath references, packages.config → PackageReference (packages\
    HintPaths dropped), Debug DefineConstants, Prefer32Bit/PlatformTarget; COMReference and custom imports → warnings.
    Reference assemblies come from the SDK's implicit Microsoft.NETFramework.ReferenceAssemblies.
  - Wine Mono: Gcenx macOS build bundles 11.3.0 in `share/wine/mono`. Ubuntu Wine 9.0 does not; `setup` reads the
    required version from appwiz.cpl (ASCII in Gcenx builds, **UTF-16 only** in Ubuntu's) → 8.1.0, downloads the MSI
    from dl.winehq.org and installs it with `msiexec /qn`.
  - 32-bit: Ubuntu container without wine32 failed with `failed to load C:\windows\syswow64\ntdll.dll` for a
    Prefer32Bit app. CrossWin detects 32-bit support (i386-windows/ntdll.dll) and builds AnyCPU+Prefer32Bit apps with
    `-p:Prefer32Bit=false` when missing (warning); explicit x86 apps get an install hint.
  - Evidence Linux (Ubuntu 24.04, Wine 9.0, Wine Mono 8.1.0): samples/LegacyWinForms renders ("Runtime:
    4.0.30319.42000 (64-bit)"), xdotool typing + click → Newtonsoft.Json from packages.config serialises
    `{"clicks":1,"note":"hallo Linux"}`; TourneyMaster (net472 WPF) renders its full UI. macOS: both build and run
    (process alive, no err lines) — not visually verified (no capture path for Mono apps).
  - Wine Mono WinExe processes print nothing to the terminal, so readiness markers via Console do not work there.
- **E14 agent session API, desktop** (2026-11-04, macOS arm64, HelloWpf + a scratch copy with a deliberate bug):
  - `ui`: WPF AutomationPeers read in-process (reflection, `Dispatcher.Invoke` with 5 s timeout) → role/name/automation
    id/value/toggle/bounds in snap/click pixel space; 9 nodes in 0.27–0.7 s. Password fields → `<redacted>`.
  - `input "click Click me"` resolves the label to the button centre; `expect "Clicks: 2"` polls the tree (3 s) and fails
    the batch; non-zero exit on failure.
  - `events`: `CS1002` reported as `build.error` with file:line mapped from the work copy to the user's tree; an
    unhandled `InvalidOperationException` from a click → `exception.unhandled` with type, message (secret redacted),
    20 frames, top user frame file:line. Warm restart: agent ready 0.4 s, window 2.5 s (cold: 12–13 s).
  - Crash → fix → verify loop including two builds/launches: 54 s, no screenshots.
- **E5 capture**: cross-process PrintWindow → all-zero bitmap; Graphics.CopyFromScreen → Win32Exception under Wine
  mac driver. In-process: PrintWindow includes child controls on the mac driver but not on X11, and WPF ignores
  WM_PRINT on X11 (black). Final method: BitBlt from the window's own DC (Wine window surface) — works for WinForms
  and WPF-software on both drivers; PrintWindow + child composition is the fallback. Images are the client area and
  share the coordinate system of `input "click X Y"`. WPF `--gpu` content is not captured by either method.

## Agent session / diagnostics model (issue #2)
```
~/.crosswin/session/<id>/session.json   identity: project, projectFile, sourceRoot, git{revision,branch,dirty}, ui, TFM,
                                         runtime (labelled "compatibility layer"), host, support, risks, state, pids
                          events.jsonl  {seq,t,ms,level,kind,msg,+file,line,col,code,type,stack,afterInteraction,…}
                                         kinds: session.start build.start/ok/failed/error app.launch/ready/output/exit
                                         exception.unhandled wine.<channel> (err deduplicated, fixme counted)
                                         interaction.batch capture.snap session.truncated
                          app.log, build.log (raw evidence, unchanged)
```
- Writers: the `run` process and every `ui/input/snap` call; cross-process exclusive file lock, seq from line count.
- Bounds: 5000 events, 2000 chars/message, 1500 UI nodes, 200 chars/value. Redaction: `password|token|secret|api_key…=…`,
  `Bearer/Basic …`, password fields. Correlation is timing-based (`afterInteraction`), labelled as observation.
- Coordinates everywhere = client-area pixels of the main window (same as snap PNGs and `click X Y`).
- Android sessions (`crosswin mobile run`) use the same files (`kind: "android"`). Two ingestion modes:
  - **Attached (default):** `mobile run` stays in the foreground with an `AndroidLiveCollector` that streams
    `adb logcat -b main,crash -v epoch -T <cursor>` continuously and, for Flutter, listens on the Dart VM service's
    Extension stream (`adb forward tcp:0 tcp:PORT` → `ws://127.0.0.1:<host>/<token>/ws` → `streamListen Extension`).
    `Flutter.Error` (framework errors / layout overflows that never reach logcat, E15) is classified by the same
    `AndroidDiagnostics.ClassifyFlutter` as logcat and tagged `source:"vm-service"`; `Flutter.Frame` → bounded
    `perf.jank`/`perf.frames`. The collector writes its pid as `liveCollector` in session.json.
  - **On-demand:** `--detach` returns right after launch (or when no collector is running); `events`/`session` then
    pull `adb logcat -d -b main,crash -v epoch -T <cursor>` and classify. `Ingest` skips logcat while a live
    `liveCollector` pid owns it, so the two modes never double-emit.
  All pids seen are kept, so restarts still match; event timestamps are device-log times (logcat) or wall time (VM).
  Implemented, not verified yet (compile-only).

## Web backend (issue #6, slice 1)
```
crosswin web run <url | file.html | dir>
  Web/Browser.cs     find a Chromium-family browser (Chrome/Chromium/Edge/Brave); launch headless (or --show) with
                     a throwaway profile + --remote-debugging-port=0; read the ws endpoint from DevToolsActivePort
  Web/WebProject.cs  resolve the target: http(s) URL | .html/static dir -> file:// (no server) | package.json dir ->
                     start the dev script (pnpm/yarn/bun/npm), scan output for the local URL
  Web/Cdp.cs         minimal Chrome DevTools Protocol client over ClientWebSocket (send+await by id, dispatch events),
                     "flatten" attach so messages carry a sessionId — same transport idea as the Dart VM-service collector
  Web/WebDiagnostics.cs  CDP page events -> shared SessionLog (kind "web"): Runtime.consoleAPICalled -> console.*,
                     Runtime.exceptionThrown -> exception.unhandled (source file mapped into the project), Network.
                     loadingFailed/responseReceived(>=400) -> network.failed/error, Log.entryAdded -> browser.*
  Web/WebCommands.cs  attached run (Ctrl-C detaches, browser left up), snap (Page.captureScreenshot), stop (close
                     target + kill browser/dev server), inspect. Reconnect info (cdpUrl, targetId, pids) in session.json
                     + ~/.crosswin/web/session.json, so snap/stop re-attach from a separate process.
```
  Web/WebPage.cs      interaction: `web ui` is one Runtime.evaluate returning a compact DOM/accessibility tree in the
                     shared node schema; `web input` does real CDP input (Input.dispatchMouseEvent / insertText /
                     dispatchKeyEvent — not element.click()), click by label (scroll into view + centre) or pixels,
                     type, key, scroll, expect/wait-for. Interactions recorded as interaction.batch.
```
No Node or Playwright is needed to *drive* the browser (CDP is spoken directly); a dev server still needs the app's own
Node toolchain, while a .html file or a static folder runs with no server. MCP exposes `crosswin_web_run`, and
`crosswin_ui/input/snap/stop` route to desktop, mobile or web by the current session's kind — the same agent loop across
all three backends. Implemented, not verified yet (compile-only).

## GUI control surface (issues #4, #5, #6 — for non-developers)
```
crosswin gui [--port N] [--no-open]
  Gui.cs            System.Net.HttpListener bound to 127.0.0.1 only (loopback; no web framework, no new deps).
                    Picks a free port (8787… then an OS-assigned one). Serves one embedded page + a small JSON API
                    over the SAME engine the CLI uses; Ctrl-C stops the server but leaves any running app alive.
  Gui/index.html    Single self-contained SPA (inline CSS/JS, system fonts so it works offline), embedded as the
                    manifest resource "gui/index.html". The original CrossWin design system lives here; the design
                    rationale is Gui/design-notes.md.
  API (all JSON):
    GET  /api/host     host OS/arch + honest setup readiness (wine/prefix/agent) → drives the status bar
    POST /api/check    {path} → auto-detects the backend and maps it to PLAIN LANGUAGE: a Windows app
                       (ProjectInspector + Assessment; wins when present), a mobile app (MobileProject.Detect,
                       guarded by a folder-local marker so it never escalates to an unrelated git root), or a
                       website (WebProject.Resolve with launchServer:false). One shape for all: {type, projects:
                       [{name, spec[], verdict, summary, risks, runnable, runKind}]}. Pure host-side; no launch.
    POST /api/run      {path,project,runKind} → spawns the existing attached runner for that backend via the
                       ./crosswin wrapper in the background (run | web run | mobile run; bash -lc … >> gui-run.log);
                       it writes the shared session files. Only runKind=desktop requires Wine setup.
    GET  /api/session  reads the current session.json (state, counts) — the GUI polls this while an app runs.
    GET  /api/events   reads events.jsonl (?since=&level=) — build errors/exceptions with file:line in the panel.
    POST /api/snap     runs the backend's snapshot (desktop `snap` / `web snap --out` / `mobile snap --out`,
                       picked by the session kind), returns each window PNG inline as a data URI → the GUI's
                       live Preview (so a non-developer never hunts for the app window).
    GET  /api/ui       runs the backend's `ui --json` (desktop/web/mobile) → the "What's on screen" list.
    GET  /api/devices  AndroidTarget.All over AndroidSdk → connected phones/emulator with state + plain-language
                       StateHelp; the mobile card's target picker (never bypasses the phone's trust prompt).
                       RunApp passes the choice through as --phone/--serial, and web's toggle as --show.
    POST /api/stop     Commands.Stop(env); GET /api/recent → recent folders (CROSSWIN_HOME/gui/recent.json).
```
The GUI is a thin presentation layer: it adds no capability the CLI/engine lacks and never bypasses the honest
support verdicts. Opening it does not restart a running session (it only reads the session files). Verified
host-side (host/check/index/session endpoints, 2026-11-04); the Run-from-GUI path and a visual review are pending
user acceptance. The engine stays fully usable headless and from the CLI; the GUI is optional.

## Known gaps / open questions
- WPF GPU path (Wine d3d9 → Vulkan → MoltenVK → Metal) not visually verified.
- Theme fidelity: WPF picks Classic theme under Wine; real apps with explicit styles are unaffected.
- .NET Framework 4.x apps: not supported yet (would need Wine Mono or the MS .NET Framework redistributable, and a
  macOS build path for legacy csproj/WinFX targets).
- Linux: verified in an emulated x86_64 container (E7), not yet on physical older hardware; ARM Linux would need box64/FEX.
- Startup hooks are ignored if an app sets `System.StartupHookProvider.IsSupported=false` (e.g. trimmed/AOT); then
  snap/input are unavailable but the app still runs.

## Novel components
- **CrossWin.Agent** — gap: Wine cannot capture another process's window (PrintWindow/screen DC), and host capture
  needs macOS Screen Recording permission. Alternatives: host `screencapture` (permission prompt), VNC/Xvfb (Linux
  only). Implementation: .NET startup hook, P/Invoke-only (no WinForms/WPF/System.Drawing dependency), PNG encoder,
  SendInput. Scope: .NET 6+ apps. Limits: GPU-rendered (D3D) content not captured.
