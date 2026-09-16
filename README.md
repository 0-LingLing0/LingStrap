# Lingstrap

A Roblox bootstrapper for Windows. Presets, FastFlags, custom cursors, server location, multi-instance.

**Status: skeleton.** The app builds, runs and remembers settings. It does not launch Roblox yet.

## Build

Needs the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```
dotnet build
dotnet run
```

Single file release build:

```
dotnet publish -c Release
```

Output lands in `bin/Release/net8.0-windows/win-x64/publish/`.

## Layout

```
App.xaml(.cs)        entry point, decides UI vs bootstrap vs watcher mode
MainWindow.xaml(.cs) sidebar + page host
Models/              settings object, preset enum
Services/            paths, logging, settings load/save
Views/               one UserControl per sidebar page
Themes/Dark.xaml     all colours and control styles
```

Settings live in `%LOCALAPPDATA%\Lingstrap\Settings.json`.

## Roadmap

1. ~~Skeleton~~
2. Launcher core: find/install Roblox, write ClientAppSettings.json, start the client, handle `roblox-player:` links
3. Preset engine: flag sets + graphics settings per preset
4. Mods: file overlay, cursor packs
5. Activity: log watcher, server IP, geolocation
6. Multi-instance: mutex holder + watcher process

## Notes

Since Roblox's FastFlag allowlist (Sept 2025) most locally set flags are ignored by the client. Flag lists copied from old forum posts are mostly dead weight. Presets should stick to flags that are verified to still do something.

Not affiliated with Roblox Corporation.
