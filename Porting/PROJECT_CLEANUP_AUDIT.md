# Project cleanup audit

Updated: **2026-08-24**.

## Scope and safety rule

Cleanup was isolated and reviewed on `cleanup/project-audit`, then merged to `master` through PR #1
after the explicit user decision on 2026-08-24. The review branch contained the completed
`port/avalonia-in-place` work and removes an artifact only when its native manifest row is closed,
its replacement is present, and the active build/package graph has no reference to it. Git history
remains the recovery source for every removed file.

The supported product graph is `MissionPlanner.slnx`: one `net10.0` Avalonia application, its
portable plugin contract, the exact transitive libraries used by that application, and the active
Avalonia/plugin regression tests. `MissionPlanner.csproj` still uses an explicit compile list, but
an evaluated MSBuild inventory now leaves no application-owned C# outside the active graph except
the official version source `Properties/AssemblyInfo.cs`; the two `Plugin/PortableApi` sources are
compiled by their own project.

## Removed replaced application remnants

- Removed the closed WinForms implementations for Radio/SiK settings, legacy firmware selection,
  `Script.cs`, the hidden `temp.cs` developer form, old board detection/firmware upload, and the
  unsafe broad HTTP/WebSocket server. Their Avalonia/service replacements and migration decisions
  remain named in `NATIVE_SURFACE.tsv`.
- Removed standalone SiK Radio, Updater, RESX Editor, old test, plugin and WiX project files after
  the native SiK page, signed updater, translation editor, built-in FaceMap/OpenDroneID/Terrain
  Maker/Shortcuts features, and WiX 5 installer had replaced them.
- Removed obsolete embedded SiK firmware/images. The current firmware service downloads current
  board-specific images over HTTPS and validates them before upload; user-facing SiK translation
  RESX files remain preserved.
- Removed the dormant non-shipped Dowding plugin, generated Dowding/Dronelogbook API clients, test
  Node server and plugin-only ONVIF dependency after the dedicated security/function audit proved
  they were not part of the official release graph.
- Removed the inherited WinForms `.sln`, the old net472 tests for deleted code, legacy MSI/driver
  scripts, AppX manifest/assets, bundled ADB binaries, old `NuGet.exe` bootstrap, and developer-only
  batch/signing remnants. Current builds use the SDK, `MissionPlanner.slnx`, WiX 5 and GitHub CI.
- Removed the final unreferenced project debris only after a second reverse-reference audit. Exact
  decisions are machine-readable in `PROJECT_ARTIFACT_AUDIT.tsv`: replaced Antenna/GDAL/GMap/DFU/
  DroneCAN/tlog components, retired Android/Solo/ADB/HIL paths, obsolete drawing/UI compatibility
  closures and unused 7zip/zlib/DSP/sample projects are gone. The active `GeoUtility` source is
  preserved; only its obsolete mobile project/solution metadata was removed.
- Removed six Visual Studio WinForms `.datasource` files, the superseded
  `Properties/app.manifest`, and Visual Studio 2019/2022 workload snapshots for the retired
  net472/Xamarin/UWP graph. `vs2026.vsconfig` now describes only the supported .NET 10 desktop
  toolchain and the optional native X-Plane bridge.
- Audited every committed DLL/EXE/PDB/native binary in `BINARY_ARTIFACT_AUDIT.tsv`. Removed stale
  binary duplicates already supplied by NuGet/.NET, old MonoMac/OpenTK/DirectShow/System.Drawing/
  System.Speech/Transitions compatibility assemblies and generated legacy GDAL wrappers. Retained
  Windows SimpleBLE/libusb payloads, the protobuf generator and the hardware driver bundle with an
  explicit reason for each file.
- Removed two unreferenced inherited PFX containers, eleven unused strong-name keys and one unused
  public-key stub. They were
  not release/update signing inputs. `KEY_ARTIFACT_AUDIT.tsv` retains only three keys referenced by
  active strong-named projects plus the public certificate paired with the retained driver catalogs;
  private updater/package signing material remains outside Git.
- Removed the final inactive WinForms dependency graph after `WINFORMS_RETIREMENT.tsv` mapped each
  library/plugin/tool to a native replacement or an explicit retirement. This includes the former
  Mono System.Windows.Forms submodule, whose only remaining references were inside that same retired
  graph. The operational External Guided file plugin was ported before its old form was removed.
- Replaced the bundled Python 2/NumPy/py2exe log analyzer with a native in-process .NET analyzer
  before removing its obsolete source/build/upload tree. The replacement keeps all 17 enabled
  official diagnostics, isolates missing-data results, ignores expected high lean in ACRO/SPORT/
  FLIP/AUTOTUNE, and reports optical-flow corrections without silently creating a parameter file.
