# Theme preview selector

Status: Implemented

The session-only persistence behavior below is superseded by [RFC 0005](0005-user-configuration.md). The theme picker now saves selections and restores them at startup.

## Motivation

Allow trying Terminal.Gui's bundled themes before choosing a permanent default.

## Requirements

- Expose a Theme button and F2 shortcut on the main screen.
- List available framework themes and preview selection immediately across the UI.
- Closing the picker keeps the preview for the current session so workflows can be inspected. Offer a return to Default through the same picker.
- Do not persist the selection or change the startup default.
- Preserve tab navigation, selection, data safety, and the application's fixed layouts and disabled shadows.

## Design and safety

Use the installed Terminal.Gui theme registry and configuration application API. A modal picker lists themes and explains that changes last only for the current run. Apply colors to existing views as well as future dialogs, including inactive tabs. Preview never calls save mutation operations or writes configuration files.

## Implementation tasks

- [x] Add the selector, main-screen button, and F2 binding.
- [x] Apply themes to existing views and preserve app layout settings.
- [x] Document session-only behavior and verify switching across all bundled themes.
- [x] Format, build with zero warnings, run tests, publish to dist, and verify terminal startup and shutdown against a temporary root.

## Verification and acceptance criteria

All five bundled themes can be previewed without restarting. Current and inactive tab contents, action buttons, and new dialogs use the selected theme. Closing the picker retains the preview for that run; restarting returns to Default. File selection and bracket navigation remain functional. No save or configuration writes occur when previewing.


## Verification results

- Formatted and verified the application project, built the full solution with zero warnings, and passed all 61 automated tests. No dependencies changed.
- Verified all five themes using an isolated runtime harness: main-window colors, every tab's inherited table colors, picker colors, new dialog colors, disabled shadow defaults, selected-tab preservation, and returning to Default.
- Terminal.Gui configuration initialization scans loaded assemblies and encounters incompatible reflection metadata in the test runner. Theme application was therefore checked in the standalone harness rather than adding a test-runner-specific workaround to the app.
- Published the application to `dist/` and launched it with an isolated temporary `VSM_DATA_ROOTS`. Verified F2, all five visible choices, keyboard and mouse preview, closing the picker, bracket tab switching with the preview retained, and clean shutdown. Restarting restored Default. No save mutations or configuration writes were performed by theme preview.
