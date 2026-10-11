# Vintage Story Save Manager

A .NET 10 terminal application for inspecting, backing up, restoring, repairing, deleting, and renaming Vintage Story saves. It supports both Flatpak and native data locations and uses the installed game's own protobuf-net model to read and update save metadata.

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

In the TUI, use the arrow keys or `h`/`j`/`k`/`l` to navigate menus and save tables. Table headers identify the filename, world, play time, creation time, and installation. Press Enter to activate the selected item and Esc to close the current dialog.

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

## Repair damaged world records

Close the world in Vintage Story first. In the TUI, choose **Repair save**, then **Scan only** or **Choose backup**. A scan checks every map-region, map-chunk, and terrain-chunk payload with the installed game's protobuf decoder, and decompresses terrain chunks. Selecting a backup also checks whether each damaged record has a valid replacement.

On the CLI, scan without writing a save:

```sh
vsm repair --save flatpak:main.vcdbs --json
vsm repair --save flatpak:main.vcdbs --backup flatpak:backup/2026-10-11_05-51-23-main.vcdbs --json
```

Create a repaired copy from a selected backup:

```sh
vsm repair --save flatpak:main.vcdbs --backup flatpak:backup/2026-10-11_05-51-23-main.vcdbs --new main-repaired --json
```

`--new` is required to write a save, and its filename must not already exist. The copy is placed beside the source, retains its world name, and preserves player data, world metadata, and every unaffected record byte-for-byte. Both interfaces use the same repair operation. JSON reports include table counts, record positions, errors, replacement availability, source/backup playtime, and the output path when a copy is created. A completed scan exits successfully even when it finds damage; a failed repair exits with an error.

The source and backup are snapshotted with SQLite's online backup API. Both must pass SQLite integrity checks. The backup must match the world's identifier, seed, dimensions, creation and last-saved game versions, and world-generation version; its playtime must not exceed the source's. The installed game version must match the last-saved version. Use `VSM_GAME_DIR` to select that installation if necessary.

Repair stops if any damaged record has no valid replacement. The staged output is fully checked again before it is moved into place without overwriting an existing file. Cancellation or failure discards temporary output and preserves the source and backup. TUI scans and writes run in a cancellable dialog; cancellation waits for the current SQLite operation to finish.

Restored records revert to their backup state. Buildings, container contents, and entities within affected chunks may roll back. This action does not recover SQLite structural corruption, regenerate terrain, reconstruct missing rows, repair player/world metadata, or validate every opaque mod payload or semantic relationship. Recover database structure on a separate copy first if necessary. Successful validation is not a full game-load test: load the new copy in Vintage Story before adopting it.
