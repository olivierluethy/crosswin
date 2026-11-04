# CrossWin GUI — design notes (slice 1)

Audience: non-developers, possibly on older hardware. Job: point at a project folder → get an
honest, plain-language verdict on whether it will run → run it. No terminal, no jargon.

## Why not the generic look
Deliberately avoiding the AI-design tells: no cream+serif+terracotta, no black+acid-green, no
broadsheet hairlines, no identical-rounded-card kit, no ALL-CAPS tracked eyebrows, no "→" on
buttons, no decorative middle-dot meta. Monospace is used ONLY for literal technical identifiers
(folder path, target framework) — real data, not labels.

## Tokens
- Palette (light): paper #FBFAF7, card #FFFFFF, ink slate #23262F, muted #5B6270, hairline #E7E4DC.
  Accent indigo #3B45C4 ("crossing"/bridge, not terracotta).
- Honest state signals (the hero system): ready green #2F8F5B, limits amber #B8791B,
  no-match clay #C0504A. Each state = colour + lamp + plain word + plain sentence.
- Dark mode: ink paper #14161B, card #1B1E25, ink #ECEDF1, accent #8B93F2, states lightened.
- Type: system stacks only (this is a local, offline-capable tool — no web-font dependency).
  UI sans stack for everything; mono stack for paths/TFM values. Distinctiveness comes from the
  verdict signal system, the readout layout and the copy — not a bought typeface.

## Layout
Single centered column (max ~700px), a vertical "readout" like a pre-flight check:
1. slim host bar (your computer + CrossWin readiness — honest).
2. hero: H1 + one open-project field (the single obvious action) + recent folders.
3. on check: one prominent verdict card per app — big honest verdict word + lamp, plain summary,
   a compact spec strip (framework / needed .NET / build), "things to expect" (risks in plain
   language), and ONE next action: Run this app (or honest setup/explain state).
4. when running: a live panel reading the existing session (state + diagnostics).

The verdict signal is the one bold element; everything else stays quiet (Chanel's rule).
Motion only on the verdict reveal (answers the user's action); prefers-reduced-motion respected.

## Honesty rules (from CLAUDE.md)
- Never disguise unsupported WPF/WinForms features. Unsupported → no Run button, explain why.
- Experimental → run allowed, but "might work / things to expect" is shown up front.
- Setup needs the terminal once; say so plainly with the exact command rather than pretending.
