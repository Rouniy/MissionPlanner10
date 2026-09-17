using System;
using System.Runtime.InteropServices;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// P/Invoke bindings for the Rust dataflash log core
    /// (rust/crates/dflog-ffi). All failures degrade to "not available" so
    /// callers can fall back to the managed scanner.
    /// </summary>
    internal static class DFLogNative
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        const string Dll = "dflog_ffi";

        /// <summary>the ABI this build expects; the library built from
        /// rust/crates/dflog-ffi by the BuildDflogNative MSBuild target must
        /// report it</summary>
        internal const uint AbiVersion = 5;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern uint dflog_abi_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_scan_file(byte[] pathUtf8, out IntPtr index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_index_free(IntPtr index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_last_error(byte[] buf, UIntPtr cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_open(byte[] pathUtf8, out IntPtr file);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_close(IntPtr file);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_get_columns(IntPtr file, byte[] typeUtf8, byte[] fieldsUtf8, out IntPtr columns);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_get_columns_filtered(IntPtr file, byte[] typeUtf8, byte[] fieldsUtf8,
            int hasInstance, long instance, out IntPtr columns);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_columns_free(IntPtr columns);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_get_array_column(IntPtr file, byte[] typeUtf8, byte[] fieldUtf8, out IntPtr column);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_array_column_free(IntPtr column);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_time_base(IntPtr file, out long gpsStartUnixMs, out long msOffset);

        [StructLayout(LayoutKind.Sequential)]
        struct NativeIndex
        {
            public ulong count;
            public IntPtr offsets;
            public IntPtr types;
            // followed by rust-owned storage; opaque to this side
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeColumns
        {
            public ulong rows;
            public uint cols;
            public IntPtr linenos;
            public IntPtr values;
            // followed by rust-owned storage; opaque to this side
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeArrayColumn
        {
            public ulong rows;
            public uint elems;
            public IntPtr linenos;
            public IntPtr values;
            // followed by rust-owned storage; opaque to this side
        }

        static byte[] Utf8Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

        /// <summary>
        /// A log held open by the native side for typed column queries
        /// (phase B). Not thread-safe; guard externally like DFLogBuffer does.
        /// </summary>
        public sealed class ColumnReader : IDisposable
        {
            IntPtr _file;

            ColumnReader(IntPtr file)
            {
                _file = file;
            }

            public static ColumnReader Open(string path)
            {
                if (!Available)
                    return null;

                try
                {
                    var rc = dflog_open(Utf8Z(path), out var file);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_open failed ({0}): {1}", rc, LastError());
                        return null;
                    }

                    return new ColumnReader(file);
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_open failed", ex);
                    return null;
                }
            }

            /// <summary>
            /// Decode all records of <paramref name="type"/> into one f64
            /// column per requested field, plus the global record index per
            /// row. Returns false (never throws) on any failure.
            /// </summary>
            public bool TryGetColumns(string type, string[] fields, out long[] linenos, out double[][] columns)
            {
                return TryGetColumns(type, fields, null, out linenos, out columns);
            }

            /// <summary>
            /// <see cref="TryGetColumns(string,string[],out long[],out double[][])"/>
            /// limited to rows of one <paramref name="instance"/> value (the
            /// field carrying the '#' unit id, e.g. IMU.I). Fails when the
            /// type has no instance field.
            /// </summary>
            public bool TryGetColumns(string type, string[] fields, long? instance, out long[] linenos,
                out double[][] columns)
            {
                linenos = null;
                columns = null;

                if (_file == IntPtr.Zero)
                    return false;

                var handle = IntPtr.Zero;
                try
                {
                    var rc = dflog_get_columns_filtered(_file, Utf8Z(type), Utf8Z(string.Join(",", fields)),
                        instance.HasValue ? 1 : 0, instance ?? 0, out handle);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_get_columns({0}) failed ({1}): {2}", type, rc, LastError());
                        return false;
                    }

                    var native = Marshal.PtrToStructure<NativeColumns>(handle);
                    if (native.rows > int.MaxValue)
                        return false;

                    var rows = (int)native.rows;
                    linenos = new long[rows];
                    if (rows > 0)
                        Marshal.Copy(native.linenos, linenos, 0, rows);

                    columns = new double[native.cols][];
                    for (var c = 0; c < native.cols; c++)
                    {
                        columns[c] = new double[rows];
                        if (rows > 0)
                            Marshal.Copy(new IntPtr(native.values.ToInt64() + (long)c * rows * sizeof(double)), columns[c], 0, rows);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_get_columns failed", ex);
                    linenos = null;
                    columns = null;
                    return false;
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                        dflog_columns_free(handle);
                }
            }

            /// <summary>
            /// Decode the `a` (int16[32]) array field of every record of
            /// <paramref name="type"/>, one short[] per row, plus the global
            /// record index per row. Returns false (never throws) on failure.
            /// </summary>
            public bool TryGetArrayColumn(string type, string field, out long[] linenos, out short[][] rows)
            {
                linenos = null;
                rows = null;

                if (_file == IntPtr.Zero)
                    return false;

                var handle = IntPtr.Zero;
                try
                {
                    var rc = dflog_get_array_column(_file, Utf8Z(type), Utf8Z(field), out handle);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_get_array_column({0}.{1}) failed ({2}): {3}", type, field, rc,
                            LastError());
                        return false;
                    }

                    var native = Marshal.PtrToStructure<NativeArrayColumn>(handle);
                    if (native.rows > int.MaxValue)
                        return false;

                    var count = (int)native.rows;
                    var elems = (int)native.elems;
                    linenos = new long[count];
                    if (count > 0)
                        Marshal.Copy(native.linenos, linenos, 0, count);

                    rows = new short[count][];
                    for (var r = 0; r < count; r++)
                    {
                        rows[r] = new short[elems];
                        Marshal.Copy(new IntPtr(native.values.ToInt64() + (long)r * elems * sizeof(short)), rows[r], 0, elems);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_get_array_column failed", ex);
                    linenos = null;
                    rows = null;
                    return false;
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                        dflog_array_column_free(handle);
                }
            }

            /// <summary>
            /// Wall-clock correlation from the log's first valid GPS fix.
            /// Returns false when the library is unavailable or the log has
            /// no usable fix.
            /// </summary>
            public bool TryGetTimeBase(out long gpsStartUnixMs, out long msOffset)
            {
                gpsStartUnixMs = 0;
                msOffset = 0;

                if (_file == IntPtr.Zero)
                    return false;

                try
                {
                    return dflog_time_base(_file, out gpsStartUnixMs, out msOffset) == 0;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_time_base failed", ex);
                    return false;
                }
            }

            public void Dispose()
            {
                if (_file != IntPtr.Zero)
                {
                    dflog_close(_file);
                    _file = IntPtr.Zero;
                }
            }
        }

        static readonly Lazy<bool> _available = new Lazy<bool>(() =>
        {
            try
            {
                var abi = dflog_abi_version();
                if (abi != AbiVersion)
                {
                    log.WarnFormat("dflog_ffi ABI {0} does not match expected {1}", abi, AbiVersion);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Debug("dflog_ffi not available: " + ex.Message);
                return false;
            }
        });

        public static bool Available => _available.Value;

        static string LastError()
        {
            try
            {
                var buf = new byte[1024];
                var n = dflog_last_error(buf, (UIntPtr)buf.Length);
                if (n > 0)
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            catch
            {
            }

            return "(unknown)";
        }

        /// <summary>
        /// Index the log at <paramref name="path"/>. Returns false (never
        /// throws) when the native library is missing or the scan fails, so
        /// the caller can use the managed scanner instead.
        /// </summary>
        public static bool TryScan(string path, out long[] offsets, out byte[] types)
        {
            offsets = null;
            types = null;

            if (!Available)
                return false;

            var handle = IntPtr.Zero;
            try
            {
                // netstandard2.0 has no LPUTF8Str - marshal the path by hand
                var pathUtf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
                var rc = dflog_scan_file(pathUtf8, out handle);
                if (rc != 0)
                {
                    log.WarnFormat("dflog_scan_file failed ({0}): {1}", rc, LastError());
                    return false;
                }

                var index = Marshal.PtrToStructure<NativeIndex>(handle);
                if (index.count > int.MaxValue)
                {
                    log.WarnFormat("dflog index too large for managed copy: {0}", index.count);
                    return false;
                }

                var count = (int)index.count;
                offsets = new long[count];
                types = new byte[count];
                if (count > 0)
                {
                    Marshal.Copy(index.offsets, offsets, 0, count);
                    Marshal.Copy(index.types, types, 0, count);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Warn("dflog native scan failed", ex);
                offsets = null;
                types = null;
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    dflog_index_free(handle);
            }
        }
    }
}
