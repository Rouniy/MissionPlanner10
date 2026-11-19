# Frozen migration baseline

Recorded on **2026-08-24** in timezone Asia/Nicosia.

## Native fork — canonical target

- Path: `/home/alex/src/MP/MissionPlanner`
- Remote: `git@github.com:Rouniy/MissionPlanner.git`
- Baseline branch: `master`
- Baseline commit: `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`
- Baseline tree: `880a9b12c8cf0538e3fdb069c0afb36eeff6d95e`
- Subject: `DroneCAN: fix GetParameters() enumeration skipping parameters`
- Migration branch: `port/avalonia-in-place`

The unchanged local `master` branch is the rollback reference. The migration branch was created
directly at this commit from a clean worktree.

## Existing Avalonia port — read-only implementation source

- Path: `/home/alex/src/MP/MissionPlanner-Avalonia`
- Remote: `git@github.com:Rouniy/MissionPlanner-Avalonia.git`
- Branch: `main`
- Source commit: `8ed19081c972a80a8b6996ed817581bc59cbcb4b`
- Source tree: `a31f0d7fd49966907cb61680c049f4b2cc461e48`
- Subject: `fix(nv-modem): preview parameter differences before staging`

Its old `external/MissionPlanner` gitlink points to the exact native baseline commit
`67a3c4f22bd1b38ac499f9756902e04fa4ed8444`. This exact match is why the port can be applied as a
normal change set above native history without vendoring or retaining an external relationship.

## Verification at migration start

The old port had a prior green baseline of 1139 tests, Release build with zero warnings/errors,
cross-builds for Ubuntu/Windows/macOS and zero open CodeQL alerts. A local rerun on 2026-08-24
produced 1138 passes and one environment collision: the transport-construction test tried to bind
fixed UDP port 14550 while the user's `hermes2` process legitimately owned it. The failure was
`SocketException: Address already in use`, not a product-code regression. The test must use an
ephemeral port when it is imported into the fork.

## Immutable safety rules

- Do not move either baseline ref.
- Do not mutate or delete the source port repository during the in-place migration.
- Do not import generated `bin`, `obj`, `out`, package, signing-key, or credential material.
- Record any later upstream/source advance explicitly before rebasing or merging it.
