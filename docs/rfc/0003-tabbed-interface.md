# Tabbed interactive interface

Status: Implemented

## Motivation

The main action menu adds a modal selection step to every workflow. Expose each workflow as a persistent tab with its file list directly available.

## Requirements

- Provide Backup save, Restore backup, Rename world, and Repair save tabs using Terminal.Gui's TabView.
- Switch tabs with `[` / `]` and the library's native tab shortcuts. Preserve Tab / Shift+Tab for moving between the tab bar and screen controls; use arrow keys on the tab bar to select tabs.
- Keep confirmations, text entry, destination selection, notes, and repair progress in dialogs. Retain all existing safety checks and shared repository operations.
- Refresh lists when switching tabs and after actions, preserving the selected file where possible. Empty screens remain usable and show an explicit message.
- Keep backup deletion and full-note viewing on the Restore tab. Keep Esc to quit the main screen or cancel the current dialog.

## Design and safety

Build one main window containing a TabView and a keyboard hint. Each tab owns a table and its action buttons. Workflow methods accept the selected entry instead of opening an initial picker. Secondary pickers remain modal. Tab shortcuts are scoped to the main screen so text prompts accept literal brackets. No repository or database behavior changes.

## Implementation tasks

- [x] Replace the main menu with tab screens and direct entry actions.
- [x] Verify native bindings and add bracket navigation.
- [x] Refresh lists and handle empty screens and deletion.
- [x] Update README and DESIGN, add navigation coverage.
- [x] Format, build without warnings, run tests, publish, and verify terminal startup/navigation/shutdown using an isolated data root.

## Verification and acceptance criteria

All four lists appear inline without an initial modal. Keyboard and mouse tab changes work; keyboard focus can reach screen actions. Switching tabs and completing or cancelling actions never mutates saves implicitly. Delete requires confirmation, deleting the last backup leaves the Restore tab available, and modal prompts retain their own keyboard handling. Automated checks cover navigation, refresh, selection, and empty lists. Launch the published application in a terminal with VSM_DATA_ROOTS pointing to temporary data only.


## Verification results

- Formatted the application project, built the full solution with zero warnings, and passed all 61 automated tests. Dependencies are unchanged.
- Added automated coverage for inline tabs, bracket navigation from a focused table, native tab-bar arrows, selected-file preservation through reordered refreshes, empty lists, and re-enabling actions when files appear.
- Published to `/tmp/vsm-tabs-publish` and launched in a terminal with `VSM_DATA_ROOTS=/tmp/vsm-tabs-check/Saves`. Verified empty screens, bracket navigation, mouse tab selection, Tab/Shift+Tab focus movement, native tab-bar arrow navigation, and clean shutdown.
- Used SQLite's online backup API to copy a save into the isolated root. Verified inline metadata, backup prompt cancellation without creation, literal brackets in note input, backup creation, note viewing, restore destination selection and cancellation, repair mode selection and cancellation, and confirmed deletion of the last backup while the Restore tab remained open. All mutations were limited to temporary copies.
