//! Typed columnar extraction by the native dflog library.
//!
//! Decodes all records of one message type into `f64` columns for the
//! requested field labels, plus the global record index ("line number") per
//! row so callers can align with the existing DFLogBuffer numbering.
//!
//! Semantics mirror the C# `BinaryLog.GetObjectFromMessage` decoders:
//! little-endian primitives; `c`/`C`/`e`/`E` scaled by 1/100; `L` by 1e-7;
//! `g` is an IEEE half; `M` is the raw mode byte; a record whose payload runs
//! past end-of-file decodes the missing bytes as zero, exactly like the C#
//! partial `Stream.Read` into a zeroed buffer.
//!
//! Precision note (deliberate, documented in the plan): these are the *raw*
//! decoded values. The legacy graphing path round-trips through 7-significant
//! -digit strings, so it loses float precision that this path keeps.

use crate::LogFile;

#[derive(Debug)]
pub enum ColumnError {
    UnknownType(String),
    UnknownField {
        field: String,
        available: String,
    },
    /// field exists but has no numeric decoding (n/N/Z strings, `a` arrays)
    NotNumeric {
        field: String,
        code: char,
    },
    /// field is not an `a` (int16[32]) array
    NotArray {
        field: String,
        code: char,
    },
    /// FMT format/labels column counts disagree; no stable field mapping
    MalformedFormat(String),
    /// an instance filter was requested for a type without an instance
    /// field (no '#' unit id in its FMTU)
    NoInstanceField(String),
}

impl std::fmt::Display for ColumnError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ColumnError::UnknownType(t) => write!(f, "unknown message type {t}"),
            ColumnError::UnknownField { field, available } => {
                write!(f, "unknown field {field}; available: {available}")
            }
            ColumnError::NotNumeric { field, code } => {
                write!(f, "field {field} (format '{code}') is not numeric")
            }
            ColumnError::NotArray { field, code } => {
                write!(f, "field {field} (format '{code}') is not an int16 array")
            }
            ColumnError::MalformedFormat(t) => write!(f, "malformed FMT for {t}"),
            ColumnError::NoInstanceField(t) => {
                write!(f, "message type {t} has no instance field")
            }
        }
    }
}

impl std::error::Error for ColumnError {}

/// Column-major result: `values[col * rows + row]`.
#[derive(Debug)]
pub struct Columns {
    pub rows: u64,
    pub cols: u32,
    pub linenos: Vec<u64>,
    pub values: Vec<f64>,
}

fn field_size(code: char) -> Option<usize> {
    Some(match code {
        'b' | 'B' | 'M' => 1,
        'h' | 'H' | 'c' | 'C' | 'g' => 2,
        'i' | 'I' | 'e' | 'E' | 'L' | 'f' => 4,
        'q' | 'Q' | 'd' => 8,
        'n' => 4,
        'N' => 16,
        'Z' => 64,
        'a' => 64,
        _ => return None,
    })
}

pub(crate) fn half_to_f32(bits: u16) -> f32 {
    // IEEE 754 binary16 -> binary32
    let sign = ((bits >> 15) & 1) as u32;
    let exp = ((bits >> 10) & 0x1F) as u32;
    let frac = (bits & 0x3FF) as u32;
    let out = match exp {
        0 => {
            if frac == 0 {
                sign << 31
            } else {
                // subnormal: normalize
                let mut e = 127 - 15 + 1;
                let mut f = frac;
                while f & 0x400 == 0 {
                    f <<= 1;
                    e -= 1;
                }
                (sign << 31) | ((e as u32) << 23) | ((f & 0x3FF) << 13)
            }
        }
        0x1F => (sign << 31) | 0x7F80_0000 | (frac << 13),
        _ => (sign << 31) | ((exp + 127 - 15) << 23) | (frac << 13),
    };
    f32::from_bits(out)
}

fn read_zero_padded(data: &[u8], start: usize, len: usize) -> [u8; 8] {
    let mut buf = [0u8; 8];
    if start < data.len() {
        let take = len.min(data.len() - start).min(8);
        buf[..take].copy_from_slice(&data[start..start + take]);
    }
    buf
}

