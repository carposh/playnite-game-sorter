# Game Sorter for Playnite

Point it at the folders where your PC games live, press **Scan & Sync**, and every game
shows up in [Playnite](https://playnite.link/) with its proper name, cover, background,
icon, description, genres, developer, publisher and release date. Run it again whenever
you like: it picks up new games, newer versions and deleted games, and tells you exactly
what changed.

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) ![playnite](https://img.shields.io/badge/Playnite-10.x-purple)

## Download

Grab `GameSorter.exe` from the [latest release](../../releases/latest). It's a single
file with nothing to install.

> Windows SmartScreen may say "Windows protected your PC" because the exe isn't
> code-signed. Click **More info → Run anyway**.

## Requirements

- Windows 10 or 11 (uses the .NET Framework 4.8 and PowerShell that ship with Windows)
- [Playnite](https://playnite.link/) installed the normal way (the portable/zip version isn't supported yet)
- An internet connection (names and artwork come from the Steam store)
- Games stored **one per sub-folder**, e.g.

  ```
  D:\Games\
    Balatro\
    Cruelty Squad\
    Subnautica 2\
  ```

## How to use

1. Run `GameSorter.exe`. The first time, it asks you to choose your games folder. Add as
   many folders as you like with **Add folder…**; it remembers them.
2. Press **Scan & Sync**.
3. Game Sorter closes Playnite, backs up your Playnite library, reopens Playnite to apply
   the changes, and shows a report when it's done.

Each run's log is saved and can be opened with the **Open logs** button.

## What it does

| Situation | What happens |
|---|---|
| New game folder | Added to Playnite with a working Play button, Steam name and all artwork/details |
| Newer version in a new folder (e.g. `Game.v1.2` → `Game.v1.3`) | The existing Playnite entry is switched to the new exe; the log shows old → new |
| Game files updated in the same folder | Logged as an update |
| Game folder deleted | Marked as *not installed* in Playnite (your playtime and data are kept) |
| Several copies of the same game | The newest copy is used; the others are listed so you can clean them up |
| Folder with no playable exe | Listed in the report (often an unfinished download or extract) |
| Game already in your Steam library | Skipped, so you don't get duplicates |
| Not found on Steam | Added under its folder name and tagged **Needs metadata** |

It also puts every game in Playnite into a category (`Steam Games`, `Epic Games`,
`PC Folder Games`, `Demos`, …). Use **Group by → Category** or **Genre** in Playnite's
sidebar.

### How it finds the right exe and name

- Skips installers, redistributables, crash handlers, uninstallers and similar files.
- For Unreal Engine games it launches the small launcher exe, not the one in `Binaries\Win64`.
- Cleans up folder names (`Some.Game.v1.2.3-Release` → `Some Game`) and tries the name
  of the folder holding the exe first, since that's usually the real title.
- Matches against the Steam store and only uses a match if it's confident.

## Safety

- Before every sync your Playnite library database is copied to
  `%APPDATA%\GameSorter\playnite-backups` (the last 5 are kept). To undo a sync, close
  Playnite and copy a backup's files back into `%APPDATA%\Playnite\library`.
- Game Sorter never moves, renames or deletes your game files.

## Command line

```bat
:: Show what a sync would do, without changing anything
GameSorter.exe --dry-run "%TEMP%\gamesorter.txt" "D:\Games" "E:\More Games"

:: Run a full sync without the window (useful with Task Scheduler)
GameSorter.exe --sync "%TEMP%\gamesorter.txt" "D:\Games"
```

## How it works

Game Sorter has two parts:

1. **`GameSorter.exe`** (C#, WinForms) scans the folders, identifies games on Steam,
   downloads artwork to `%APPDATA%\GameSorter\cache`, compares the result with the last
   run (`state.json`) and writes the changes to `pending.json`.
2. **A small Playnite script extension**, embedded in the exe and installed automatically,
   applies `pending.json` when Playnite starts and writes `result.json`, which the exe
   reads to build the report.

Playnite doesn't allow outside programs to edit its library while it's running, which is
why the exe restarts Playnite to apply changes.

## Building from source

No SDK needed; it builds with the C# compiler that ships with Windows:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces `GameSorter.exe` next to `build.ps1`.

| File | What it is |
|---|---|
| `src/GameSorter.cs` | The app: scanning, Steam matching, update detection, UI |
| `src/PlayniteExtension/GameSorter.psm1` | The Playnite side that applies changes |
| `build.ps1` | Builds the exe (and its icon) |
