# Config (CastleMiner Z Mod)

`Config` is a CastleMiner Z mod that adds an in-game config editor UI for files under the live `!Mods` folder.

## Features

- In-game config file browser/editor
- Save current file changes
- Reload all config files from disk
- Periodic hot-reload when config files change on disk
- Mouse + keyboard file navigation

## What It Does

`Config` gives you a live in-game way to manage text-based mod configs without tabbing out constantly.

- It scans the game runtime mods directory (`<GameFolder>\!Mods`).
- It finds config-like file types:
  - `.ini`
  - `.cfg`
  - `.config`
  - `.json`
  - `.xml`
- It shows those files in a left pane.
- It opens the selected file in a right-side text editor.
- It lets you edit, save, reload, and refresh from inside the game UI.

## How It Works

### 1) Menu injection and screen open

The mod uses Harmony patches to insert a `Config` menu entry and open a custom UI screen when selected.

- Main menu: adds `Config`
- Pause/in-game menu: adds `Config`
- Selection opens the custom `ConfigScreen`

### 2) File discovery

When the screen opens (or you press refresh/reload all), it:

- Resolves runtime mods root from `AppDomain.CurrentDomain.BaseDirectory + "!Mods"`
- Recursively enumerates allowed config file extensions
- Builds a sorted list by display path

### 3) Editor behavior

The editor is line-based text editing with:

- caret movement
- insert/delete
- mouse placement
- mouse-wheel vertical scroll
- unsaved/dirty tracking

`Ctrl+S` writes the current file to disk.

### 4) Reload behavior

`RELOAD ALL` re-scans and reloads config files from disk.

- If there are unsaved edits in the current editor buffer, reload is blocked to avoid accidental data loss.

### 5) Hot-reload behavior

A periodic poll checks for file list/content changes by computing a deterministic fingerprint of discovered config files.

- If fingerprint changed and editor is not dirty:
  - file list is refreshed
  - current file is reloaded
  - status shows that hot-reload happened

This makes external edits (for example from Notepad or another mod tool) show up automatically.

### 6) Safety guards

- Very large files are skipped (intended for config-sized files only).
- UI warns before switching files when the current file has unsaved changes.

## Build

Project file:

- `Config.csproj`

Release build output:

- `Build\Release\!Mods\Config.dll`

## Install

Copy `Config.dll` into your game mods folder:

- `C:\Program Files (x86)\Steam\steamapps\common\CastleMiner Z\!Mods\`

## Notes

- This mod is intended for config-sized text files (`.ini`, `.cfg`, `.config`, `.json`, `.xml`).
- Very large files are intentionally skipped for safety/performance.
