# Lingstrap

A Windows bootstrapper for Roblox — presets, FastFlag editing, mod and cursor
replacement, multi-instance support, CPU/GPU/network tuning, a themeable UI,
a self-updater, and a tiny standalone installer.

> **This entire project was written by AI.** Every line of code, the whole
> GitHub/CI setup, the release pipeline, the installer — all of it was
> written by Claude (Anthropic). No human wrote or edited any code, and no
> human configured any of the GitHub/build tooling by hand. The only human
> contributions were prompts and some of the ideas behind features. If
> you're reading the source expecting a human author, there isn't one.

## Download

Grab **`LingstrapSetup.exe`** from the
[latest release](https://github.com/0-LingLing0/LingStrap/releases/latest) —
that's the only file you need. It downloads and installs the real app
automatically, and keeps working for every future update from the same link.

Requires Windows 10/11 and the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
(Windows will prompt you to install it automatically if it's missing).

## Features

- **Presets** — three curated flag/graphics bundles (best performance,
  balanced, best quality) applied on launch, plus a **My Presets** tab to
  save your own: captures every enabled FastFlag, the same graphics settings
  the built-in presets manage, and process priority into a named preset you
  can re-apply, rename, or delete later
- **FastFlags** — hand-edit individual flags, on top of whatever preset is
  active
- **Mods and cursors** — swap in custom cursors, sounds, fonts, and splash
  images; per-slot size scaling
- **Multi-instance** — run more than one Roblox client at once
- **Behaviour tuning** — CPU core pinning, process priority, GPU selection,
  preferred launch monitor, network optimization (adapter power saving,
  packet throttling, QoS) with a one-click restore, and launch/close
  behavior (skip the loading screen, close Lingstrap once Roblox starts,
  reopen it once Roblox closes)
- **Server info and Discord Rich Presence** — an in-game overlay banner
  showing the city/country of the server you joined, and/or Discord Rich
  Presence reporting "Playing Lingstrap" with the current place
- **FPS overlay** — a small always-on-top, click-through frame-rate counter
  over the Roblox window, measured from outside the process; the one-time
  administrator approval it needs is asked for once, not on every launch
- **Companion apps** — launch other programs alongside Roblox, closed when
  it closes
- **Appearance** — pick an accent color (or any custom color via the
  built-in picker), light or dark theme, and a UI scale setting
- **Self-updating** — checks GitHub for new releases on startup; Notify mode
  prompts every launch until you actually update, AutoInstall updates
  silently and shows what's new on the next launch, both via the same tiny
  installer used to install it in the first place

## Building from source

Needs the .NET 8 SDK.

```bash
dotnet build
dotnet run
```

Single-file release build:

```bash
dotnet publish -c Release
```

The installer lives in `Setup/` and builds/publishes the same way, targeting
`Setup/Setup.csproj`.

## Notes

Since Roblox's FastFlag allowlist (Sept 2025), most locally set flags are
ignored by the client. Flag lists copied from old forum posts are mostly
dead weight - presets stick to flags verified to still do something.

Not affiliated with Roblox Corporation. Modifying your own client is your
own risk, especially multi-instance, which Roblox does not support.
