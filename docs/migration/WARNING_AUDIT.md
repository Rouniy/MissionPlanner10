# Native dependency warning audit

Updated: **2026-08-24**.

## Scope and result

A clean Release build of the complete Avalonia test graph originally emitted **156 warnings** and
no errors. The warnings were all in native `ExtLibs` dependencies; the imported application and
test projects were already clean. After the reviewed fixes below, the same clean graph builds with
**zero warnings and zero errors** without a repository-wide `NoWarn`.

The original distribution was:

| Project | Warnings |
| --- | ---: |
| Zeroconf | 52 |
| MissionPlanner.Utilities | 46 |
| MissionPlanner.Drawing | 20 |
| MissionPlanner.ArduPilot | 13 |
| MissionPlanner.Comms | 11 |
| KMLib | 9 |
| DroneCAN | 3 |
| GMap.NET.Core | 2 |

The largest diagnostic groups were missing XML documentation (`CS1591`, 48), assigned-but-unused
locals (`CS0219`, 22), obsolete `AsyncifyVariable` diagnostics (16), unread private fields
(`CS0414`, 13), non-CLS public API (`CS3002`, 12) and obsolete `AsyncFixer01` diagnostics (9).

## Decisions and fixes

- Removed the obsolete build-only AsyncFixer and Asyncify analyzer packages from the three native
  projects that referenced them. They have no runtime role and reported stale patterns rather than
  current compiler defects.
- Disabled XML documentation-file generation only in the vendored Zeroconf project. The library is
  an internal application dependency, is not shipped as a public NuGet package and its inherited
  public documentation is incomplete. This is intentionally narrower than suppressing `CS1591`.
- Corrected hidden `Stream` members and deterministic disposal in BLE communications, removed
  unused retry/PInvoke state, and made the intentional fire-and-forget WebSocket send explicit.
- Restored equality/hash consistency for drawing matrices, initialized the vertical-font state,
  marked the Skia-backed drawing API intentionally non-CLS-compliant, and made the intentional
  `Region.IsEmpty` shadowing explicit.
- Made KML `*Specified` properties public so `XmlSerializer` can honor them, and removed unused KML
  wrapper state.
- Observed previously write-only camera/gimbal state through public read-only properties and log
  background MAVLink exceptions instead of silently swallowing them.
- Removed dead locals and fields across Utilities, ArduPilot, DroneCAN and GMap; explicitly observed
  intentionally fire-and-forget tasks; exposed GDAL progress reporting; and fixed small literal,
  exception and source-encoding issues found by the compiler.

Regression tests cover the changed matrix equality contract, KML optional-field serialization and
safe repeated disposal of an unopened BLE stream.

## Reproduction

From the repository root:

```bash
DOTNET_CLI_HOME=/tmp/missionplanner-inplace-dotnet \
  dotnet clean MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release --nologo

DOTNET_CLI_HOME=/tmp/missionplanner-inplace-dotnet \
  dotnet build MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release -m:1 --nologo --no-restore '-clp:WarningsOnly;Summary'

DOTNET_CLI_HOME=/tmp/missionplanner-inplace-dotnet \
  dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release --no-build --nologo
```

Current verified result: build `0 warnings / 0 errors`; tests
`1266 passed / 0 failed / 0 skipped`.

## Dependency security gate

The repository now enables NuGet audit for the complete transitive graph and promotes
`NU1901`–`NU1904` to errors. The audit found the vulnerable
`System.Text.RegularExpressions` 4.3.0 transitively in the inherited Utilities/ArduPilot graph and
the net472 legacy-plugin fixtures. RID-specific publish also exposed vulnerable
`System.Private.Uri` 4.3.0 in the netstandard graph. Those paths are pinned to patched 4.3.1 and
4.3.2 respectively; forced solution and RID restores plus
`dotnet list MissionPlanner.slnx package --vulnerable --include-transitive` report no known
vulnerable packages.
