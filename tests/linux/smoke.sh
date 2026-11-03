#!/bin/bash
Xvfb :99 -screen 0 1280x800x24 >/dev/null 2>&1 &
export DISPLAY=:99
cd /repo && ./crosswin setup 2>&1 | grep -E "FAIL|Ready|Not ready"
for app in HelloWinForms HelloWpf; do
  ./crosswin run samples --project $app --snap-after 4 > /out/$app.log 2>&1 &
  for i in $(seq 1 400); do grep -q -E "==> snap|exited" /out/$app.log && break; sleep 1; done
  if [ $app = HelloWinForms ]; then ./crosswin input "click 76 88" "wait 700" "click 76 88" "click 200 130" "type Linux says hi"
  else ./crosswin input "click 80 66" "wait 700" "click 80 66" "click 440 98" "type  on Linux"; fi
  sleep 1; ./crosswin snap; cp "$(cat ~/.crosswin/session/current)"/snap-*.png /out/$app.png
  xwd -root -silent | convert xwd:- /out/$app-root.png
  grep -E "==>|Unhandled" /out/$app.log
  pkill -f $app.exe; sleep 3
done
