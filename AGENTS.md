# Mission Planner — agent instructions

Read `Porting/README.md`, `Porting/BASELINE.md`, `Porting/NATIVE_SURFACE.tsv` and
`Porting/PORT_SOURCE_IMPORT.tsv` before changing the migration architecture.

The root `MissionPlanner.csproj` has been converted in place from WinForms/net472 to the tested
cross-platform Avalonia/net10 application. The repository must contain one main product and
assembly named `MissionPlanner`; do not add a second sibling application and do not add the old
`MissionPlanner-Avalonia/external/MissionPlanner` submodule or source dependency.

Preserve upstream resources and translations until their replacement has been mapped. Every native
main-project source/resource must remain represented in `Porting/NATIVE_SURFACE.tsv` as `retain`,
`replace`, `merge`, `remove`, or `unported-blocker`. A compiling allow-list is only a transition
mechanism and is not evidence that excluded features have been ported.

Check control widths at the window's `MinWidth`, not its default size: Avalonia's `Grid` silently
paints an overflowing child across its neighbours, and a `NumericUpDown` spends 82px on spinner
chrome and padding before its first glyph.

The source/reference repository is `/home/alex/src/MP/MissionPlanner-Avalonia` at the exact commit
recorded in `Porting/BASELINE.md`. Keep it read-only unless the user explicitly asks to maintain it
separately. Never import `bin`, `obj`, `out`, secrets, signing private keys, or credentials.

The user temporarily disabled Claude. Do not invoke it until that restriction is explicitly lifted.
If it is re-enabled, use headless `claude -p`; never use `mcp__claude__Agent`.

Start each change from the latest `origin/master` on a topic branch named
`type/short-kebab-case-subject` and deliver it as a pull request. `port/avalonia-in-place` is the
finished, fully merged migration branch; do not use it. Preserve unexpected user changes, use
atomic commits, and do not merge to `master`, delete the old port repository, or rewrite published
history without explicit approval. Before each handoff record exact Git state, tests, builds,
remaining blockers, and the next executable step in `Porting/STATUS.md`.
