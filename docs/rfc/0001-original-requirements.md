# Vintage Story Save Manager — Requirements

## Purpose

Build a .NET terminal user interface (TUI) application for managing Vintage Story savegames and backups. It supports selecting saves, inspecting metadata, restoring backups, deleting backups, and renaming worlds.

## Runtime and technology constraints

- Application language and runtime: C# on .NET 10.
- TUI library: [`Terminal.Gui`](https://github.com/tui-cs/Terminal.Gui).
- Vintage Story's internal save types are protobuf-net types loaded dynamically from the game DLLs.
- Vintage Story does not publish `.proto` schemas. Use the dynamic game-DLL loading technique demonstrated by [`bluelightning32/vs-proto`](https://github.com/bluelightning32/vs-proto) to access and serialize those types.
- Use GitHub CLI (`gh`) rather than `curl` when retrieving GitHub-hosted project information or source.
- Vintage Story may be installed as a Flatpak.

## Savegame discovery

Detect which of these Vintage Story configuration roots exist instead of assuming one installation type:

- Flatpak: `~/.var/app/at.vintagestory.VintageStory/config`
- Non-Flatpak: `~/.config`

The application must locate the relevant savegame data beneath every detected root. Behavior must remain unambiguous if both roots exist.

## Savegame format and metadata

- A savegame (`.vcdbs`) is a SQLite database.
- Save lists must show metadata extracted from the database rather than only the filesystem name.
- Required display fields:
  - Save or backup filename.
  - World name.
  - Total time played, rendered in a human-readable hours/minutes form.
  - Creation time, rendered like `9 Oct 2026, 22:59`.
- Metadata extracted from each savegame must match the values displayed by Vintage Story.

## Command-line and interactive UX

- Support subcommands for direct operations, including at minimum backup and restore.
- When invoked without a subcommand, open an interactive menu-driven TUI.
- Interactive workflows must cover backup, restore, backup deletion, world renaming, and repair.
- Support both arrow-key and Vim-style `h`/`j`/`k`/`l` navigation in menus and selection tables.
- Display column headers on save and backup selection tables.
- Destructive actions must require clear confirmation.
- Errors must be actionable and must not leave a partially copied or corrupted destination file.

## Backup workflow

1. Discover all savegames.
2. Let the user choose which savegame to back up.
3. Show each candidate's filename, world name, time played, and creation time.
4. Create a backup without mutating the selected live save.
5. Do not hardcode a savegame filename.

## Restore workflow

1. Show all available source backups with their metadata.
2. Allow a selected backup to be deleted from the same dialog/workflow, via a separate action or hotkey.
3. After choosing a backup, prompt for a restore destination:
   - Any existing savegame.
   - `<Save as new file>`.

### Restore over an existing save

- Clearly identify the destination that will be replaced.
- Ask whether the user wants to back up the destination before it is replaced.
- Require confirmation before replacement.
- Replace the destination safely so interruption does not leave it corrupted.

### Restore as a new save

- Prompt for a filename without the `.vcdbs` extension.
- Prefill the filename from the backup filename.
- Prompt for the restored world's name.
- Prefill the world name from the backup database.
- Write the chosen world name into the newly restored database.
- Validate the filename and prevent accidental overwrite.

## Rename-world workflow

1. Show a metadata-rich list of savegames.
2. Let the user select a savegame.
3. Prompt for and validate a new world name.
4. Update the world name in the save database using the game's protobuf-backed save types.
5. Preserve the rest of the save data.

## Repair workflow

1. Select a save to scan, optionally selecting a backup as a source of replacement records.
2. Work exclusively on consistent SQLite snapshots; require valid SQLite structure and decodable world metadata.
3. Validate all existing `mapregion`, `mapchunk`, and `chunk` payloads with the installed game types and protobuf-net runtime; decompress terrain data as well.
4. Require matching world identifiers, seeds, dimensions, creation/last-saved game versions, and world-generation versions. Reject backups ahead of the source's playtime and require the installed game version to match the save.
5. Report counts and each damaged record, including whether a valid backup replacement is available. CLI scans support `--json` and write no save unless `--new` is supplied.
6. Allow repair only when all detected damaged records have valid replacements in the selected backup. Write to a validated, unused filename alongside the source; never replace the source or an existing destination.
7. Preserve metadata, player data, and all unaffected records byte-for-byte. Verify those guarantees, database integrity, and all repaired-world payloads before publication.
8. Explain that affected records revert to the backup's state and may roll back builds, containers, and entities. Show source and backup playtime before TUI confirmation.
9. Keep the TUI responsive during scans and writes, support cancellation, and clean up staged output on cancellation or failure.

Repair does not perform SQLite structural recovery, regenerate terrain, identify missing rows, repair metadata/player payloads, or guarantee semantic validity of opaque mod data. Recommend testing the new copy in the game.

## Data safety and operational requirements

- Treat live saves and backups as user data that must not be silently overwritten or deleted.
- Use temporary files and atomic replacement where the filesystem permits.
- Preserve source files if an operation fails.
- Validate that a candidate is a readable Vintage Story save database before presenting or modifying it.
- Surface missing game DLLs, incompatible game/.NET versions, malformed databases, and insufficient permissions distinctly.
- Paths containing spaces must work.

## Acceptance criteria

- Running the program with no arguments opens the interactive main menu.
- CLI backup and restore subcommands are usable without entering the TUI.
- Both Flatpak and non-Flatpak configuration roots are detected when present.
- Savegame world names and played-time values match what the game displays.
- A user can select and back up any discovered valid save.
- A user can restore a backup over an existing save, with an optional safety backup.
- A user can restore a backup to a validated new filename and choose its world name.
- A user can delete a backup only after confirmation.
- A user can rename a world's display name without changing unrelated save data.
- A user can scan damaged world payloads and repair them into a new save using a compatible backup, with a machine-readable CLI report and a TUI preview.
- Interrupted or failed backup/restore operations do not corrupt the source or an existing destination.

## Resolved implementation decisions

- Saves are read from `VintagestoryData/Saves` beneath each detected configuration root; backups remain in `Saves/backup`.
- New backups use filesystem-safe names in the form `yyyy-MM-dd_HH-mm-ss-save-name.vcdbs`.
- The single row in SQLite's `gamedata` table contains the protobuf-net `SaveGame` payload. `WorldName` and `TotalSecondsPlayed` come from that dynamically loaded game type.
- The game directory is resolved from `VSM_GAME_DIR`, then `VINTAGE_STORY`, native installation candidates, and user/system Flatpak deployments.
- Saves from all detected installations appear together and include an installation-qualified ID such as `flatpak:main.vcdbs`, preventing ambiguity.
- A full path, qualified ID, or unique filename may be used on the CLI. Destructive non-interactive operations require `--yes`.
