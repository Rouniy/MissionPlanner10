//! Text rendering of a dataflash log, byte-compatible with the C#
//! `BinaryLog.ConvertBin`.
//!
//! The reference is `BinaryLog.ReadMessage` running headless (no
//! `onFlightMode` subscriber, so `M` fields render as the raw mode number -
//! inside the full app the legacy path substitutes mode names, a documented
//! divergence). Every quirk here is intentional parity:
//!
//! - one pass with the same resync state machine as the index scan, learning
//!   FMT progressively; records of a type whose FMT has not been seen yet
//!   produce no output and are rescanned mid-payload
//! - numbers format like .NET's `IConvertible.ToString(InvariantCulture)`:
//!   integers plainly; float via legacy "G7", double (and the c/C/e/E/L
//!   scaled fields) via legacy "G15" - fixed notation unless the decimal
//!   exponent is < -4 or >= the precision, then `d.dddE+XX`
//! - `n`/`N` strings map non-ASCII bytes to '?' (ASCIIEncoding) and trim NULs
//! - `Z` additionally escapes backslash, \n, \r, \t and control chars as \xHH
//! - `a` renders as "[s0 s1 ... s31]"
//! - an unknown format char contributes an empty field and advances zero
//!   bytes, exactly like the C# `(null, 0)` decoder result

use std::io::{self, Write};

use crate::{FMT_PAYLOAD_LEN, FMT_TYPE, HEAD_BYTE1, HEAD_BYTE2};

#[derive(Debug)]
pub struct RenderStats {
    pub records: u64,
    pub dropped: u64,
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

/// lay out a trimmed significant-digit string under the .NET "G" rules:
/// fixed notation unless the decimal exponent is < -4 or >= the precision,
/// then d.dddE+XX with at least two exponent digits
#[expect(
    clippy::string_slice,
    reason = "`digits` is ASCII 0-9 by construction (extracted from `format!(\"{:.e}\")` output), so byte indexes are always char boundaries"
)]
fn layout_digits(negative: bool, digits: &str, exponent: i32, prec: usize) -> String {
    let mut out = String::new();
    if negative {
        out.push('-');
    }

    if exponent < -4 || exponent >= prec as i32 {
        out.push_str(&digits[..1]);
        if digits.len() > 1 {
            out.push('.');
            out.push_str(&digits[1..]);
        }
        out.push('E');
        if exponent < 0 {
            out.push('-');
        } else {
            out.push('+');
        }
        out.push_str(&format!("{:02}", exponent.abs()));
    } else if exponent >= 0 {
        let int_len = exponent as usize + 1;
        if digits.len() <= int_len {
            out.push_str(digits);
            out.push_str(&"0".repeat(int_len - digits.len()));
        } else {
            out.push_str(&digits[..int_len]);
            out.push('.');
            out.push_str(&digits[int_len..]);
        }
    } else {
        out.push_str("0.");
        out.push_str(&"0".repeat((-exponent - 1) as usize));
        out.push_str(digits);
    }

    out
}

/// mantissa/exponent of a Rust `{:e}` / `{:.*e}` string
fn parse_sci(formatted: &str) -> (bool, String, i32) {
    let (mantissa, exp_str) = formatted.split_once('e').expect("exponent");
    let exponent: i32 = exp_str.parse().expect("exponent value");
    let negative = mantissa.starts_with('-');
    let digits: String = mantissa.chars().filter(|c| c.is_ascii_digit()).collect();
    (negative, digits, exponent)
}

/// reference path: 60 correctly-rounded significant digits, then round the
/// digit string half AWAY FROM ZERO at `prec` - the legacy .NET Framework
/// behavior (a shortest conversion rounds ties to even instead; exact
/// midpoints such as the float 94502.125 must format as 94502.13). The
/// 60-digit margin makes the >=5 cutoff test exact for every double: a
/// non-tie double cannot sit within 1e-40 of a decimal midpoint.
pub(crate) fn format_significant_exact(value: f64, prec: usize) -> String {
    let (negative, digit_str, mut exponent) = parse_sci(&format!("{:.59e}", value));
    let mut digits: Vec<u8> = digit_str.bytes().map(|b| b - b'0').collect();

    if digits.len() > prec {
        let round_up = digits[prec] >= 5;
        digits.truncate(prec);
        if round_up {
            let mut i = prec;
            loop {
                if i == 0 {
                    digits.insert(0, 1);
                    digits.truncate(prec);
                    exponent += 1;
                    break;
                }
                i -= 1;
                if digits[i] == 9 {
                    digits[i] = 0;
                } else {
                    digits[i] += 1;
                    break;
                }
            }
        }
    }

    while digits.len() > 1 && *digits.last().unwrap() == 0 {
        digits.pop();
    }

    let digits: String = digits.iter().map(|&d| (d + b'0') as char).collect();
    layout_digits(negative, &digits, exponent, prec)
}