fn decode(code: char, data: &[u8], at: usize) -> f64 {
    let b = read_zero_padded(data, at, field_size(code).unwrap_or(0));
    match code {
        'b' => (b[0] as i8) as f64,
        'B' | 'M' => b[0] as f64,
        'h' => i16::from_le_bytes([b[0], b[1]]) as f64,
        'H' => u16::from_le_bytes([b[0], b[1]]) as f64,
        'i' => i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64,
        'I' => u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64,
        'q' => i64::from_le_bytes(b) as f64,
        'Q' => u64::from_le_bytes(b) as f64,
        'f' => f32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64,
        'd' => f64::from_le_bytes(b),
        'g' => half_to_f32(u16::from_le_bytes([b[0], b[1]])) as f64,
        'c' => i16::from_le_bytes([b[0], b[1]]) as f64 / 100.0,
        'C' => u16::from_le_bytes([b[0], b[1]]) as f64 / 100.0,
        'e' => i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0,
        'E' => u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0,
        'L' => i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 10000000.0,
        _ => f64::NAN,
    }
}

/// Row linenos of `id` records, optionally limited to one instance value
/// (the field whose FMTU unit id is '#', compared on its decoded value).
fn collect_rows(
    log: &LogFile,
    type_name: &str,
    id: u8,
    codes: &[char],
    instance: Option<i64>,
) -> Result<Vec<u64>, ColumnError> {
    let instance_at = match instance {
        None => None,
        Some(wanted) => {
            let index = log
                .units()
                .instance_field_index(id)
                .filter(|&index| index < codes.len())
                .ok_or_else(|| ColumnError::NoInstanceField(type_name.into()))?;
            let code = codes[index];
            if field_size(code).is_none() || matches!(code, 'n' | 'N' | 'Z' | 'a') {
                return Err(ColumnError::NoInstanceField(type_name.into()));
            }
            let offset: usize = codes[..index]
                .iter()
                .map(|&c| field_size(c).unwrap_or(0))
                .sum();
            Some((offset, code, wanted as f64))
        }
    };

    let data = log.data();
    let mut linenos = Vec::new();
    for (i, &t) in log.index.types.iter().enumerate() {
        if t != id {
            continue;
        }
        if let Some((offset, code, wanted)) = instance_at {
            let payload = log.index.offsets[i] as usize + 3;
            if decode(code, data, payload + offset) != wanted {
                continue;
            }
        }
        linenos.push(i as u64);
    }
    Ok(linenos)
}

/// Decode `fields` of every `type_name` record in the log.
pub fn get_columns(
    log: &LogFile,
    type_name: &str,
    fields: &[&str],
) -> Result<Columns, ColumnError> {
    get_columns_filtered(log, type_name, fields, None)
}

/// Decode `fields` of `type_name` records, limited to one `instance` value
/// when given (e.g. IMU instance 1); the whole log's records when None.
pub fn get_columns_filtered(
    log: &LogFile,
    type_name: &str,
    fields: &[&str],
    instance: Option<i64>,
) -> Result<Columns, ColumnError> {
    let &id = log
        .name_to_id
        .get(type_name)
        .ok_or_else(|| ColumnError::UnknownType(type_name.into()))?;
    let fmt = log
        .fmts
        .get(&id)
        .ok_or_else(|| ColumnError::UnknownType(type_name.into()))?;

    let codes: Vec<char> = fmt.format.chars().collect();
    if codes.len() != fmt.labels.len() {
        return Err(ColumnError::MalformedFormat(type_name.into()));
    }

    // label -> (payload byte offset, format code); first match wins like the
    // C# FindMessageOffset lookup
    let mut offsets = Vec::with_capacity(fields.len());
    for &field in fields {
        let pos = fmt.labels.iter().position(|l| l == field).ok_or_else(|| {
            ColumnError::UnknownField {
                field: field.into(),
                available: fmt.labels.join(","),
            }
        })?;
        let code = codes[pos];
        if field_size(code).is_none() || matches!(code, 'n' | 'N' | 'Z' | 'a') {
            return Err(ColumnError::NotNumeric {
                field: field.into(),
                code,
            });
        }
        let offset: usize = codes[..pos]
            .iter()
            .map(|&c| field_size(c).unwrap_or(0))
            .sum();
        offsets.push((offset, code));
    }

    let data = log.data();
    let linenos = collect_rows(log, type_name, id, &codes, instance)?;

    let rows = linenos.len();
    let mut values = vec![0f64; rows * fields.len()];
    for (col, &(field_offset, code)) in offsets.iter().enumerate() {
        let out = &mut values[col * rows..(col + 1) * rows];
        for (row, &lineno) in linenos.iter().enumerate() {
            let payload = log.index.offsets[lineno as usize] as usize + 3;
            out[row] = decode(code, data, payload + field_offset);
        }
    }

    Ok(Columns {
        rows: rows as u64,
        cols: fields.len() as u32,
        linenos,
        values,
    })
}

