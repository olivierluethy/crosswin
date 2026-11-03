# Linux smoke test (x86_64 via Docker)

Runs `setup`, then `run` + `input` + `snap` for both samples under Ubuntu 24.04, distro Wine 9.0 and Xvfb.
On Apple Silicon Docker Desktop emulates linux/amd64.

```bash
docker build --platform linux/amd64 -t crosswin-linux-test tests/linux
mkdir -p /tmp/cw-repo /tmp/cw-out && tar --exclude .git --exclude bin --exclude obj -cf - . | tar -xf - -C /tmp/cw-repo
docker run --rm --platform linux/amd64 -v /tmp/cw-repo:/repo -v /tmp/cw-out:/out \
  -v "$PWD/tests/linux/smoke.sh:/t.sh" crosswin-linux-test /t.sh
```
Outputs `<App>.png` (agent capture) and `<App>-root.png` (whole Xvfb screen) in the out directory.
The repo is copied because the bootstrap writes `obj/` next to the CLI sources.
