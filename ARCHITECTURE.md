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

## Initial architecture hypothesis (unverified)
1. **Project inspector:** parse .sln/.csproj, TargetFramework, UseWPF/UseWindowsForms, platform and native dependencies.
2. **Backend selector:** determine installed runtimes/compatibility layers and choose an explicitly supported route; report unsupported cases.
3. **Build/launch adapter:** isolate output and runtime settings; run the real program, stream stdout/stderr, expose exit status and logs.
4. **Compatibility gap ledger:** track concrete failing APIs, graphics issues, input behavior, installers and dependencies; implement minimal reusable shims only when justified.
5. **CLI-first interface:** single simple command before considering a GUI, IDE or extension.

## Research questions to answer with experiments
- Which WinForms projects and target frameworks run under current Wine on macOS/Linux?
- Can any representative WPF project run interactively? If not, what exact missing APIs, CLR support, graphics subsystem or XAML/runtime features block it?
- What can be built on non-Windows hosts with the available SDKs, and which build tasks remain Windows-only?
- What is the lowest-friction supported install method on Intel Mac, Apple Silicon and older Linux x86_64?
- Which third-party dependencies and licenses constrain packaging/distribution?

## Evidence ledger
Not yet populated. Claude Code must record for each experiment: date, host OS/CPU, sample and license, target framework, backend and version, commands, observed output, failure point, and source links where appropriate.

## Novel components
None created yet. When developing a missing component, document: precise gap, existing alternatives, why they fail, new implementation, compatibility scope, reproducible evidence and remaining limits.
