# In-place Avalonia migration

This directory is the record of replacing the root Mission Planner WinForms application in place
with the cross-platform Avalonia port. The work happened in the native Mission Planner fork so the
implementation, backend fixes, plugins, documentation and future upstream merges share one
history. Its manifests are frozen; the scripts in `build/migration/` check them, and CI runs
`check-native-surface.sh`. Product documentation is in [`docs/`](../), and the running status
and handoff log is [`docs/STATUS.md`](../STATUS.md).

## Non-negotiable result

- `MissionPlanner.csproj` becomes the net10 Avalonia application.
- The application/product/assembly identity is `MissionPlanner`.
- Ported views replace the corresponding native features in `GCSViews/`, `Controls/`, setup,
  wizard, plugin and other normal feature locations.
- No `external/MissionPlanner` gitlink, copied source tree, or build-time dependency is introduced.
- A historical submodule is removed only after the active source/project/package graph proves it
  has no consumer; this evidence now closes the former WinForms-only `ExtLibs/mono` dependency.
- WinForms files are removed only after a mapped Avalonia replacement or an explicit, justified
  `remove` decision exists.
- Existing RESX cultures remain preserved and receive an explicit localization mapping.

## Manifests

`NATIVE_SURFACE.tsv` inventories the tracked native C# and RESX surface outside `ExtLibs` and
`MissionPlannerTests`. Its statuses are:

- `retain`: keep and compile/reuse the native artifact;
- `replace`: an Avalonia implementation replaces it;
- `merge`: native and port implementations must be combined;
- `remove`: deliberate removal with a documented reason;
- `unported-blocker`: classification or replacement is not complete, so full-port acceptance is
  blocked.

`PORT_SOURCE_IMPORT.tsv` inventories every tracked file at the pinned port commit and records a
planned native target plus an import action. It prevents accidental import of the old submodule,
generated output, or a second application layout.

`IMPORTED_APPLICATION.tsv`, `IMPORTED_TESTS.tsv` and `IMPORTED_REFERENCE.tsv` retain the exact
source blob for copied artifacts. `PORT_SOURCE_RESOLUTION.tsv` closes every remaining repository,
project, package and deliberately retired source row with native evidence. Together they cover all
708 paths at the pinned source commit and are guarded by its canonical path/blob digest:

```bash
./build/migration/check-port-source-resolution.sh
# Strong local check against the read-only source worktree:
./build/migration/check-port-source-resolution.sh /home/alex/src/MP/MissionPlanner-Avalonia
```

Regenerate both inventories with:

```bash
./build/migration/generate-manifests.sh /home/alex/src/MP/MissionPlanner-Avalonia
```

Import the pinned application-owned source (not its project graph, tests, old submodule, build
output, or repository metadata) with:

```bash
./build/migration/import-application.sh /home/alex/src/MP/MissionPlanner-Avalonia
```

The importer verifies the exact source commit, refuses a dirty source, uses only the reviewed
mapping, records source blob identities and performs the planned application namespace migration
from `MissionPlannerAvalonia.*` to `MissionPlanner.*`. It only replaces the baseline `Program.cs`;
any other unexpected target collision aborts the import. It also generates
`ImportedApplicationItems.props`, the explicit transitional compile allow-list used by the root
project. The allow-list contains only audited imported files and never makes excluded native files
disappear from `NATIVE_SURFACE.tsv`.

Import the pinned regression-test sources (without copying their old project graph) with:

```bash
./build/migration/import-tests.sh /home/alex/src/MP/MissionPlanner-Avalonia
```

Test sources are placed below `MissionPlannerTests/Avalonia`, renamed to the native
`MissionPlanner.*` namespace family and retain a source-blob record in `IMPORTED_TESTS.tsv`.

Import the pinned historical audits needed to verify the migration with:

```bash
./build/migration/import-reference-docs.sh /home/alex/src/MP/MissionPlanner-Avalonia
```

These files live in `docs/migration/Reference` and are evidence for the in-place migration rather
than a second source tree. They remain byte-identical to their recorded source blobs, except
`NV_MODEM.md` and `PORT_STATUS.md`: private product names were removed after import, so
`PORT_SOURCE_RESOLUTION.tsv` records them as merged into those files.

Later corrections and implementation advances belong in the live `FEATURE_AUDIT.md`,
`NV_MODEM.md` and `TEMP_HANDLER_AUDIT.md` files. The matching names below `Reference/` are the
immutable import snapshot and must not be edited to make current state look historical.

`WINFORMS_RETIREMENT.tsv` closes the remaining inactive WinForms libraries, tools and plugins only
after mapping each operational workflow to native evidence or documenting why no user workflow
existed. `check-no-winforms.sh` rejects any returned retired path, WinForms project/code dependency
or submodule. Standard RESX reader/writer headers and untranslated WinForms layout metadata remain
as inert localization-source data: the Avalonia project neither compiles nor embeds these old RESX
files, while the native translation editor intentionally reads only translatable string entries.

`PROJECT_ARTIFACT_AUDIT.tsv` records every retained project/solution artifact outside the active
`MissionPlanner.slnx` graph and every retired inactive tree. Its checker rejects an unaudited
`.csproj`, `.sln`, `.slnx` or `.vcxproj`, a missing retained tool, or the return of a removed path:

```bash
./build/migration/check-project-artifacts.sh
```

This is deliberately stricter than deleting everything outside the solution. Conditional Windows
dependencies and independently useful protocol/generator tools remain explicit instead of being
misclassified by a Linux-only evaluated graph.

`BINARY_ARTIFACT_AUDIT.tsv` similarly accounts for every committed DLL/EXE/PDB/native library.
It distinguishes conditional Windows runtime payloads, hardware-driver artifacts and generators
from stale binary duplicates and compatibility assemblies:

```bash
./build/migration/check-binary-artifacts.sh
```

`KEY_ARTIFACT_AUDIT.tsv` accounts for committed strong-name/certificate containers. It removes
unreferenced inherited PFX/SNK files and makes the few retained public/strong-name artifacts
explicit; updater and package private signing keys remain external secrets:

```bash
./build/migration/check-key-artifacts.sh
```

The generator deliberately starts uncertain native C# entries as `unported-blocker`. Classification
is changed only after code-level comparison; this makes missing functionality visible instead of
silently excluding it from the new project.

## Execution order

1. Freeze and verify the two exact baselines.
2. Close every manifest blocker through retain/replace/merge/remove evidence.
3. Replace the root project graph and main assembly identity.
4. Import Avalonia sources into native feature locations and migrate namespaces.
5. Apply Settings, session-only MAVState parameters, Radio/Grid and CoreCLR compatibility directly
   to native source; remove build-generated patches.
6. Merge the legacy plugin ABI into the main `MissionPlanner` assembly and retain a distinctly named
   portable contract only where useful.
7. Preserve RESX localization and translation tooling.
8. Merge README, licensing, CI, CodeQL, updater, release and package workflows by meaning.
9. Require all port tests, relevant native backend tests, cross-RID publish, Linux smoke/package,
   plugin, localization and session-safety gates.
10. Commit/push the verified migration branch; merge/cut over only with user approval.

The detailed live progress and next step belong in `STATUS.md`; fixed source identities belong in
`BASELINE.md`.