/// The shortest round-trip digits equal the legacy half-away rounding of the
/// exact expansion at `prec` digits only when the binary grid is finer than
/// half the final decimal digit: |v - S| <= ulp/2 must be strictly below
/// 0.5 * 10^(exp - prec + 1). This excludes subnormals (coarse grid, short
/// shortest, long true expansion), the marginal near-9.999999 mantissas, and
/// exact decimal ties (which sit a full half-unit away from any shorter S).
/// The 0.9 margin absorbs powi rounding.
fn shortest_is_exact(ulp: f64, exponent: i32, prec: usize) -> bool {
    ulp < 0.9 * 10f64.powi(exponent - prec as i32 + 1)
}

fn ulp_f64(value: f64) -> f64 {
    let bits = value.abs().to_bits();
    f64::from_bits(bits + 1) - f64::from_bits(bits)
}

fn ulp_f32(value: f32) -> f64 {
    let bits = value.abs().to_bits();
    (f32::from_bits(bits + 1) - f32::from_bits(bits)) as f64
}

/// legacy .NET Framework "G" format with `prec` significant digits (G15 for
/// double), invariant culture. Fast path: the shortest round-trip digits,
/// when they are provably the legacy output (see shortest_is_exact);
/// otherwise the 60-digit reference path. Verified equivalent by the
/// millions-of-bit-patterns property test below.
pub fn format_significant(value: f64, prec: usize) -> String {
    if value.is_nan() {
        return "NaN".into();
    }
    if value.is_infinite() {
        return if value > 0.0 {
            "Infinity".into()
        } else {
            "-Infinity".into()
        };
    }
    if value == 0.0 {
        return "0".into();
    }

    let (negative, digits, exponent) = parse_sci(&format!("{:e}", value));
    if digits.len() <= prec && shortest_is_exact(ulp_f64(value), exponent, prec) {
        return layout_digits(negative, &digits, exponent, prec);
    }

    format_significant_exact(value, prec)
}

/// the C# float path: G7 of the widened double. The fast test uses the f32
/// shortest representation and the f32 ulp - the f64 shortest of the same
/// value is ~17 digits and would never qualify.
fn format_significant_f32(value: f32) -> String {
    if value.is_nan() {
        return "NaN".into();
    }
    if value.is_infinite() {
        return if value > 0.0 {
            "Infinity".into()
        } else {
            "-Infinity".into()
        };
    }
    if value == 0.0 {
        return "0".into();
    }

    let (negative, digits, exponent) = parse_sci(&format!("{:e}", value));
    if digits.len() <= 7 && shortest_is_exact(ulp_f32(value), exponent, 7) {
        return layout_digits(negative, &digits, exponent, 7);
    }

    format_significant_exact(value as f64, 7)
}

/// ASCIIEncoding semantics: bytes > 0x7F become '?', then trim NULs
fn ascii_lossy_trim(bytes: &[u8]) -> String {
    let mapped: String = bytes
        .iter()
        .map(|&b| if b > 0x7F { '?' } else { b as char })
        .collect();
    mapped.trim_matches('\0').to_string()
}

/// None when the trimmed string is empty: the C# escape chain ends in a
/// seedless LINQ Aggregate, which throws on an empty sequence and drops the
/// whole record line
fn escape_z(bytes: &[u8]) -> Option<String> {
    let s = ascii_lossy_trim(bytes);
    let s = s.replace('\\', "\\\\");
    let s = s.replace('\n', "\\n");
    let s = s.replace('\r', "\\r");
    let s = s.replace('\t', "\\t");
    if s.is_empty() {
        return None;
    }
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        if (c as u32) < 32 || (c as u32) > 127 {
            out.push_str(&format!("\\x{:02X}", c as u32 as u8));
        } else {
            out.push(c);
        }
    }
    Some(out)
}

/// stack-only zero-padded field bytes (fields are at most 64 bytes)
fn zero_padded(payload: &[u8], at: usize, len: usize) -> [u8; 64] {
    let mut buf = [0u8; 64];
    if at < payload.len() {
        let take = len.min(payload.len() - at).min(64);
        buf[..take].copy_from_slice(&payload[at..at + take]);
    }
    buf
}

