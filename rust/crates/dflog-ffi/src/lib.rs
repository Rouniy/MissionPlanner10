//! C ABI over dflog-core for Mission Planner's P/Invoke bindings
//! (ExtLibs/Utilities/DFLogNative.cs).
//!
//! Contract:
//! - Every function returns 0 on success or a negative error code; call
//!   `dflog_last_error` for a UTF-8 message describing the last failure on
//!   the calling thread.
//! - `dflog_scan_file` allocates a `DflogIndex`; release it with
//!   `dflog_index_free`. The `offsets`/`types` pointers stay valid until then.
//! - Panics never cross the boundary: they convert to `DFLOG_ERR_PANIC`.

use std::cell::RefCell;
use std::ffi::CStr;
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::ptr;

pub const DFLOG_OK: i32 = 0;
pub const DFLOG_ERR_BAD_ARGUMENT: i32 = -1;
pub const DFLOG_ERR_IO: i32 = -2;
pub const DFLOG_ERR_PANIC: i32 = -3;

/// Bumped when the ABI changes shape; checked by the C# side.
pub const DFLOG_ABI_VERSION: u32 = 5;

pub const DFLOG_ERR_NO_TIME_BASE: i32 = -5;

pub const DFLOG_ERR_QUERY: i32 = -4;

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_last_error(message: String) {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = message);
}

#[repr(C)]
#[derive(Debug)]
pub struct DflogIndex {
    pub count: u64,
    pub offsets: *const u64,
    pub types: *const u8,
    // owned storage the raw pointers refer to; not part of the C layout
    // contract - the C side must treat this struct as opaque beyond `types`
    offsets_vec: Vec<u64>,
    types_vec: Vec<u8>,
}

#[no_mangle]
pub extern "C" fn dflog_abi_version() -> u32 {
    DFLOG_ABI_VERSION
}

