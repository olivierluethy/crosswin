#!/bin/bash
apt-get update -qq >/dev/null 2>&1 && apt-get install -y -qq --no-install-recommends xdotool >/dev/null 2>&1
Xvfb :99 -screen 0 1400x900x24 >/dev/null 2>&1 &
export DISPLAY=:99
cd /repo && ./crosswin setup >/dev/null 2>&1
./crosswin run samples/LegacyWinForms > /out/legacy.log 2>&1 &
for i in $(seq 1 400); do grep -q -E "Launching|exited|crosswin: " /out/legacy.log && break; sleep 1; done
sleep 30
xdotool mousemove 120 85 click 1; sleep 1; xdotool type "hallo Linux"; sleep 1
xdotool mousemove 347 85 click 1; sleep 3
xwd -root -silent | convert xwd:- -crop 440x170+0+20 /out/legacy-after.png
grep -E "Unhandled|Exception|exited" /out/legacy.log | head -5