/// false = the whole record line must be dropped (empty-Z quirk)
fn render_field(code: u8, payload: &[u8], at: usize, out: &mut String) -> bool {
    use std::fmt::Write;

    let b = zero_padded(payload, at, field_size(code).max(1));
    match code {
        b'b' => write!(out, "{}", b[0] as i8).unwrap(),
        b'B' | b'M' => write!(out, "{}", b[0]).unwrap(),
        b'h' => write!(out, "{}", i16::from_le_bytes([b[0], b[1]])).unwrap(),
        b'H' => write!(out, "{}", u16::from_le_bytes([b[0], b[1]])).unwrap(),
        b'i' => write!(out, "{}", i32::from_le_bytes([b[0], b[1], b[2], b[3]])).unwrap(),
        b'I' => write!(out, "{}", u32::from_le_bytes([b[0], b[1], b[2], b[3]])).unwrap(),
        b'q' => write!(out, "{}", i64::from_le_bytes(b[..8].try_into().unwrap())).unwrap(),
        b'Q' => write!(out, "{}", u64::from_le_bytes(b[..8].try_into().unwrap())).unwrap(),
        b'f' => out.push_str(&format_significant_f32(f32::from_le_bytes([
            b[0], b[1], b[2], b[3],
        ]))),
        b'd' => out.push_str(&format_significant(
            f64::from_le_bytes(b[..8].try_into().unwrap()),
            15,
        )),
        b'g' => out.push_str(&format_significant_f32(crate::columns::half_to_f32(
            u16::from_le_bytes([b[0], b[1]]),
        ))),
        b'c' => out.push_str(&format_significant(
            i16::from_le_bytes([b[0], b[1]]) as f64 / 100.0,
            15,
        )),
        b'C' => out.push_str(&format_significant(
            u16::from_le_bytes([b[0], b[1]]) as f64 / 100.0,
            15,
        )),
        b'e' => out.push_str(&format_significant(
            i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0,
            15,
        )),
        b'E' => out.push_str(&format_significant(
            u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0,
            15,
        )),
        b'L' => out.push_str(&format_significant(
            i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 10000000.0,
            15,
        )),
        b'n' => out.push_str(&ascii_lossy_trim(&b[..4])),
        b'N' => out.push_str(&ascii_lossy_trim(&b[..16])),
        b'Z' => match escape_z(&b[..64]) {
            Some(s) => out.push_str(&s),
            None => return false,
        },
        b'a' => {
            out.push('[');
            for (i, pair) in b.as_chunks::<2>().0.iter().enumerate() {
                if i > 0 {
                    out.push(' ');
                }
                write!(out, "{}", i16::from_le_bytes(*pair)).unwrap();
            }
            out.push(']');
        }
        _ => {} // unknown code: empty field, zero bytes consumed
    }

    true
}

#[derive(Clone, Default)]
struct FmtEntry {
    length: usize,
    name: String,
    format: Vec<u8>,
}

