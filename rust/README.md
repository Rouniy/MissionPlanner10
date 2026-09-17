# dflog - native dataflash log core

Rust implementation of ArduPilot dataflash (`.bin`) log parsing, vendored
from the upstream fork (userepo/MissionPlanner, `rust/` on branch
`rust/dflog-core`, crates 0.7.1). Two crates:

- `crates/dflog-core` - parser, index scan, typed columnar access, units and
  GPS time-base metadata. Behavior is bug-for-bug compatible with the C#
  parser in `ExtLibs/Utilities` (`DFLogBuffer`/`BinaryLog`), pinned by the
  golden characterization tests over `testdata/`.
- `crates/dflog-ffi` - `dflog_ffi` cdylib exposing a C ABI (ABI version 5)
  consumed from `ExtLibs/Utilities` over P/Invoke. Every export catches
  panics at the boundary.

The main application builds this workspace automatically when `cargo` is on
the PATH (see the dflog targets in `MissionPlanner.csproj`); without a Rust
toolchain the app builds normally and uses the managed parser. The library is
never checked in - it is compiled per RID into `obj/` and flows into the
publish payload from there.

Local development:

```
cargo test            # golden + unit tests (needs testdata/)
cargo fmt --check
cargo clippy --workspace --all-targets
```

The upstream fork carries the wider surface (CLI, Python bindings, fuzz
targets, benchmarks). Changes to the parser core should land there first and
be re-vendored here, keeping crate versions in sync.