- Removed an obsolete netcoreapp3.1 Android publish batch file, an empty `AssemblyInfo.cs`, and a
  hard-coded Xamarin.iOS `System.Runtime.Loader` hint path from the active Utilities project.

## Removed alternate application experiments

The repository previously contained several independent application stacks in addition to the
main product. They were not libraries consumed by Avalonia and could not be validated as part of
Mission Planner:

- the EOL Xamarin Android/iOS/macOS/UWP application and its `MissionPlannerLib` aggregation graph;
- generated Uno/XAML conversion experiments;
- the old Blazor WebAssembly/Cesium application snapshot;
- the retired Windows Store packaging project;
- the Xamarin-only SkiaSharp WinForms renderer.

The associated manual Android/Apple workflows and mobile-only copied resources were removed with
those graphs. Linux, Windows and macOS desktop support is provided by the single Avalonia project
and its current package workflows; this cleanup does not claim Android/iOS product support.

## Build and warning fixes

- `MissionPlanner.slnx` now lists the exact active transitive project graph instead of hiding most
  libraries behind project-reference discovery.
- Disabled inherited `GeneratePackageOnBuild` for alglibnet, netDxf, MAVLink and SharpZipLib. They
  are internal project references; release automation does not publish their incidental NuGet
  packages or consume the resulting readme/license warnings.
- Restricted the PE application icon property to Windows/RID builds while preserving the same icon
  as an Avalonia resource on all platforms.
- Moved synchronous SimpleBLE scan/connect calls from the shared managed thread pool to dedicated
  blocking workers. A fully parallel CI run exposed that a blocked or slowly scheduled native BLE
  call could delay cancellation and peripheral cleanup; the cancellation regression now passes the
  complete suite and repeated stress runs without relaxing its timeout.
- Aligned Avalonia Desktop, Fluent theme, Inter fonts and the headless xUnit adapter with the
  application's existing Avalonia 11.3.18 core. The previous 11.3.13/11.3.18 mix could rarely make
  the headless adapter enter an unsupported dispatcher frame on a clean CI runner; the complete
  1253-test suite now passes repeatedly. DataGrid remains at its latest compatible 11.3.x release.
- Kept transitive NuGet auditing enabled and promoted `NU1901` through `NU1904` to errors. The
  active graph reports no known vulnerable packages.
- The `temp.cs` regression now validates the frozen 68-handler audit directly, so preserving the
  obsolete 1,400-line WinForms form is no longer required merely to count its methods.

The Release compiler build is strictly zero-warning and zero-error. `dotnet format ... analyzers`
also exits successfully with zero analyzer diagnostics and changes zero files. At diagnostic
verbosity, its .NET 10 MSBuildWorkspace loader still prints `Found project reference without a
matching metadata reference` for inherited netstandard project-reference edges; the same projects
resolve, compile and audit successfully in the authoritative build. These loader notices are not
suppressed or misreported as source warnings.

## Security scan triage

Cleanup CodeQL checkpoint `32688021913` originally reported five open but fully reviewed alerts and
no untriaged alert. The focused PR #6 security/quality pass preserves each decision as a narrow
source-level CodeQL suppression immediately at the reviewed sink:

- #1 is a generic writer in vendored netDxf. The Avalonia application reads DXF overlays and has no
  path to `DxfDocument.Save`; retain the reader and re-evaluate this flow if DXF export is added.
- #2 and #3 are the two overload paths of intentional operator-selected parameter-file export.
  Raw Parameters and DroneCAN both require `Dialogs.ConfirmDangerous` with reject as the default.
- #4 is intentional operator-selected decoded tlog export. Every UI entry point uses the same
  reject-by-default warning for precise coordinates, identifiers, missions, network details and
  parameters before the service writes a local file.
- #5 is the required ECB block primitive inside SharpZipLib's interoperable WinZip AES-CTR
  construction. It encrypts an incrementing nonce into a keystream and authenticates ciphertext;
  application data is not encrypted as plain ECB.

The pass also corrects a separate real defect in the vendored WinZip AES constructor: AES always
uses a 16-byte block/IV, but the old AES-256 path passed a 32-byte authentication key as the IV and
could fail before processing an archive. Deterministic round-trip tests now cover AES-128 and
AES-256. Parameter serialization also has regression coverage after its redundant decimal branch
was collapsed.

