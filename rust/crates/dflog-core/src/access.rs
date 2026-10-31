//! General-purpose typed record access - the DFReader-style layer for
//! external consumers (Python bindings, exporters). Lives BESIDE the Mission
//! Planner parity surfaces (columns, render), not inside them: string fields
//! here are plain trimmed text (no Z escaping), M is the raw mode number,
//! and scaled fields use the same legacy scaling as `columns` (c/C/e/E per
//! cent, L degrees). Truncated payloads decode missing bytes as zero, like
//! every other layer.

use crate::columns::half_to_f32;
use crate::{FmtDef, LogFile};

/// one decoded field value
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    I64(i64),
    U64(u64),
    F64(f64),
    Str(String),
    /// `a` format: int16[32] sample block
    Shorts(Vec<i16>),
}

impl Value {
    /// numeric view; integers convert like an `as f64` cast
    pub fn as_f64(&self) -> Option<f64> {
        match self {
            Value::I64(v) => Some(*v as f64),
            Value::U64(v) => Some(*v as f64),
            Value::F64(v) => Some(*v),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Value::Str(s) => Some(s),
            _ => None,
        }
    }
}

fn field_size(code: u8) -> usize {
    match code {
        b'b' | b'B' | b'M' => 1,
        b'h' | b'H' | b'c' | b'C' | b'g' => 2,
        b'i' | b'I' | b'e' | b'E' | b'L' | b'f' => 4,
        b'q' | b'Q' | b'd' => 8,
        b'n' => 4,
        b'N' => 16,
        b'Z' => 64,
        b'a' => 64,
        _ => 0,
    }
}

fn zero_padded64(payload: &[u8], at: usize) -> [u8; 64] {
    let mut buf = [0u8; 64];
    if at < payload.len() {
        let take = (payload.len() - at).min(64);
        buf[..take].copy_from_slice(&payload[at..at + take]);
    }
    buf
}

fn ascii_lossy_trim(bytes: &[u8]) -> String {
    let mapped: String = bytes
        .iter()
        .map(|&b| if b > 0x7F { '?' } else { b as char })
        .collect();
    mapped.trim_matches('\0').to_string()
}

/// None for format chars with no known decoding
fn decode_value(code: u8, payload: &[u8], at: usize) -> Option<Value> {
    let b = zero_padded64(payload, at);
    Some(match code {
        b'b' => Value::I64((b[0] as i8) as i64),
        b'B' | b'M' => Value::U64(b[0] as u64),
        b'h' => Value::I64(i16::from_le_bytes([b[0], b[1]]) as i64),
        b'H' => Value::U64(u16::from_le_bytes([b[0], b[1]]) as u64),
        b'i' => Value::I64(i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as i64),
        b'I' => Value::U64(u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as u64),
        b'q' => Value::I64(i64::from_le_bytes(b[..8].try_into().unwrap())),
        b'Q' => Value::U64(u64::from_le_bytes(b[..8].try_into().unwrap())),
        b'f' => Value::F64(f32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64),
        b'd' => Value::F64(f64::from_le_bytes(b[..8].try_into().unwrap())),
        b'g' => Value::F64(half_to_f32(u16::from_le_bytes([b[0], b[1]])) as f64),
        b'c' => Value::F64(i16::from_le_bytes([b[0], b[1]]) as f64 / 100.0),
        b'C' => Value::F64(u16::from_le_bytes([b[0], b[1]]) as f64 / 100.0),
        b'e' => Value::F64(i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0),
        b'E' => Value::F64(u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0),
        b'L' => Value::F64(i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 10000000.0),
        b'n' => Value::Str(ascii_lossy_trim(&b[..4])),
        b'N' => Value::Str(ascii_lossy_trim(&b[..16])),
        b'Z' => Value::Str(ascii_lossy_trim(&b[..64])),
        b'a' => Value::Shorts(
            b.as_chunks::<2>()
                .0
                .iter()
                .map(|p| i16::from_le_bytes(*p))
                .collect(),
        ),
        _ => return None,
    })
}

/// one record, decoded lazily field by field
#[derive(Debug)]
pub struct Record<'a> {
    pub lineno: u64,
    pub fmt: &'a FmtDef,
    payload: &'a [u8],
}

impl<'a> Record<'a> {
    pub fn type_name(&self) -> &str {
        &self.fmt.name
    }

    /// decoded value of the named field (first matching label, like the
    /// managed FindMessageOffset); None for unknown labels and undecodable
    /// format chars
    pub fn value(&self, field: &str) -> Option<Value> {
        let codes = self.fmt.format.as_bytes();
        let pos = self.fmt.labels.iter().position(|l| l == field)?;
        if pos >= codes.len() {
            return None;
        }
        let at: usize = codes[..pos].iter().map(|&c| field_size(c)).sum();
        decode_value(codes[pos], self.payload, at)
    }

