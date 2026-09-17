# rust/testdata

SITL corpus logs used by the Rust crates' golden characterization tests.
Byte-for-byte copies of the canonical corpus maintained in the upstream fork
(userepo/MissionPlanner, `rust/testdata` on branch `rust/dflog-core`), which
in turn mirrors that fork's C# characterization corpus.

If the canonical corpus is ever regenerated upstream, refresh these copies
too - several Rust tests pin exact values from them (record counts, GPS.Lat
units, MSG text), the same way C# characterization goldens do.
