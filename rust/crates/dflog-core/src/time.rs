//! Wall-clock time correlation for dataflash logs: derives a UTC base from
//! the first valid GPS fix, so board-time fields (TimeUS/TimeMS) can be
//! mapped to real timestamps - the same correlation Mission Planner's DFLog
//! establishes, with its quirks preserved where they matter:
//!
//! - field offsets are resolved via the `GPS` format even for GPS2/GPSB
//!   records (they share the layout in practice)
//! - a Status field of 0/1/2 (no 3D fix) rejects the record; an absent
//!   Status field does not
//! - the board-time offset prefers TimeUS with INTEGER division by 1000,
//!   falling back to the legacy `T` field
//! - at most 2000 GPS records are examined, like the managed warm-up pass
//!
//! One deliberate divergence: leap seconds come from the LOG's own GPS date
//! (historical table) rather than the host's current date, which the managed
//! code uses. Equal for every log recorded since 2017.

use crate::LogFile;

/// GPS->UTC leap second table: (gps week the count became effective, count).
/// GPS time began 1980-01-06 already 0 ahead; entries are cumulative.
const LEAP_TABLE: &[(i64, i64)] = &[
    (77, 1),    // 1981-07
    (129, 2),   // 1982-07
    (181, 3),   // 1983-07
    (286, 4),   // 1985-07
    (416, 5),   // 1988-01
    (521, 6),   // 1990-01
    (573, 7),   // 1991-01
    (651, 8),   // 1992-07
    (703, 9),   // 1993-07
    (755, 10),  // 1994-07
    (834, 11),  // 1996-01
    (912, 12),  // 1997-07
    (990, 13),  // 1999-01
    (1356, 14), // 2006-01
    (1512, 15), // 2009-01
    (1695, 16), // 2012-07
    (1851, 17), // 2015-07
    (1930, 18), // 2017-01
];

fn leap_seconds_for_week(week: i64) -> i64 {
    let mut leap = 0;
    for &(effective_week, count) in LEAP_TABLE {
        if week >= effective_week {
            leap = count;
        }
    }
    leap
}

/// unix epoch milliseconds of the GPS epoch 1980-01-06 00:00:00 UTC
const GPS_EPOCH_UNIX_MS: i64 = 315_964_800_000;

/// the established correlation: a UTC base plus the board-time offset it
/// corresponds to
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TimeBase {
    /// unix ms (UTC) of the correlation point
    pub gps_start_unix_ms: i64,
    /// board milliseconds at that point
    pub ms_offset: i64,
}

impl TimeBase {
    /// board milliseconds -> unix ms (UTC)
    pub fn wall_clock_unix_ms(&self, board_ms: f64) -> f64 {
        self.gps_start_unix_ms as f64 + (board_ms - self.ms_offset as f64)
    }
}

impl LogFile {
    /// Establish the wall-clock correlation from the first valid GPS fix
    /// (GPS, GPS2 or GPSB records, in log order). None when the log has no
    /// usable fix.
    pub fn time_base(&self) -> Option<TimeBase> {
        // quirk: offsets resolved via the GPS format regardless of record type
        let gps_fmt = self
            .name_to_id
            .get("GPS")
            .and_then(|id| self.fmts.get(id))?;

        let label_pos = |name: &str| gps_fmt.labels.iter().position(|l| l == name);

        let status_pos = label_pos("Status");
        let ms_pos = label_pos("TimeMS").or_else(|| label_pos("GMS"))?;
        let week_pos = label_pos("Week").or_else(|| label_pos("GWk"))?;
        let timeus_pos = label_pos("TimeUS");
        let t_pos = label_pos("T");

        let field = |record: &crate::access::Record, pos: usize| {
            gps_fmt
                .labels
                .get(pos)
                .and_then(|label| record.value(label))
                .and_then(|v| v.as_f64())
        };

        for (examined, record) in self.records_of(&["GPS", "GPS2", "GPSB"]).enumerate() {
            if examined >= 2000 {
                break;
            }

            if let Some(pos) = status_pos {
                match field(&record, pos) {
                    Some(status) if status <= 2.0 => continue,
                    _ => {}
                }
            }

            let Some(week) = field(&record, week_pos) else {
                continue;
            };
            let Some(gms) = field(&record, ms_pos) else {
                continue;
            };

            let week = week as i64;
            let sec = gms / 1000.0;
            if !(0..=5000).contains(&week) || !(0.0..(60.0 * 60.0 * 24.0 * 7.0)).contains(&sec) {
                continue;
            }

            let leap = leap_seconds_for_week(week);
            let gps_start_unix_ms =
                GPS_EPOCH_UNIX_MS + week * 7 * 86_400_000 + gms as i64 - leap * 1000;

            // board offset from the same record: TimeUS/1000 (integer
            // division, like the managed long.Parse(...)/1000), else T
            let ms_offset = if let Some(pos) = timeus_pos {
                (field(&record, pos)? as i64) / 1000
            } else if let Some(pos) = t_pos {
                field(&record, pos)? as i64
            } else {
                0
            };

            return Some(TimeBase {
                gps_start_unix_ms,
                ms_offset,
            });
        }

        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn corpus(name: &str) -> LogFile {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../testdata")
            .join(name);
        LogFile::open(&path).expect("corpus log")
    }

    #[test]
    fn leap_table_boundaries() {
        assert_eq!(leap_seconds_for_week(0), 0);
        assert_eq!(leap_seconds_for_week(76), 0);
        assert_eq!(leap_seconds_for_week(77), 1);
        assert_eq!(leap_seconds_for_week(1929), 17);
        assert_eq!(leap_seconds_for_week(1930), 18);
        assert_eq!(leap_seconds_for_week(4000), 18);
    }

    #[test]
    fn corpus_logs_have_a_time_base() {
        for name in ["copter.bin", "plane.bin", "rover.bin", "copter-isbd.bin"] {
            let log = corpus(name);
            let base = log
                .time_base()
                .unwrap_or_else(|| panic!("{name}: no time base"));
            // sanity: SITL simulates recent dates; well after 2020-01-01 UTC
            assert!(
                base.gps_start_unix_ms > 1_577_836_800_000,
                "{name}: {base:?}"
            );
            assert!(base.ms_offset > 0, "{name}: {base:?}");
            // monotonic mapping
            let t0 = base.wall_clock_unix_ms(base.ms_offset as f64);
            assert_eq!(t0 as i64, base.gps_start_unix_ms);
        }
    }
}