    /// (label, value) pairs for every decodable field, in format order
    pub fn values(&self) -> Vec<(&str, Value)> {
        let codes = self.fmt.format.as_bytes();
        let mut out = Vec::with_capacity(codes.len());
        let mut at = 0usize;
        for (pos, &code) in codes.iter().enumerate() {
            if let (Some(label), Some(value)) = (
                self.fmt.labels.get(pos),
                decode_value(code, self.payload, at),
            ) {
                out.push((label.as_str(), value));
            }
            at += field_size(code);
        }
        out
    }
}

/// records in log order; `type_filter` limits to those type ids
#[derive(Debug)]
pub struct RecordIter<'a> {
    log: &'a LogFile,
    pos: usize,
    type_filter: Option<[bool; 256]>,
}

impl<'a> Iterator for RecordIter<'a> {
    type Item = Record<'a>;

    fn next(&mut self) -> Option<Record<'a>> {
        while self.pos < self.log.index.types.len() {
            let i = self.pos;
            self.pos += 1;
            if let Some(filter) = &self.type_filter {
                if !filter[self.log.index.types[i] as usize] {
                    continue;
                }
            }
            if let Some(record) = self.log.record_at(i) {
                return Some(record);
            }
            // record of a type with no FMT: not decodable
        }
        None
    }
}

impl LogFile {
    /// the decodable record at position `i` of the scan index; None when
    /// out of range or the record's type has no FMT
    pub fn record_at(&self, i: usize) -> Option<Record<'_>> {
        let t = *self.index.types.get(i)?;
        let fmt = self.fmts.get(&t)?;
        let data = self.data();
        let start = self.index.offsets[i] as usize + 3;
        let size = fmt.length.saturating_sub(3);
        let end = (start + size).min(data.len());
        let payload = if start < data.len() {
            &data[start..end]
        } else {
            &[]
        };
        Some(Record {
            lineno: i as u64,
            fmt,
            payload,
        })
    }

    /// every decodable record in log order
    pub fn records(&self) -> RecordIter<'_> {
        RecordIter {
            log: self,
            pos: 0,
            type_filter: None,
        }
    }

    /// records of the named types, in log order; unknown names are ignored
    pub fn records_of(&self, types: &[&str]) -> RecordIter<'_> {
        let mut filter = [false; 256];
        for name in types {
            if let Some(&id) = self.name_to_id.get(*name) {
                filter[id as usize] = true;
            }
        }
        RecordIter {
            log: self,
            pos: 0,
            type_filter: Some(filter),
        }
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

    /// record values must agree with the columnar decoders row for row
    #[test]
    fn records_agree_with_columns() {
        let log = corpus("copter.bin");
        let cols = crate::columns::get_columns(&log, "ATT", &["TimeUS", "Roll", "Pitch"]).unwrap();

        let mut row = 0usize;
        for record in log.records_of(&["ATT"]) {
            assert_eq!(record.lineno, cols.linenos[row]);
            let rows = cols.rows as usize;
            assert_eq!(
                record.value("TimeUS").unwrap().as_f64().unwrap(),
                cols.values[row]
            );
            assert_eq!(
                record.value("Roll").unwrap().as_f64().unwrap(),
                cols.values[rows + row]
            );
            assert_eq!(
                record.value("Pitch").unwrap().as_f64().unwrap(),
                cols.values[2 * rows + row]
            );
            row += 1;
        }

        assert_eq!(row as u64, cols.rows);
    }

    #[test]
    fn string_fields_decode_as_text() {
        let log = corpus("copter.bin");
        let first_msg = log.records_of(&["MSG"]).next().expect("MSG record");
        let text = first_msg.value("Message").unwrap();
        let text = text.as_str().unwrap();
        assert!(
            text.starts_with("ArduCopter"),
            "unexpected MSG text: {text}"
        );

        // PARM names are 'N' strings
        let first_parm = log.records_of(&["PARM"]).next().expect("PARM record");
        assert!(!first_parm
            .value("Name")
            .unwrap()
            .as_str()
            .unwrap()
            .is_empty());
    }

    #[test]
    fn full_iteration_covers_all_known_records() {
        let log = corpus("copter.bin");
        let known: usize = log
            .index
            .types
            .iter()
            .filter(|t| log.fmts.contains_key(t))
            .count();
        assert_eq!(log.records().count(), known);

        // every record decodes every field without panicking
        for record in log.records() {
            let _ = record.values();
        }
    }

    /// random access must see exactly what iteration sees
    #[test]
    fn record_at_matches_iteration() {
        let log = corpus("copter.bin");
        for record in log.records_of(&["ATT"]).take(50) {
            let direct = log.record_at(record.lineno as usize).expect("record_at");
            assert_eq!(direct.type_name(), record.type_name());
            assert_eq!(direct.values(), record.values());
        }
        assert!(log.record_at(usize::MAX).is_none());
    }
}
