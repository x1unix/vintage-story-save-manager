# Repository guidance

## Project overview

This repository contains `vsm`, a C#/.NET 10 terminal application for managing Vintage Story savegames. The interactive UI uses Terminal.Gui. Savegames are SQLite databases whose `gamedata.data` value is a protobuf-net payload.

Treat save files as irreplaceable user data. Correctness and recoverability take priority over convenience.

## Source of truth

- Original product requirements: `docs/rfc/0001-original-requirements.md`.
- Feature proposals, change requirements, and implementation tasks: numbered documents in `docs/rfc/`; process guidance: `docs/rfc/README.md`.
- Architecture and implementation decisions: `DESIGN.md`.
- User-facing build and usage instructions: `README.md`.

Keep these documents consistent when behavior, commands, paths, or safety guarantees change.

## Change proposals

- Record future functionality, improvements, and behavior changes in `docs/rfc/NNNN-short-title.md` before implementation. Use the next available number. Keep status and tasks in the RFC itself; do not maintain an entry list in `docs/rfc/README.md`.
- Include status, motivation, requirements, design and safety considerations, implementation tasks, and verification/acceptance criteria. Distinguish planned behavior from implemented behavior.
- Follow approval already given in the conversation; documenting an accepted plan does not require asking for approval again.
- Update the RFC's tasks and status as work progresses. Accepted RFCs extend the original requirements; preserve `docs/rfc/0001-original-requirements.md` as the baseline and describe later changes in their own RFCs.
- Keep `DESIGN.md` and `README.md` aligned with implemented behavior and link relevant RFCs. Routine fixes that restore documented behavior can reference the existing RFC rather than creating a new proposal.

## Architecture constraints

- Target .NET 10 and keep the user-facing application in C#.
- Use Terminal.Gui for the interactive interface.
- Do not introduce generated `.proto` files for game data.
- Load Vintage Story's installed assemblies and protobuf-net runtime dynamically through `DynamicSaveCodec`.
- Keep CLI and TUI workflows backed by the same `SaveRepository` operations.
- Support both Flatpak and native save roots, including the case where both exist.
- Never resolve an ambiguous basename silently; require a qualified ID or full path.

## Data-safety rules

- Never modify live user saves during development or automated tests.
- Use temporary test roots through `VSM_DATA_ROOTS` and copies of save databases for integration checks.
- Create consistent database copies with SQLite's online backup API.
- Prepare and validate replacements in a sibling temporary file before atomically moving them into place.
- Require confirmation before replacement, deletion, or renaming unless the CLI caller explicitly passes `--yes`.
- Restrict deletion to files already discovered inside a managed backup directory.
- Validate filenames, destination containment, SQLite structure, and decoded protobuf metadata before mutation.
- Preserve the source and clean up temporary files when an operation fails.

## Development commands

```sh
dotnet restore VintageStorySaveManager.slnx
dotnet build VintageStorySaveManager.slnx
dotnet test VintageStorySaveManager.slnx
dotnet format src/VintageStorySaveManager/VintageStorySaveManager.csproj --verify-no-changes
dotnet publish src/VintageStorySaveManager/VintageStorySaveManager.csproj -c Release -o dist
```

Use `make build`, `make test`, or `make publish` for the corresponding common workflows.

## Code conventions

- Keep nullable reference types enabled and avoid suppressing nullable warnings without a documented reason.
- Prefer small services with explicit responsibilities over adding behavior to `Program`.
- Translate filesystem, SQLite, reflection, and protobuf failures into actionable `UserFacingException` messages at the application boundary.
- Keep CLI output stable and machine-readable when `--json` is requested.
- Use invariant culture for persisted or machine-readable values; use the specified display format for timestamps.
- Add or update tests for discovery, identifier resolution, validation, formatting, and safety behavior when those areas change.

## Verification expectations

Before handing off a change:

1. Format the C# project.
2. Build the full solution with zero warnings.
3. Run all automated tests.
4. Run the NuGet vulnerability audit when dependencies change.
5. For database-operation changes, exercise the affected workflow only against an isolated temporary data root.
6. For TUI changes, launch the published application in a terminal and verify clean startup and shutdown.

Metadata read from a savegame must match what Vintage Story displays for that save.
