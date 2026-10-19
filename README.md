# Zendoria Camera Bridge

Runs on one always-on PC at a branch, on the same network as its cameras. It pulls each
camera's video feed and publishes it as a real, secure web address that the Zendoria admin
app can show — no port forwarding on the shop's router, no manual URLs to paste in.

## What it needs on that PC

- Windows, macOS, or Linux, always on (or on whenever you want cameras viewable).
- **[Tailscale](https://tailscale.com/download)** installed first — a real, separate
  install this program does not do for you (it drives Tailscale's own command line rather
  than replacing it). Nothing else needs configuring in Tailscale itself; the program
  connects it to Zendoria's own private network automatically during setup.
- Network access to the cameras' IP addresses (it should be, since it's the same LAN).

MediaMTX (the actual video relay engine) is downloaded automatically on first run — nothing
to install by hand for that part.

## Downloads

The [latest release](https://github.com/graktor/graktor-camera-bridge/releases/tag/latest) has a
self-contained executable for each platform (win-x64, macos-x64, macos-arm64, linux-x64), rebuilt
automatically from every push to `main`.

## Setup

1. In the Zendoria admin app, go to **Cameras → Zendoria Camera Bridge**, pick the branch,
   and generate a pairing code (valid 15 minutes).
2. Run `ZendoriaCameraBridge.exe` on the branch PC.
3. Paste in the pairing code when asked. That's it — from then on it runs unattended,
   automatically picks up any camera added to that branch in Zendoria, and keeps each
   camera's playback URL up to date on its own.

To run it automatically on startup, add a shortcut to it in
`shell:startup` (Win+R → `shell:startup`), or set it up as a Scheduled Task set to run at
log-on.

## How it actually works

Every 60 seconds it: sends a heartbeat, fetches the branch's camera list (with real RTSP
credentials — this program is the thing that connects to the cameras directly, not a
browser), regenerates and restarts a local [MediaMTX](https://github.com/bluenviron/mediamtx)
instance if the camera list changed, makes sure Tailscale is connected and its
[Funnel](https://tailscale.com/kb/1223/funnel) is turned on for MediaMTX's port, and reports
each camera's resulting public HTTPS address back to Zendoria.

## Troubleshooting

Check `bridge.log`, next to the exe — every run appends to it, and it's what to look at
first if a camera isn't showing up. Re-running the program with a fresh pairing code (delete
`bridge-config.json` first) starts pairing over from scratch if something's gone wrong with
this installation's own key.

## Building from source

```powershell
./publish.ps1
```

Produces a single self-contained executable per platform under `publish/<rid>/` (win-x64,
osx-x64, osx-arm64, linux-x64) — no .NET install required on the machine that runs it.