These are source-scoped, reviewable suppressions, not a repository-wide query exclusion or a
dashboard-only dismissal. The older source-port alert numbering and decisions remain immutable in
`Reference/CODEQL_TRIAGE.md`.

GitHub dependency vulnerability alerts are enabled. Secure `log4net` 3.3.2 and `SharpCompress`
0.48.0 versions are declared directly in every checked-in package manifest as well as enforced by
the root build; the local full transitive NuGet audit reports no vulnerable package. Two historical
Mapbox secret-scanning alerts refer only to removed upstream paths. At the user's explicit request
they were resolved as `wont_fix`, with an audit comment recording that token ownership/revocation
cannot be verified and published upstream-derived history will not be rewritten. They were not
misclassified as revoked or false positives, and no token value is reproduced here.

PR #6 code checkpoint CI `32721719954` passes the complete Linux, Windows and macOS matrix;
CodeQL `32721719966` passes and its branch-specific API result contains zero open alerts.

## Intentionally retained

- `Scripts/` contains 19 official Mission Planner IronPython examples. They are operator scripts,
  not build scripts, and are supported by `Services/PythonScriptHost.cs`; the directory is not
  cleanup material.
- Existing neutral and translated RESX files remain source data for the native translation editor.
  A source-less directory containing RESX files is therefore not automatically unused. Only empty
  Visual Studio resource templates or resources belonging to an explicitly retired feature were
  deleted and marked `remove` in the manifest.
- `NoFly/` contains four official KML/KMZ datasets. They are flight-domain data, not generated
  output, so they remain pending an explicit default-data packaging policy.
- `APMPlannerXplanes/` is the small native X-Plane/HIL bridge, not an abandoned UI port. It remains
  separate from the desktop application by design.
- Project/solution metadata outside `MissionPlanner.slnx` is exhaustively classified by
  `PROJECT_ARTIFACT_AUDIT.tsv`. The retained set is limited to the conditional Windows WinUSB
  dependency; MAVLink, parameter and P/Invoke generators; NMEA2000/NTRIP/EGM96 utilities; active
  GeoUtility vendor metadata; and the native X-Plane bridge. The checker fails if another project
  artifact appears without a decision.
- `Lib.zip` is the IronPython standard library consumed by `PythonScriptHost`; it is not a stale
  release archive. `build.bat` mirrors the supported solution build/test commands, and `Makefile`
  drives the current cross-platform package targets.
- `Swarm/Vertexs.py` is retained conservatively: it is a Blender content-authoring helper for the
  same `Layouts`/`Steps` JSON shape loaded, edited and executed by the native swarm sequence UI.
- `Properties/AssemblyInfo.cs` remains the authoritative upstream Mission Planner version source;
  the tracked local build number and Git commit are appended by the native version pipeline.
- Historical localized RESX files remain translation-source data. Their standard reader/writer and
  form-layout type strings are not compiled or embedded by the Avalonia project; the translation
  editor reads string entries only, so deleting them would discard upstream translations without
  removing a runtime WinForms dependency.

## Generated local output

`bin/`, `obj/`, `TestResults/`, `out/`, `dist/`, `upload/` and `__pycache__/` are ignored and must
never be committed. They are reproducible diagnostic/package output rather than source cleanup
targets.

## Reproduction gates

```bash
./build/porting/check-native-surface.sh
./build/porting/check-port-source-resolution.sh
./build/porting/check-no-winforms.sh
./build/porting/check-project-artifacts.sh
./build/porting/check-binary-artifacts.sh
./build/porting/check-key-artifacts.sh
dotnet restore MissionPlanner.slnx --force --nologo
dotnet build MissionPlanner.slnx -c Release -m:1 --nologo
dotnet format MissionPlanner.slnx analyzers --verify-no-changes --no-restore
dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release -m:1 --no-build --nologo
dotnet list MissionPlanner.slnx package --vulnerable --include-transitive --no-restore
make linux-packages
make windows-zip
```

The latest local audit passes all six structural gates, a zero-warning Release build, analyzer
verification, the no-vulnerable-package query and all 1263 tests after the NV modem
settings/security follow-up.
Linux TAR/DEB and Windows ZIP also build from the dirty review tree; the DEB passes `lintian` and
both archives pass integrity checks. WiX deliberately remains a Windows-runner gate: on Linux it
emits its documented Windows-only warning before undefined path validation. Published review CI
checkpoint `32688021866` passed Windows ZIP/MSI build plus default-path install/file
checks/uninstall, both macOS architectures, and Linux build/test/package/smoke. The final merged
`master` repeated that complete matrix successfully in run `32713804092`; CodeQL run `32713804084`
also passed.
