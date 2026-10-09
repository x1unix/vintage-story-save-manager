# Vintage Story Save Manager

A .NET 10 terminal application for inspecting, backing up, restoring, deleting, and renaming Vintage Story saves. It supports both Flatpak and native data locations and uses the installed game's own protobuf-net model to read and update save metadata.

## Build

```sh
dotnet build VintageStorySaveManager.slnx
```

Run the TUI:

```sh
dotnet run --project src/VintageStorySaveManager
```

Run a CLI command:

```sh
dotnet run --project src/VintageStorySaveManager -- list saves
dotnet run --project src/VintageStorySaveManager -- backup --save flatpak:main.vcdbs
```

Use `vsm help` for the full command list after publishing.

Publish a runnable application to `dist/`:

```sh
make publish
./dist/vsm
```

## Location detection

The application detects saves under:

- `~/.var/app/at.vintagestory.VintageStory/config/VintagestoryData/Saves`
- `~/.config/VintagestoryData/Saves`

It detects user and system Flatpak game deployments automatically. Set `VSM_GAME_DIR` when the game is installed elsewhere. Set `VSM_DATA_ROOTS` to a colon-separated list of configuration roots or `Saves` directories to override save discovery.

Backups are stored in each installation's existing `Saves/backup` directory and remain compatible with backups created by `vstorybackup.sh`.

When both installations exist, entries are qualified by location (for example, `flatpak:main.vcdbs` and `native:main.vcdbs`). A full path, qualified ID, or unique filename can be supplied to CLI commands.

## Safety

Backups and restore staging use SQLite's online backup API. Replacements and world renames are prepared and validated in a sibling temporary database before an atomic move. Existing files are never deleted as an intermediate step.
