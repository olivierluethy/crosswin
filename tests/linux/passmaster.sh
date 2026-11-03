#!/bin/bash
Xvfb :99 -screen 0 1280x800x24 >/dev/null 2>&1 &
export DISPLAY=:99
cd /repo && ./crosswin setup 2>&1 | grep -E "font:|FAIL|Ready"
./crosswin run pms --snap-after 5 > /out/pms.log 2>&1 &
for i in $(seq 1 400); do grep -q -E "==> snap|exited" /out/pms.log && break; sleep 1; done
S=$(cat ~/.crosswin/session/current); cp "$(ls -S $S/snap-*.png | head -1)" /out/pms-home.png
for v in "chk 85 196" "crk 83 237"; do set -- $v; ./crosswin input "click $2 $3"; sleep 5; ./crosswin snap | cut -f2-4; cp "$(ls -S $S/snap-*.png | head -1)" /out/pms-$1.png; done
grep -E "exited|Unrecoverable" /out/pms.log $S/app.log
