# Design and implementation plan

The [original requirements](docs/rfc/0001-original-requirements.md) establish the product baseline. Subsequent functionality and behavior changes are recorded as numbered proposals in [docs/rfc/](docs/rfc/), following the [RFC process](docs/rfc/README.md), with requirements, implementation tasks, and verification criteria. This document describes implemented design; accepted proposals remain planned until their implementation is complete.

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
7. Prepare repair through `SaveRepository.PrepareRepair`: `RepairSession` owns consistent temporary source/backup snapshots, checks SQLite schemas/integrity and world identity, then records damaged payloads and replacement availability. `DynamicSaveCodec` creates a `DynamicRepairValidator` using the installed protobuf types and terrain decompressor. Runtime/dependency failures abort validation rather than classifying all records as damaged.
8. Complete repair through `SaveRepository.CompleteRepair` with a new filename. Copy the source snapshot into a sibling staging file, replace only planned damaged rows in a transaction, revalidate all payloads and SQLite structure, compare metadata/player rows and every unaffected world payload, then atomically move without overwrite. Source and backup snapshots remain fixed between preview and completion. Dispose the session to remove snapshots. Reject unexpected triggers/schemas and incompatible world/game versions.

## Backup notes

[RFC 0002](docs/rfc/0002-backup-notes.md) extends backups with optional notes. `SaveEntry` exposes `Note` and `NoteError` independently from metadata errors. `BackupNotes` derives adjacent `.note.txt` paths, reads UTF-8, checks containment and symbolic links, sanitizes terminal display, and performs silent best-effort cleanup after database deletion.

`SaveRepository.Backup` chooses a name unused by both the database and its sidecar, stages a consistent SQLite snapshot and optional UTF-8 note, validates SQLite integrity and decoded metadata, then publishes without overwriting. On failure it cleans up staging and files published by that attempt, preserving pre-existing files and the source. Publishing two files is not atomic as a pair: a process interruption can leave a backup without a note. Automatic safety backups have no note. Restore and repair do not copy notes to live saves.

## CLI

- `vsm` — interactive TUI.
- `vsm list saves|backups [--json]`.
- `vsm backup --save <id-or-path> [--note <text>] [--json]`. Prompts for an optional note only when input is interactive, no explicit note is supplied, and `--json` is absent.
- `vsm restore --backup <id-or-path> (--destination <id-or-path> | --new <name>) [--world-name <name>] [--backup-existing] [--yes] [--json]`.
- `vsm delete-backup --backup <id-or-path> [--yes]`.
- `vsm rename --save <id-or-path> --world-name <name> [--yes]`.
- `vsm repair --save <id-or-path> [--backup <id-or-path>] [--new <filename>] [--json]`. Without `--new`, scan only; writing requires a compatible backup and valid replacements for all damage. JSON output contains mode, report, output (or null), and limitations. A completed scan exits successfully even if it finds damage; an unsuccessful write exits with an error.

An exact full path always resolves. A basename or displayed ID resolves only when unique.

## TUI

The main menu offers Backup, Restore, Rename world, Repair save, and Quit. Tables show filename, world name, played time, creation time, and installation. Backup tables also show a Note column before Installation, size Note to fill the remaining width as the table resizes, keep Installation sized to its header/content, and provide a View note action opening a scrollable, read-only full-note dialog. Backup confirmation includes an optional note field; cancellation writes nothing. Restore selection also exposes deletion. Deleting a backup refreshes the list and closes the selection dialog when no backups remain; only choosing Restore or activating a table row opens destination selection. Dialog buttons consume their acceptance events so other actions cannot activate the default button. All destructive actions use modal confirmation prompts.

Repair offers a scan-only mode or backup selection, followed by a damage summary, new-filename prompt, and rollback confirmation. Scans and writes run in a worker task with a cancellable progress dialog. Cancellation is checked between records and before publication; SQLite backup/integrity operations finish before cancellation takes effect.

## Verification

- Unit-test root discovery, formatting, and filename validation.
- Build and test the .NET solution.
- Run metadata inspection against the installed Flatpak and verify the two known saves.
- Exercise backup, restore-as-new, rename, replace-with-safety-backup, and deletion in a temporary data root; never modify the user's live saves during tests.
- Exercise repair on isolated copies with the installed game's decoder, checking that only damaged rows are replaced and all metadata/player rows are preserved. Unit-test compatibility, missing replacements, cancellation, failures, and refusal to overwrite.
