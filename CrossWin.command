#!/usr/bin/env bash
# Double-click this file to open CrossWin in your web browser.
# (On macOS: if it won't open the first time, right-click → Open → Open.)
#
# It changes into the CrossWin folder and starts the control surface; the first
# run installs a private .NET and may take a minute. Keep this Terminal window
# open while you use CrossWin — closing it stops CrossWin. Press Ctrl-C to stop.

cd "$(dirname "$0")" || { echo "Could not find the CrossWin folder."; exit 1; }

if [ ! -x "./crosswin" ]; then
  echo "This doesn't look like the CrossWin folder (no 'crosswin' launcher next to this file)."
  echo "Keep CrossWin.command inside the cloned crosswin folder."
  read -r -p "Press Return to close." _
  exit 1
fi

echo "Starting CrossWin… your browser will open in a moment."
exec ./crosswin gui
