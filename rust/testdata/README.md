# rust/testdata

SITL corpus logs used by the Rust crates' golden characterization tests and,
through a link in `MissionPlanner.Tests.csproj`, by the C# dflog tests that
compare the native and managed parsers over them.

Several Rust tests pin exact values from them (record counts, GPS.Lat units,
MSG text), so regenerating a log means updating those tests too. The C#
tests compare the two parsers rather than pinning values.