/// Scan the dataflash log at `path_utf8` and return its record index.
///
/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string and `out` a valid
/// pointer to receive the index.
#[no_mangle]
pub unsafe extern "C" fn dflog_scan_file(
    path_utf8: *const c_char,
    out: *mut *mut DflogIndex,
) -> i32 {
    if path_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: `path_utf8` is a non-null NUL-terminated string per the
    // caller contract
    let path = match unsafe { CStr::from_ptr(path_utf8) }.to_str() {
        Ok(s) => PathBuf::from(s),
        Err(_) => {
            set_last_error("path is not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    let result = catch_unwind(AssertUnwindSafe(|| dflog_core::scan_file(&path)));

    match result {
        Ok(Ok(index)) => {
            let mut boxed = Box::new(DflogIndex {
                count: index.offsets.len() as u64,
                offsets: ptr::null(),
                types: ptr::null(),
                offsets_vec: index.offsets,
                types_vec: index.types,
            });
            boxed.offsets = boxed.offsets_vec.as_ptr();
            boxed.types = boxed.types_vec.as_ptr();
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(boxed) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(format!("{}: {}", path.display(), err));
            DFLOG_ERR_IO
        }
        Err(_) => {
            set_last_error("panic in dflog_scan_file".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release an index returned by `dflog_scan_file`.
///
/// # Safety
/// `index` must be a pointer previously returned via `dflog_scan_file`, and
/// must not be used after this call. Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_index_free(index: *mut DflogIndex) {
    if !index.is_null() {
        // SAFETY: `index` came from Box::into_raw in dflog_scan_file and is
        // not used again per the caller contract
        drop(unsafe { Box::from_raw(index) });
    }
}

/// An open log kept resident for typed column queries (phase B).
#[derive(Debug)]
pub struct DflogFile {
    log: dflog_core::LogFile,
}

/// Column-major query result: `values[col * rows + row]`.
#[repr(C)]
#[derive(Debug)]
pub struct DflogColumns {
    pub rows: u64,
    pub cols: u32,
    pub linenos: *const u64,
    pub values: *const f64,
    // rust-owned storage; opaque to the C side beyond `values`
    linenos_vec: Vec<u64>,
    values_vec: Vec<f64>,
}

/// Open the log at `path_utf8` for column queries; release with `dflog_close`.
///
/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string and `out` a valid
/// pointer to receive the handle.
#[no_mangle]
pub unsafe extern "C" fn dflog_open(path_utf8: *const c_char, out: *mut *mut DflogFile) -> i32 {
    if path_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: `path_utf8` is a non-null NUL-terminated string per the
    // caller contract
    let path = match unsafe { CStr::from_ptr(path_utf8) }.to_str() {
        Ok(s) => PathBuf::from(s),
        Err(_) => {
            set_last_error("path is not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    match catch_unwind(AssertUnwindSafe(|| dflog_core::LogFile::open(&path))) {
        Ok(Ok(log)) => {
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(Box::new(DflogFile { log })) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(format!("{}: {}", path.display(), err));
            DFLOG_ERR_IO
        }
        Err(_) => {
            set_last_error("panic in dflog_open".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release a handle returned by `dflog_open`.
///
/// # Safety
/// `file` must come from `dflog_open` and not be used afterwards. Null is
/// ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_close(file: *mut DflogFile) {
    if !file.is_null() {
        // SAFETY: `file` came from Box::into_raw in dflog_open and is not
        // used again per the caller contract
        drop(unsafe { Box::from_raw(file) });
    }
}

/// Decode the comma-separated `fields_utf8` of every `type_utf8` record into
/// f64 columns. Release the result with `dflog_columns_free`.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the strings must be valid
/// NUL-terminated UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_get_columns(
    file: *const DflogFile,
    type_utf8: *const c_char,
    fields_utf8: *const c_char,
    out: *mut *mut DflogColumns,
) -> i32 {
    // SAFETY: forwarded caller contract
    unsafe { get_columns_impl(file, type_utf8, fields_utf8, None, out) }
}

/// `dflog_get_columns` limited to one instance value (the field whose FMTU
/// unit id is '#') when `has_instance` is non-zero. A type without an
/// instance field fails with `DFLOG_ERR_QUERY`.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the strings must be valid
/// NUL-terminated UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_get_columns_filtered(
    file: *const DflogFile,
    type_utf8: *const c_char,
    fields_utf8: *const c_char,
    has_instance: i32,
    instance: i64,
    out: *mut *mut DflogColumns,
) -> i32 {
    let instance = (has_instance != 0).then_some(instance);
    // SAFETY: forwarded caller contract
    unsafe { get_columns_impl(file, type_utf8, fields_utf8, instance, out) }
}

/// # Safety
/// Same contract as `dflog_get_columns`.
unsafe fn get_columns_impl(
    file: *const DflogFile,
    type_utf8: *const c_char,
    fields_utf8: *const c_char,
    instance: Option<i64>,
    out: *mut *mut DflogColumns,
) -> i32 {
    if file.is_null() || type_utf8.is_null() || fields_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: both strings are non-null and NUL-terminated per the caller
    // contract
    let (type_name, fields_csv) = match unsafe {
        (
            CStr::from_ptr(type_utf8).to_str(),
            CStr::from_ptr(fields_utf8).to_str(),
        )
    } {
        (Ok(t), Ok(f)) => (t, f),
        _ => {
            set_last_error("type/fields are not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    let fields: Vec<&str> = fields_csv.split(',').collect();
    // SAFETY: `file` is a live dflog_open handle per the caller contract
    let log = unsafe { &(*file).log };

    match catch_unwind(AssertUnwindSafe(|| {
        dflog_core::columns::get_columns_filtered(log, type_name, &fields, instance)
    })) {
        Ok(Ok(cols)) => {
            let mut boxed = Box::new(DflogColumns {
                rows: cols.rows,
                cols: cols.cols,
                linenos: ptr::null(),
                values: ptr::null(),
                linenos_vec: cols.linenos,
                values_vec: cols.values,
            });
            boxed.linenos = boxed.linenos_vec.as_ptr();
            boxed.values = boxed.values_vec.as_ptr();
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(boxed) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(err.to_string());
            DFLOG_ERR_QUERY
        }
        Err(_) => {
            set_last_error("panic in dflog_get_columns".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Row-major array-column result: `values[row * elems + e]`, elems = 32 for
/// the `a` (int16[32]) format.
#[repr(C)]
#[derive(Debug)]
pub struct DflogArrayColumn {
    pub rows: u64,
    pub elems: u32,
    pub linenos: *const u64,
    pub values: *const i16,
    // rust-owned storage; opaque to the C side beyond `values`
    linenos_vec: Vec<u64>,
    values_vec: Vec<i16>,
}

/// Decode the `a` (int16[32]) array `field_utf8` of every `type_utf8` record.
/// Release the result with `dflog_array_column_free`.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the strings must be valid
/// NUL-terminated UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_get_array_column(
    file: *const DflogFile,
    type_utf8: *const c_char,
    field_utf8: *const c_char,
    out: *mut *mut DflogArrayColumn,
) -> i32 {
    if file.is_null() || type_utf8.is_null() || field_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: both strings are non-null and NUL-terminated per the caller
    // contract
    let (type_name, field) = match unsafe {
        (
            CStr::from_ptr(type_utf8).to_str(),
            CStr::from_ptr(field_utf8).to_str(),
        )
    } {
        (Ok(t), Ok(f)) => (t, f),
        _ => {
            set_last_error("type/field are not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    // SAFETY: `file` is a live dflog_open handle per the caller contract
    let log = unsafe { &(*file).log };

    match catch_unwind(AssertUnwindSafe(|| {
        dflog_core::columns::get_array_column(log, type_name, field)
    })) {
        Ok(Ok(col)) => {
            let mut boxed = Box::new(DflogArrayColumn {
                rows: col.rows,
                elems: dflog_core::columns::ARRAY_ELEMS as u32,
                linenos: ptr::null(),
                values: ptr::null(),
                linenos_vec: col.linenos,
                values_vec: col.values,
            });
            boxed.linenos = boxed.linenos_vec.as_ptr();
            boxed.values = boxed.values_vec.as_ptr();
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(boxed) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(err.to_string());
            DFLOG_ERR_QUERY
        }
        Err(_) => {
            set_last_error("panic in dflog_get_array_column".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Wall-clock correlation from the log's first valid GPS fix:
/// `gps_start_unix_ms` (UTC) and the board `ms_offset` it corresponds to.
/// Returns `DFLOG_ERR_NO_TIME_BASE` when the log has no usable fix.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the out pointers must be valid.
#[no_mangle]
pub unsafe extern "C" fn dflog_time_base(
    file: *const DflogFile,
    gps_start_unix_ms: *mut i64,
    ms_offset: *mut i64,
) -> i32 {
    if file.is_null() || gps_start_unix_ms.is_null() || ms_offset.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }

    // SAFETY: `file` is a live dflog_open handle per the caller contract
    let log = unsafe { &(*file).log };
    match catch_unwind(AssertUnwindSafe(|| log.time_base())) {
        Ok(Some(base)) => {
            // SAFETY: the out pointers are non-null and valid per the
            // caller contract
            unsafe {
                *gps_start_unix_ms = base.gps_start_unix_ms;
                *ms_offset = base.ms_offset
            };
            DFLOG_OK
        }
        Ok(None) => {
            set_last_error("no usable gps fix in log".into());
            DFLOG_ERR_NO_TIME_BASE
        }
        Err(_) => {
            set_last_error("panic in dflog_time_base".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release a result returned by `dflog_get_array_column`.
///
/// # Safety
/// `column` must come from `dflog_get_array_column` and not be used
/// afterwards. Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_array_column_free(column: *mut DflogArrayColumn) {
    if !column.is_null() {
        // SAFETY: `column` came from Box::into_raw in dflog_get_array_column
        // and is not used again per the caller contract
        drop(unsafe { Box::from_raw(column) });
    }
}

/// Release a result returned by `dflog_get_columns`.
///
/// # Safety
/// `columns` must come from `dflog_get_columns` and not be used afterwards.
/// Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_columns_free(columns: *mut DflogColumns) {
    if !columns.is_null() {
        // SAFETY: `columns` came from Box::into_raw in dflog_get_columns and
        // is not used again per the caller contract
        drop(unsafe { Box::from_raw(columns) });
    }
}

/// Copy the calling thread's last error message (UTF-8, NUL-terminated) into
/// `buf`. Returns the number of bytes written excluding the NUL, or the
/// required capacity as a negative number when `cap` is too small.
///
/// # Safety
/// `buf` must point to at least `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn dflog_last_error(buf: *mut c_char, cap: usize) -> i32 {
    if buf.is_null() || cap == 0 {
        return DFLOG_ERR_BAD_ARGUMENT;
    }

    LAST_ERROR.with(|slot| {
        let message = slot.borrow();
        let bytes = message.as_bytes();
        if bytes.len() + 1 > cap {
            return -(bytes.len() as i32 + 1);
        }
        // SAFETY: `buf` holds at least `cap` writable bytes per the caller
        // contract, and bytes.len() + 1 <= cap was just checked
        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), buf as *mut u8, bytes.len());
            *buf.add(bytes.len()) = 0
        };
        bytes.len() as i32
    })
}