/// Elements per `a`-format field: int16_t[32].
pub const ARRAY_ELEMS: usize = 32;

/// Row-major result: `values[row * ARRAY_ELEMS + elem]`.
#[derive(Debug)]
pub struct ArrayColumn {
    pub rows: u64,
    pub linenos: Vec<u64>,
    pub values: Vec<i16>,
}

/// Decode the `a` (int16[32]) array `field` of every `type_name` record.
/// Bytes past end-of-file decode as zero, matching the C# partial read into
/// a zeroed buffer that backs `BinaryLog.UnionArray`.
pub fn get_array_column(
    log: &LogFile,
    type_name: &str,
    field: &str,
) -> Result<ArrayColumn, ColumnError> {
    get_array_column_filtered(log, type_name, field, None)
}

/// `get_array_column` limited to one `instance` value when given.
pub fn get_array_column_filtered(
    log: &LogFile,
    type_name: &str,
    field: &str,
    instance: Option<i64>,
) -> Result<ArrayColumn, ColumnError> {
    let &id = log
        .name_to_id
        .get(type_name)
        .ok_or_else(|| ColumnError::UnknownType(type_name.into()))?;
    let fmt = log
        .fmts
        .get(&id)
        .ok_or_else(|| ColumnError::UnknownType(type_name.into()))?;

    let codes: Vec<char> = fmt.format.chars().collect();
    if codes.len() != fmt.labels.len() {
        return Err(ColumnError::MalformedFormat(type_name.into()));
    }

    let pos =
        fmt.labels
            .iter()
            .position(|l| l == field)
            .ok_or_else(|| ColumnError::UnknownField {
                field: field.into(),
                available: fmt.labels.join(","),
            })?;
    if codes[pos] != 'a' {
        return Err(ColumnError::NotArray {
            field: field.into(),
            code: codes[pos],
        });
    }

    let field_offset: usize = codes[..pos]
        .iter()
        .map(|&c| field_size(c).unwrap_or(0))
        .sum();

    let data = log.data();
    let linenos = collect_rows(log, type_name, id, &codes, instance)?;

    let rows = linenos.len();
    let mut values = vec![0i16; rows * ARRAY_ELEMS];
    for (row, &lineno) in linenos.iter().enumerate() {
        let start = log.index.offsets[lineno as usize] as usize + 3 + field_offset;
        let out = &mut values[row * ARRAY_ELEMS..(row + 1) * ARRAY_ELEMS];
        for (e, slot) in out.iter_mut().enumerate() {
            let at = start + e * 2;
            let b = read_zero_padded(data, at, 2);
            *slot = i16::from_le_bytes([b[0], b[1]]);
        }
    }

    Ok(ArrayColumn {
        rows: rows as u64,
        linenos,
        values,
    })
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

    /// per-instance extractions must partition the unfiltered rows exactly
    #[test]
    fn instance_filter_partitions_rows() {
        let log = corpus("copter.bin");
        let all = get_columns(&log, "IMU", &["I", "GyrX"]).unwrap();
        let rows = all.rows as usize;
        assert!(rows > 0);

        let mut instances: Vec<i64> = all.values[..rows].iter().map(|&v| v as i64).collect();
        instances.sort_unstable();
        instances.dedup();
        assert!(
            instances.len() > 1,
            "corpus IMU should have multiple instances"
        );

        let mut filtered_total = 0u64;
        let mut seen_linenos = Vec::new();
        for &instance in &instances {
            let one = get_columns_filtered(&log, "IMU", &["I", "GyrX"], Some(instance)).unwrap();
            let one_rows = one.rows as usize;
            // every kept row carries the requested instance value
            assert!(one.values[..one_rows].iter().all(|&v| v as i64 == instance));
            filtered_total += one.rows;
            seen_linenos.extend_from_slice(&one.linenos);
        }

        assert_eq!(filtered_total, all.rows);
        seen_linenos.sort_unstable();
        assert_eq!(seen_linenos, all.linenos);
    }

    #[test]
    fn instance_filter_on_type_without_instances_errors() {
        let log = corpus("copter.bin");
        // ATT has no '#' unit id in its FMTU
        assert!(matches!(
            get_columns_filtered(&log, "ATT", &["Roll"], Some(0)),
            Err(ColumnError::NoInstanceField(_))
        ));
        // an absent instance value filters to zero rows, not an error
        let none = get_columns_filtered(&log, "IMU", &["GyrX"], Some(99)).unwrap();
        assert_eq!(none.rows, 0);
    }

    #[test]
    fn instance_field_resolution() {
        let log = corpus("copter.bin");
        assert_eq!(log.instance_field("IMU").as_deref(), Some("I"));
        assert_eq!(log.instance_field("GPS").as_deref(), Some("I"));
        assert_eq!(log.instance_field("ATT"), None);
        assert_eq!(log.instance_field("NOPE"), None);
    }

    #[test]
    fn half_conversion_basics() {
        assert_eq!(half_to_f32(0x0000), 0.0);
        assert_eq!(half_to_f32(0x3C00), 1.0);
        assert_eq!(half_to_f32(0xC000), -2.0);
        assert_eq!(half_to_f32(0x7BFF), 65504.0);
        assert!(half_to_f32(0x7C00).is_infinite());
    }

    #[test]
    fn decodes_int16_array_field() {
        // synthetic ISBD-like type 0xAB "ISB" format "Ha" labels "N,x":
        // one record with N=7 and x = [0,1,2,...,31] plus a second record
        // whose array is cut off by end-of-file (zero-padded tail)
        let mut data = vec![0xA3, 0x95, 0x80];
        let mut fmt = vec![0u8; 86];
        fmt[0] = 0xAB;
        fmt[1] = (3 + 2 + 64) as u8;
        fmt[2..5].copy_from_slice(b"ISB");
        fmt[6..8].copy_from_slice(b"Ha");
        fmt[22..25].copy_from_slice(b"N,x");
        data.extend_from_slice(&fmt);

        data.extend_from_slice(&[0xA3, 0x95, 0xAB]);
        data.extend_from_slice(&7u16.to_le_bytes());
        for v in 0..32i16 {
            data.extend_from_slice(&v.to_le_bytes());
        }

        data.extend_from_slice(&[0xA3, 0x95, 0xAB]);
        data.extend_from_slice(&8u16.to_le_bytes());
        for v in 0..5i16 {
            data.extend_from_slice(&(100 + v).to_le_bytes());
        }
        // eof mid-array

        let dir = std::env::temp_dir().join(format!("dflog-arr-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("isb.bin");
        std::fs::write(&path, &data).unwrap();

        let log = LogFile::open(&path).unwrap();
        let col = get_array_column(&log, "ISB", "x").unwrap();
        assert_eq!(col.rows, 2);
        assert_eq!(col.linenos, vec![1, 2]);
        assert_eq!(&col.values[..32], (0..32i16).collect::<Vec<_>>().as_slice());
        assert_eq!(&col.values[32..37], &[100, 101, 102, 103, 104]);
        assert!(col.values[37..].iter().all(|&v| v == 0));

        assert!(matches!(
            get_array_column(&log, "ISB", "N"),
            Err(ColumnError::NotArray { .. })
        ));

        drop(log);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn decodes_scaled_and_primitive_fields() {
        // synthetic log: one FMT for type 0xAA "TST" format "chL" labels "A,B,C"
        // then one TST record: c=-1234 (=-12.34), h=1000, L=473566000 (=47.3566)
        let mut data = vec![0xA3, 0x95, 0x80];
        let mut fmt = vec![0u8; 86];
        fmt[0] = 0xAA;
        fmt[1] = 3 + 2 + 2 + 4;
        fmt[2..5].copy_from_slice(b"TST");
        fmt[6..9].copy_from_slice(b"chL");
        fmt[22..27].copy_from_slice(b"A,B,C");
        data.extend_from_slice(&fmt);
        data.extend_from_slice(&[0xA3, 0x95, 0xAA]);
        data.extend_from_slice(&(-1234i16).to_le_bytes());
        data.extend_from_slice(&1000i16.to_le_bytes());
        data.extend_from_slice(&473566000i32.to_le_bytes());

        let dir = std::env::temp_dir().join(format!("dflog-col-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("tst.bin");
        std::fs::write(&path, &data).unwrap();

        let log = LogFile::open(&path).unwrap();
        let cols = get_columns(&log, "TST", &["A", "B", "C"]).unwrap();
        assert_eq!(cols.rows, 1);
        assert_eq!(cols.linenos, vec![1]);
        assert_eq!(cols.values, vec![-12.34, 1000.0, 47.3566]);

        drop(log);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
