# User configuration and persisted theme

Status: Implemented

## Motivation

Keep the selected theme across application runs and provide a configuration store for future preferences. This extends RFC 0004 and supersedes its session-only persistence behavior.

## Requirements

- Store JSON at `$XDG_CONFIG_HOME/vs-save-manager/config.json`, falling back to `~/.config/vs-save-manager/config.json` on Linux. Use .NET's application-data special-folder API with DoNotVerify so resolving a missing directory does not create it or return an empty path.
- Persist the theme as `theme` and restore it at TUI startup, after Terminal.Gui initializes. Missing configuration uses Default and creates nothing until the user selects a theme.
- Selecting a theme applies and saves it immediately. Show an actionable error on save failure; keep the previous stored configuration intact.
- Keep the configuration model extensible and preserve unrecognized JSON properties when updating the theme.
- Reject malformed JSON, invalid theme values, and unreadable configuration with an actionable path-specific error. Do not overwrite a malformed file. Unknown framework theme names fall back to Default with a startup warning and remain unchanged on disk until the user selects a theme.
- CLI commands do not load or write UI configuration.

## Design and safety

Add a typed application-settings model and a dedicated configuration store, independent of Terminal.Gui and SaveRepository. Resolve the default path through `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify)`; allow an injected configuration base directory for isolated tests. Re-read configuration before each update to retain unknown properties. Serialize to a unique sibling temporary file, flush and close it, then atomically replace the destination. Clean up staging files on failure. No savegame behavior changes.

The existing theme picker becomes a persisted selector. Its help text explains automatic saving. Apply the stored theme before constructing the main window; keep application shadow defaults disabled. Retain all existing live-preview behavior.

## Implementation tasks

- [x] Implement settings model, standard path resolution, loading, and atomic updates.
- [x] Restore the theme at startup and persist selection with actionable error handling.
- [x] Cover missing/invalid configuration, unknown fields, paths, updates, and failure preservation in tests.
- [x] Update README and DESIGN and annotate RFC 0004's superseded behavior.
- [x] Format, build without warnings, run tests, publish dist/vsm, and verify terminal persistence with isolated config and save roots.

## Verification and acceptance criteria

Verify Linux fallback and XDG override, including a nonexistent config base; no configuration is written merely by starting the app. Select a theme, restart, and check the saved theme on the main screen and dialogs. Test Unicode/unknown property preservation, invalid JSON and schema errors, publication failure cleanup, unknown-theme fallback, and successful replacement. Use only temporary configuration and save roots during verification.


## Verification results

- Added 12 automated configuration cases and passed all 73 tests. Covered platform path resolution, missing files without directory creation, valid updates, latest unknown-property and Unicode preservation, invalid JSON/schema rejection without overwrite, unknown theme storage, read errors, and failed-update preservation/cleanup.
- Verified .NET path resolution in separate processes with XDG_CONFIG_HOME unset, set to a nonexistent absolute directory, and set to a relative value. Absolute overrides were honored; unset/relative values resolved beneath the home .config directory, and resolution created nothing.
- Formatted the C# project, built the full solution with zero warnings, and published to dist. No dependencies changed.
- Launched the published application using only temporary configuration and save roots. Starting without config created no file. Selecting Dark wrote the expected JSON; restarting restored Dark. Making the config directory unwritable produced an actionable error and preserved Dark in both UI and file.
- Verified an unavailable theme produced a readable startup warning and Default fallback without changing the file, then clean main-screen startup and shutdown. Invalid JSON failed before terminal initialization; CLI JSON listing still succeeded with the same invalid UI config. No live user saves or user configuration were modified during verification.
