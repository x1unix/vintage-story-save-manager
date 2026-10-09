# Design and implementation plan

## Architecture

`vsm` is a single .NET 10 application. It owns discovery, SQLite access, safe file operations, command-line parsing, and the interactive `Terminal.Gui` UI.

At runtime it dynamically resolves assemblies from the detected Vintage Story installation, deserializes `gamedata.data` with the game's own `SaveGame` type and protobuf-net runtime, and reads or changes metadata through reflection. No generated schema is bundled, so metadata handling follows the installed game version.

## Data locations

For each existing root below, discover `VintagestoryData/Saves`:

- `$HOME/.var/app/at.vintagestory.VintageStory/config`
- `$HOME/.config`

Backups use the script-compatible `Saves/backup` directory. Each item retains its installation identity so equal filenames in two installations are not ambiguous.

The game install is resolved from `VSM_GAME_DIR`, `VINTAGE_STORY`, common native locations, then user/system Flatpak deployments. `VSM_DATA_ROOTS` provides an explicit save-location override for testing and unusual installations.

## Operations

1. Validate each candidate as SQLite and require exactly one `gamedata` row.
2. Read the protobuf blob and use the dynamically loaded game types for `WorldName`, `TotalSecondsPlayed`, and save version fields.
3. Create backups through SQLite's online backup API to get a consistent snapshot even if the source is open.
4. Restore by copying to a temporary sibling, optionally changing `WorldName`, validating and closing the result, and atomically renaming it into place.
5. Rename through the same temporary-copy and atomic-replace path.
6. Delete only files already discovered inside a managed backup directory and only after confirmation.

## CLI

- `vsm` — interactive TUI.
- `vsm list saves|backups [--json]`.
- `vsm backup --save <id-or-path> [--json]`.
- `vsm restore --backup <id-or-path> (--destination <id-or-path> | --new <name>) [--world-name <name>] [--backup-existing] [--yes] [--json]`.
- `vsm delete-backup --backup <id-or-path> [--yes]`.
- `vsm rename --save <id-or-path> --world-name <name> [--yes]`.

An exact full path always resolves. A basename or displayed ID resolves only when unique.

## TUI

The main menu offers Backup, Restore, Rename world, and Quit. Tables show filename, world name, played time, creation time, and installation. Restore selection also exposes deletion. All destructive actions use modal confirmation prompts.

## Verification

- Unit-test root discovery, formatting, and filename validation.
- Build and test the .NET solution.
- Run metadata inspection against the installed Flatpak and verify the two known saves.
- Exercise backup, restore-as-new, rename, replace-with-safety-backup, and deletion in a temporary data root; never modify the user's live saves during tests.
