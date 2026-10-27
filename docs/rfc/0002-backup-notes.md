# RFC 0002: Backup notes

Status: Implemented.

The user approved the implementation plan and clarified that note deletion failures must be silent and non-blocking.

## Motivation

Users need to describe why they created a backup so they can identify the appropriate restore or repair source later. Notes must remain separate from Vintage Story savegame data.

## Requirements

- Prompt for an optional note when a user creates a backup interactively. Empty or whitespace-only input creates no note file.
- Store the note in an adjacent UTF-8 text file using the backup's filename stem: `foo.vcdbs` has a note at `foo.note.txt`. The same naming rule maps `foo.vcdb` to `foo.note.txt` if that extension is supported in the future; this feature does not change supported save extensions.
- Never write the note into the savegame database or protobuf payload.
- Existing backups without sidecars remain usable and display an empty note.
- Display notes wherever backups are listed: CLI text and JSON, the TUI restore/deletion selector, and the TUI repair backup picker. Place the Note column before Installation in TUI backup tables. Note fills the remaining table width as the terminal resizes; Installation stays sized to its header/content. Make long notes fully readable in the TUI.
- Delete the backup first, using the existing confirmation and managed-directory restrictions. Then attempt to delete its adjacent note. Ignore note deletion failures silently and report successful backup deletion as successful.
- Do not add prompts to automatic safety backups created during restore; these backups have no note.
- Editing notes on existing backups is outside this RFC's scope.

## Design and safety

Extend the shared `SaveRepository` backup operations and backup entry model so both interfaces use the same storage and cleanup behavior.

The TUI backup confirmation includes an optional note field. Cancellation creates neither a backup nor a note. The CLI accepts `backup --save <id> --note "Before exploring"`. Without `--note`, prompt only in interactive use. Redirected input and `--json` remain noninteractive; an omitted note creates a backup without a sidecar.

Choose an unused backup name that avoids collisions with both databases and sidecars, including orphaned note files. Never overwrite an existing note. Prepare and validate the database and stage the note before publishing them. Clean up files created by a failed operation while preserving the source and all pre-existing files. Two separate files cannot be published atomically as a pair; handle publication failures explicitly and do not claim that guarantee.

Derive sidecar paths from managed backup paths, enforce containment, and check links so note reads and writes cannot reach outside the managed backup directory. Keep note-read errors distinct from savegame metadata errors so a note problem does not make a valid backup unusable.

Expose an optional note in backup JSON results and lists, preserving machine-readable output. Render note text safely in terminal output and provide access to full note contents when table cells are truncated.

Restoring or repairing a save consumes its database; a backup note is not copied into the resulting live save.

## Implementation tasks

- [x] Add optional note data to backup entries and shared sidecar storage/read helpers.
- [x] Extend backup creation with optional notes, staging, collision handling, and failure cleanup.
- [x] Add best-effort sidecar deletion after successful backup deletion; suppress cleanup errors.
- [x] Add the optional note field to the TUI backup confirmation, preserving cancellation behavior.
- [x] Add CLI `--note` and interactive prompting without disrupting scripted or JSON use.
- [x] Display notes in every CLI and TUI backup list, including repair selection, with full-note access in the TUI.
- [x] Update both CLI help paths, README usage, and implemented design documentation.
- [x] Add automated coverage and complete isolated integration and terminal verification.

## Verification and acceptance criteria

- Test absent, empty, whitespace-only, and Unicode notes; verify the exact sidecar path and UTF-8 contents.
- Verify source database contents remain unchanged and notes never enter game metadata.
- Verify existing backups without notes remain discoverable, restorable, and usable for repair.
- Verify notes in CLI text/JSON and TUI restore/repair lists, including long-note access.
- Verify explicit CLI notes and noninteractive behavior; cancellation must leave no new files.
- Verify backup-name and orphan-sidecar collisions never overwrite existing data.
- Exercise staging/publication failures and check cleanup and source preservation.
- Verify successful deletion removes the note, absent notes are harmless, and note-deletion failure is silent and does not fail backup deletion.
- Verify failed backup deletion does not remove its note, and containment/link checks protect unrelated files.
- Format the C# project, build the full solution with zero warnings, and run all automated tests. Audit dependencies if they change.
- Exercise backup creation and deletion only with an isolated temporary root through `VSM_DATA_ROOTS` and copied save databases.
- Publish and launch the TUI in a terminal; verify note entry, list display, cancellation, and clean shutdown.

## Verification results

- Formatted the application project and verified formatting; built the full solution with zero warnings and passed all 59 automated tests. No dependency changes were needed.
- Automated coverage includes WAL-mode source databases, UTF-8/Unicode notes, blank notes, source preservation, note-free restore/safety backups, collisions, staging failures, deletion failures, symbolic links, unreadable notes, prompt selection, CLI output, and backup table contents.
- Exercised the published CLI against a consistent copy of a real game backup in an isolated temporary `VSM_DATA_ROOTS`. Verified sidecar contents, text/JSON lists, confirmed deletion, no leftover staging files, and unchanged source bytes. Also verified the CLI note prompt in a terminal.
- Verified responsive Note sizing at multiple table widths in an automated test and checked the published table in a wide terminal: Note fills the remaining space before a compact Installation column.
- Launched the published TUI in a terminal. Verified optional note entry, cancellation without new files, note viewing in restore and repair selection, scrolling to the end of a long note, and clean shutdown.

Backup staging switches the private SQLite snapshot to DELETE journal mode before validation/publication, preventing temporary WAL sidecars from being left behind. Note access rejects symbolic links in the backup directory's path, and note reads reject linked sidecars. As designed, database and note publication are separate filesystem operations; abrupt process interruption can leave a valid backup without its note.