/// One-pass conversion of a binary log to the ConvertBin text form.
pub fn convert<W: Write>(data: &[u8], out: &mut W) -> io::Result<RenderStats> {
    let mut fmts: Vec<FmtEntry> = vec![FmtEntry::default(); 256];
    let mut stats = RenderStats {
        records: 0,
        dropped: 0,
    };
    let len = data.len();
    let mut pos = 0usize;
    let mut line = String::new();

    let mut step = 0u8;
    while pos < len {
        let b = data[pos];
        pos += 1;
        match step {
            0 => {
                if b == HEAD_BYTE1 {
                    step = 1;
                }
            }
            1 => {
                step = if b == HEAD_BYTE2 { 2 } else { 0 };
            }
            _ => {
                step = 0;
                if b == FMT_TYPE {
                    let take = FMT_PAYLOAD_LEN.min(len - pos);
                    let mut payload = [0u8; FMT_PAYLOAD_LEN];
                    payload[..take].copy_from_slice(&data[pos..pos + take]);
                    pos += take;

                    let entry = FmtEntry {
                        length: payload[1] as usize,
                        name: ascii_lossy_trim(&payload[2..6]),
                        format: ascii_lossy_trim(&payload[6..22]).into_bytes(),
                    };
                    fmts[payload[0] as usize] = entry;

                    line.clear();
                    line.push_str("FMT, ");
                    line.push_str(&payload[0].to_string());
                    line.push_str(", ");
                    line.push_str(&payload[1].to_string());
                    line.push_str(", ");
                    line.push_str(&ascii_lossy_trim(&payload[2..6]));
                    line.push_str(", ");
                    line.push_str(&ascii_lossy_trim(&payload[6..22]));
                    line.push_str(", ");
                    line.push_str(&ascii_lossy_trim(&payload[22..86]));
                    line.push_str("\r\n");
                    out.write_all(line.as_bytes())?;
                    stats.records += 1;
                } else {
                    let fmt = &fmts[b as usize];
                    if fmt.length == 0 {
                        // unknown type: no output, rescan inside its payload
                        stats.dropped += 1;
                        continue;
                    }
                    if fmt.length < 3 {
                        // C# throws on new byte[size - 3]: record dropped
                        stats.dropped += 1;
                        continue;
                    }

                    let size = fmt.length - 3;
                    let take = size.min(len - pos);
                    let payload = &data[pos..pos + take];
                    pos += take;

                    line.clear();
                    line.push_str(&fmt.name);
                    let mut at = 0usize;
                    let mut ok = true;
                    for &code in &fmt.format {
                        line.push_str(", ");
                        if !render_field(code, payload, at, &mut line) {
                            ok = false;
                            break;
                        }
                        at += field_size(code);
                    }

                    if ok {
                        line.push_str("\r\n");
                        out.write_all(line.as_bytes())?;
                        stats.records += 1;
                    } else {
                        stats.dropped += 1;
                    }
                }
            }
        }
    }

    Ok(stats)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn g_format_basics() {
        assert_eq!(format_significant(0.0, 15), "0");
        assert_eq!(format_significant(1.0, 15), "1");
        assert_eq!(format_significant(-12.34, 15), "-12.34");
        assert_eq!(format_significant(0.1, 15), "0.1");
        assert_eq!(
            format_significant(1234567890123456.0, 15),
            "1.23456789012346E+15"
        );
        assert_eq!(format_significant(0.00001, 15), "1E-05");
        assert_eq!(format_significant(47.3566, 15), "47.3566");
        assert_eq!(format_significant(f64::NAN, 15), "NaN");
        assert_eq!(format_significant(-0.0, 15), "0");
    }

    #[test]
    fn g7_float_basics() {
        assert_eq!(format_significant_f32(0.0), "0");
        assert_eq!(format_significant_f32(1.5), "1.5");
        assert_eq!(format_significant_f32(1.2345678), "1.234568");
        assert_eq!(format_significant_f32(12345678.0), "1.234568E+07");
        assert_eq!(format_significant_f32(-0.001), "-0.001");
        assert_eq!(format_significant_f32(0.00001), "1E-05");
        // exact decimal midpoint: legacy rounds half away from zero
        assert_eq!(format_significant_f32(94502.125), "94502.13");
        assert_eq!(format_significant_f32(-94502.125), "-94502.13");
    }

    /// the fast (shortest-first) path must agree with the 60-digit reference
    /// for arbitrary bit patterns and for constructed near-tie values
    #[test]
    fn fast_path_matches_exact_path() {
        let mut x: u64 = 0x243F6A8885A308D3;
        let mut next = move || {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            x.wrapping_mul(0x2545F4914F6CDD1D)
        };

        for _ in 0..500_000u32 {
            let bits = next();
            let d = f64::from_bits(bits);
            if d.is_finite() && d != 0.0 {
                assert_eq!(
                    format_significant(d, 15),
                    format_significant_exact(d, 15),
                    "f64 {bits:#x}"
                );
            }

            let fbits = bits as u32;
            let f = f32::from_bits(fbits);
            if f.is_finite() && f != 0.0 {
                assert_eq!(
                    format_significant_f32(f),
                    format_significant_exact(f as f64, 7),
                    "f32 {fbits:#x}"
                );
            }
        }

        // scaled-field shapes (int/100) and decimal midpoints at the cutoff
        for i in (i32::MIN..i32::MAX).step_by(9_999_991) {
            let d = i as f64 / 100.0;
            if d != 0.0 {
                assert_eq!(
                    format_significant(d, 15),
                    format_significant_exact(d, 15),
                    "scaled {i}"
                );
            }
        }

        for mid in [94502.125f32, 0.15625, 1.5, 2.5e-7, 123456.75, -94502.125] {
            assert_eq!(
                format_significant_f32(mid),
                format_significant_exact(mid as f64, 7),
                "midpoint {mid}"
            );
        }
    }

    #[test]
    fn z_escaping() {
        let mut bytes = [0u8; 64];
        bytes[..7].copy_from_slice(b"a\\b\nc\td");
        assert_eq!(escape_z(&bytes).as_deref(), Some("a\\\\b\\nc\\td"));
        let mut ctrl = [0u8; 64];
        ctrl[0] = b'x';
        ctrl[1] = 0x1B;
        ctrl[2] = b'y';
        assert_eq!(escape_z(&ctrl).as_deref(), Some("x\\x1By"));
        // empty after trim: the record line is dropped (C# Aggregate quirk)
        assert_eq!(escape_z(&[0u8; 64]), None);
    }
}
