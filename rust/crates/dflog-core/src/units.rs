//! Units and multipliers metadata from the log's own UNIT / MULT / FMTU
//! records, exposed the way pymavlink's DFReader models it: pure metadata,
//! looked up per message field. Nothing here changes how values decode -
//! `access` and `columns` keep the legacy scaling; consumers apply
//! multipliers themselves if they want SI values.
//!
//! Semantics as written by ArduPilot:
//! - UNIT: unit id char -> unit name (e.g. 'd' -> "deg").
//! - MULT: multiplier id char -> factor (e.g. 'B' -> 0.01); the table the
//!   autopilot logs maps '-' to 0 and '?' to 1.
//! - FMTU: message type id -> one unit id and one mult id per field, in
//!   field order; '-' means "none". FMT's format string is 16 chars, so
//!   the 16-char FMTU strings always cover every field.

use std::collections::HashMap;

use crate::access::Value;
use crate::LogFile;

/// units/multiplier metadata for one message field
#[derive(Debug, Clone, PartialEq)]
pub struct FieldMeta {
    /// unit id char from FMTU; None when the log marks the field '-'
    pub unit_id: Option<char>,
    /// unit name resolved through the UNIT table
    pub unit: Option<String>,
    /// multiplier id char from FMTU; None when the log marks the field '-'
    pub mult_id: Option<char>,
    /// factor resolved through the MULT table
    pub multiplier: Option<f64>,
}

/// the log's UNIT/MULT/FMTU tables, built once per log
#[derive(Debug, Default)]
pub struct UnitsTable {
    /// unit id char -> unit name (UNIT records, last definition wins)
    pub unit_names: HashMap<char, String>,
    /// multiplier id char -> factor (MULT records, last definition wins)
    pub multipliers: HashMap<char, f64>,
    /// message type id -> per-field unit id chars (FMTU records)
    fmt_unit_ids: HashMap<u8, Vec<char>>,
    /// message type id -> per-field multiplier id chars (FMTU records)
    fmt_mult_ids: HashMap<u8, Vec<char>>,
}

impl UnitsTable {
    /// true when the log carries no units metadata at all
    pub fn is_empty(&self) -> bool {
        self.fmt_unit_ids.is_empty() && self.fmt_mult_ids.is_empty()
    }

    /// index (format order) of the type's instance field - the first field
    /// whose FMTU unit id is '#'; None when the type has no instances
    pub fn instance_field_index(&self, type_id: u8) -> Option<usize> {
        self.fmt_unit_ids
            .get(&type_id)?
            .iter()
            .position(|&c| c == '#')
    }

    /// metadata for field `index` (format order) of message type `type_id`;
    /// fields the log does not annotate come back all-None
    pub fn field_meta(&self, type_id: u8, index: usize) -> FieldMeta {
        let id_at = |ids: &HashMap<u8, Vec<char>>| {
            ids.get(&type_id)
                .and_then(|chars| chars.get(index))
                .copied()
                .filter(|&c| c != '-')
        };

        let unit_id = id_at(&self.fmt_unit_ids);
        let mult_id = id_at(&self.fmt_mult_ids);
        FieldMeta {
            unit_id,
            unit: unit_id.and_then(|c| self.unit_names.get(&c).cloned()),
            mult_id,
            multiplier: mult_id.and_then(|c| self.multipliers.get(&c).copied()),
        }
    }
}

fn id_char(value: Option<Value>) -> Option<char> {
    // UNIT/MULT ids are logged as 'b' (int8); anything outside ASCII is
    // not a usable id
    match value {
        Some(Value::I64(v)) if (0..=0x7F).contains(&v) => Some(v as u8 as char),
        _ => None,
    }
}

impl LogFile {
    /// build the units/multipliers tables from the log's UNIT, MULT and
    /// FMTU records; empty tables when the log predates units metadata
    pub fn units(&self) -> UnitsTable {
        let mut table = UnitsTable::default();

        for record in self.records_of(&["UNIT", "MULT", "FMTU"]) {
            match record.type_name() {
                "UNIT" => {
                    if let (Some(id), Some(Value::Str(label))) =
                        (id_char(record.value("Id")), record.value("Label"))
                    {
                        table.unit_names.insert(id, label);
                    }
                }
                "MULT" => {
                    if let (Some(id), Some(Value::F64(mult))) =
                        (id_char(record.value("Id")), record.value("Mult"))
                    {
                        table.multipliers.insert(id, mult);
                    }
                }
                _ => {
                    let fmt_type = match record.value("FmtType") {
                        Some(Value::U64(v)) if v <= 0xFF => v as u8,
                        _ => continue,
                    };
                    if let Some(Value::Str(ids)) = record.value("UnitIds") {
                        table.fmt_unit_ids.insert(fmt_type, ids.chars().collect());
                    }
                    if let Some(Value::Str(ids)) = record.value("MultIds") {
                        table.fmt_mult_ids.insert(fmt_type, ids.chars().collect());
                    }
                }
            }
        }

        table
    }

