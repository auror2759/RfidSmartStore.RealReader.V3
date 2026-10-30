using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RfidSmartStore.Local
{
    internal sealed class SqliteDb : IDisposable
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;
        private const int SQLITE_OPEN_CREATE = 0x00000004;
        private const int SQLITE_OPEN_FULLMUTEX = 0x00010000;
        private static readonly IntPtr SqliteTransient = new IntPtr(-1);
        private readonly object sync = new object();
        private IntPtr db;

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr statement, int column);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_last_insert_rowid(IntPtr db);

        public SqliteDb(string path)
        {
            int rc = sqlite3_open_v2(Utf8(path), out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException("无法打开SQLite数据库：" + Error());
            Execute("PRAGMA foreign_keys=ON");
            Execute("PRAGMA journal_mode=WAL");
        }

        public int Execute(string sql, params object[] values)
        {
            lock (sync)
            {
                IntPtr stmt = Prepare(sql, values);
                try
                {
                    int rc = sqlite3_step(stmt);
                    if (rc != SQLITE_DONE && rc != SQLITE_ROW) throw new InvalidOperationException(Error());
                    return rc;
                }
                finally { sqlite3_finalize(stmt); }
            }
        }

        public List<Dictionary<string, object>> Query(string sql, params object[] values)
        {
            lock (sync)
            {
                List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                IntPtr stmt = Prepare(sql, values);
                try
                {
                    int rc;
                    while ((rc = sqlite3_step(stmt)) == SQLITE_ROW) rows.Add(ReadRow(stmt));
                    if (rc != SQLITE_DONE) throw new InvalidOperationException(Error());
                    return rows;
                }
                finally { sqlite3_finalize(stmt); }
            }
        }

        public Dictionary<string, object> QueryOne(string sql, params object[] values)
        {
            List<Dictionary<string, object>> rows = Query(sql, values);
            return rows.Count == 0 ? null : rows[0];
        }

        public long LastInsertRowId { get { lock (sync) { return sqlite3_last_insert_rowid(db); } } }

        private IntPtr Prepare(string sql, object[] values)
        {
            IntPtr stmt;
            int rc = sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException(Error());
            try
            {
                for (int i = 0; i < values.Length; i++) Bind(stmt, i + 1, values[i]);
                return stmt;
            }
            catch { sqlite3_finalize(stmt); throw; }
        }

        private static void Bind(IntPtr stmt, int index, object value)
        {
            int rc;
            if (value == null || value == DBNull.Value) rc = sqlite3_bind_null(stmt, index);
            else if (value is int || value is long || value is short || value is byte) rc = sqlite3_bind_int64(stmt, index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            else if (value is decimal || value is double || value is float) rc = sqlite3_bind_double(stmt, index, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            else
            {
                byte[] bytes = Utf8(Convert.ToString(value, CultureInfo.InvariantCulture));
                rc = sqlite3_bind_text(stmt, index, bytes, bytes.Length - 1, SqliteTransient);
            }
            if (rc != SQLITE_OK) throw new InvalidOperationException("SQLite参数绑定失败：" + rc);
        }

        private static Dictionary<string, object> ReadRow(IntPtr stmt)
        {
            Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int count = sqlite3_column_count(stmt);
            for (int i = 0; i < count; i++)
            {
                string name = PtrUtf8(sqlite3_column_name(stmt, i));
                int type = sqlite3_column_type(stmt, i);
                object value;
                if (type == 1) value = sqlite3_column_int64(stmt, i);
                else if (type == 2) value = sqlite3_column_double(stmt, i);
                else if (type == 5) value = null;
                else value = PtrUtf8(sqlite3_column_text(stmt, i));
                row[name] = value;
            }
            return row;
        }

        private string Error() { return PtrUtf8(sqlite3_errmsg(db)); }
        private static byte[] Utf8(string value)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            byte[] result = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            return result;
        }
        private static string PtrUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        public void Dispose() { lock (sync) { if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; } } }
    }
}