    /// convenience lookup: metadata for `field` of message `name` (first
    /// matching label, like the record accessors); None when the message
    /// or field is unknown
    pub fn field_meta(&self, name: &str, field: &str) -> Option<FieldMeta> {
        let &id = self.name_to_id.get(name)?;
        let fmt = self.fmts.get(&id)?;
        let index = fmt.labels.iter().position(|l| l == field)?;
        Some(self.units().field_meta(id, index))
    }

    /// label of the instance field of message `name` (e.g. "I" for IMU), or
    /// None when the message is unknown or has no instances. Builds the
    /// units table on each call - cache it for repeated lookups.
    pub fn instance_field(&self, name: &str) -> Option<String> {
        let &id = self.name_to_id.get(name)?;
        let fmt = self.fmts.get(&id)?;
        let index = self.units().instance_field_index(id)?;
        fmt.labels.get(index).cloned()
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

    /// values pinned from the ArduPilot-written tables in the SITL corpus
    #[test]
    fn known_fields_resolve_units_and_multipliers() {
        let log = corpus("copter.bin");

        let lat = log.field_meta("GPS", "Lat").expect("GPS.Lat");
        assert_eq!(lat.unit_id, Some('D'));
        assert_eq!(lat.unit.as_deref(), Some("deglatitude"));
        // ArduPilot logs MULT factors through a float cast; the table
        // reports exactly what the log carries
        assert_eq!(lat.mult_id, Some('G'));
        assert_eq!(lat.multiplier, Some(1e-7f32 as f64));

        let time_us = log.field_meta("ATT", "TimeUS").expect("ATT.TimeUS");
        assert_eq!(time_us.unit.as_deref(), Some("s"));
        assert_eq!(time_us.multiplier, Some(1e-6f32 as f64));
    }

    /// '-' in FMTU means "no annotation", not a table lookup
    #[test]
    fn dash_ids_resolve_to_none() {
        let log = corpus("copter.bin");
        let table = log.units();

        // FMT's own FMTU row is "-b---": only field 1 has a unit
        let fmt_id = log.name_to_id["FMT"];
        let type_field = table.field_meta(fmt_id, 0);
        assert_eq!(type_field.unit_id, None);
        assert_eq!(type_field.unit, None);
        assert_eq!(type_field.mult_id, None);
        assert_eq!(type_field.multiplier, None);
        assert_eq!(table.field_meta(fmt_id, 1).unit_id, Some('b'));
    }

    #[test]
    fn corpus_logs_all_carry_units_metadata() {
        for name in ["copter.bin", "plane.bin", "rover.bin", "copter-isbd.bin"] {
            let log = corpus(name);
            let table = log.units();
            assert!(!table.is_empty(), "{name}: no FMTU records");
            assert!(!table.unit_names.is_empty(), "{name}: no UNIT records");
            assert!(!table.multipliers.is_empty(), "{name}: no MULT records");

            // the autopilot's own convention rows
            assert_eq!(
                table.unit_names.get(&'d').map(String::as_str),
                Some("deg"),
                "{name}"
            );
            assert_eq!(table.multipliers.get(&'0').copied(), Some(1.0), "{name}");
        }
    }

    /// unknown messages, unknown fields and out-of-range indexes are safe
    #[test]
    fn missing_metadata_is_none_not_panic() {
        let log = corpus("copter.bin");
        assert!(log.field_meta("NOPE", "X").is_none());
        assert!(log.field_meta("GPS", "NoSuchField").is_none());

        let table = log.units();
        let meta = table.field_meta(0xFE, 99);
        assert_eq!(
            meta,
            FieldMeta {
                unit_id: None,
                unit: None,
                mult_id: None,
                multiplier: None
            }
        );

        let empty = LogFile::open_bytes(&[]).unwrap();
        assert!(empty.units().is_empty());
    }
}
