using System;
using System.Data.SQLite;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace wordlist2sql
{
    /// <summary>
    /// Minimal embedded HTTP server exposing each table at /tablename so a
    /// table can be pulled with curl, e.g.:
    ///     curl http://localhost:8088/rockyou -o rockyou.txt
    /// Opens its own read-only SQLite connection per request, independent of
    /// the UI thread's connection.
    /// </summary>
    public sealed class ExportServer : IDisposable
    {
        // Per-response copy buffer. Large writes mean fewer http.sys/kernel
        // transitions, which is the main lever for raw outbound throughput.
        private const int StreamBufferSize = 1 << 20; // 1 MiB

        private readonly string _dbPath;
        private readonly int _port;
        private readonly bool _lanAccess;
        private readonly HttpListener _listener = new HttpListener();
        private Thread _thread;
        private volatile bool _running;

        // Live connection accounting.
        private int _active;
        private long _totalServed;
        private long _peak;

        /// <summary>Raised (on a background thread) with a one-line log message.</summary>
        public event Action<string> Log;

        /// <summary>
        /// Raised when the active-connection count changes:
        /// (activeNow, totalServedSoFar, peakConcurrent).
        /// </summary>
        public event Action<int, long, long> ConnectionsChanged;

        public int ActiveConnections => Volatile.Read(ref _active);

        /// <summary>
        /// URL clients should use. For LAN mode this is the machine's primary
        /// IPv4 address; otherwise loopback.
        /// </summary>
        public string BaseUrl =>
            _lanAccess ? $"http://{GetLocalIPv4()}:{_port}/" : $"http://localhost:{_port}/";

        /// <param name="lanAccess">
        /// When true, bind to all network interfaces so other machines on the
        /// LAN can reach the server. When false, bind to loopback only.
        /// </param>
        public ExportServer(string dbPath, int port, bool lanAccess)
        {
            _dbPath = dbPath;
            _port = port;
            _lanAccess = lanAccess;
            // "+" is the strong wildcard: listen on every interface/host header.
            // "localhost" restricts to loopback (no LAN reachability).
            string host = lanAccess ? "+" : "localhost";
            _listener.Prefixes.Add($"http://{host}:{_port}/");
        }

        public void Start()
        {
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                throw new InvalidOperationException(
                    "Access denied binding to all interfaces for LAN access.\r\n\r\n" +
                    "Windows requires elevation or a one-time URL reservation to listen on " +
                    "non-loopback addresses. Fix it with EITHER:\r\n\r\n" +
                    "  1) Run wordlist2sql as Administrator, OR\r\n" +
                    "  2) Run this once in an elevated command prompt:\r\n" +
                    $"     netsh http add urlacl url=http://+:{_port}/ user=Everyone\r\n\r\n" +
                    "You may also need to allow the port through the firewall:\r\n" +
                    $"     netsh advfirewall firewall add rule name=\"wordlist2sql {_port}\" " +
                    $"dir=in action=allow protocol=TCP localport={_port}",
                    ex);
            }

            // Allow many simultaneous downloads (http.sys queues; we serve in
            // parallel) and warm the pool so concurrent clients start instantly.
            try
            {
                ThreadPool.GetMinThreads(out int w, out int io);
                ThreadPool.SetMinThreads(Math.Max(w, 32), Math.Max(io, 32));
            }
            catch { /* best effort */ }

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "wordlist-http" };
            _thread.Start();

            Log?.Invoke($"Serving {BaseUrl}  (GET / for table list)");
            if (_lanAccess)
            {
                Log?.Invoke($"LAN clients:  curl {BaseUrl}<table> -o out.txt");
                Log?.Invoke($"If unreachable, allow it through the firewall (elevated):  " +
                            $"netsh advfirewall firewall add rule name=\"wordlist2sql {_port}\" " +
                            $"dir=in action=allow protocol=TCP localport={_port}");
            }
        }

        /// <summary>Best-effort primary IPv4 of this machine (for display).</summary>
        public static string GetLocalIPv4()
        {
            try
            {
                // No traffic is sent; this just selects the outbound interface.
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530);
                    if (s.LocalEndPoint is IPEndPoint ep)
                        return ep.Address.ToString();
                }
            }
            catch { /* fall through */ }

            try
            {
                foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
            }
            catch { /* ignore */ }

            return "localhost";
        }

        private void Loop()
        {
            // One thread only accepts; each accepted request is handled on a
            // pool thread so many clients download concurrently.
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    // Listener stopped/disposed.
                    break;
                }

                ThreadPool.QueueUserWorkItem(state => Serve((HttpListenerContext)state), ctx);
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            int now = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _totalServed);
            // Track the high-water mark of concurrent connections.
            long peak;
            do { peak = Interlocked.Read(ref _peak); }
            while (now > peak && Interlocked.CompareExchange(ref _peak, now, peak) != peak);
            RaiseConnections();

            try { Handle(ctx); }
            catch (Exception ex) { Log?.Invoke("Request error: " + ex.Message); }
            finally
            {
                Interlocked.Decrement(ref _active);
                RaiseConnections();
            }
        }

        private void RaiseConnections()
        {
            ConnectionsChanged?.Invoke(
                Volatile.Read(ref _active),
                Interlocked.Read(ref _totalServed),
                Interlocked.Read(ref _peak));
        }

        private void Handle(HttpListenerContext ctx)
        {
            string raw = ctx.Request.Url.AbsolutePath.Trim('/');

            if (string.IsNullOrEmpty(raw))
            {
                WriteIndex(ctx);
                return;
            }

            // Route:  /table            -> word list  OR  single/all blobs
            //         /table/<selector> -> a specific blob by name or id
            int slash = raw.IndexOf('/');
            string tableSeg = slash >= 0 ? raw.Substring(0, slash) : raw;
            string selector = slash >= 0 ? Uri.UnescapeDataString(raw.Substring(slash + 1)) : null;

            string table = WordlistDb.SanitizeTableName(Uri.UnescapeDataString(tableSeg));
            ServeTable(ctx, table, selector);
        }

        private void WriteIndex(HttpListenerContext ctx)
        {
            var sb = new StringBuilder();
            try
            {
                using (var conn = OpenReadOnly())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT name FROM sqlite_master WHERE type='table' " +
                        "AND name NOT LIKE 'sqlite_%' AND name <> '_wordlist_meta' ORDER BY name;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            sb.AppendLine(r.GetString(0));
                    }
                }
            }
            catch (Exception ex)
            {
                WriteStatus(ctx, 500, "error: " + ex.Message);
                return;
            }

            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
            Log?.Invoke($"{ctx.Request.RemoteEndPoint} GET / -> table list");
        }

        private void ServeTable(HttpListenerContext ctx, string table, string selector)
        {
            SQLiteConnection conn = null;
            try
            {
                conn = OpenReadOnly();

                // Confirm the table exists before streaming a 200.
                using (var check = conn.CreateCommand())
                {
                    check.CommandText =
                        "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1;";
                    check.Parameters.AddWithValue("@n", table);
                    if (check.ExecuteScalar() == null)
                    {
                        WriteStatus(ctx, 404, $"no such table: {table}\n");
                        return;
                    }
                }

                bool hasWord = HasColumn(conn, table, "word");
                bool hasData = HasColumn(conn, table, "data");

                if (hasWord)
                    ServeWords(ctx, conn, table);
                else if (hasData)
                    ServeBlob(ctx, conn, table, selector);
                else
                    WriteStatus(ctx, 400,
                        $"'{table}' has no 'word' or 'data' column; don't know how to serve it.\n");
            }
            catch (HttpListenerException)
            {
                // Client disconnected mid-stream; nothing to do.
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
                conn?.Dispose();
            }
        }

        private void ServeWords(HttpListenerContext ctx, SQLiteConnection conn, string table)
        {
            bool isHead = string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            string rangeHeader = ctx.Request.Headers["Range"];

            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.AddHeader("Accept-Ranges", "bytes");
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{table}.txt\"");

            // The streamed length: UTF-8 bytes of each non-null word + one '\n'.
            // Stable across requests (row order is fixed), which makes resume safe.
            long total = WordTableByteLength(conn, table);

            long start = 0, end = total - 1;
            var range = ParseRange(rangeHeader, total);
            if (range.Kind == RangeKind.Unsatisfiable)
            {
                ctx.Response.AddHeader("Content-Range", $"bytes */{total}");
                WriteStatus(ctx, 416, $"requested range not satisfiable for '{table}' ({total} bytes).\n");
                return;
            }
            if (range.Kind == RangeKind.Partial)
            {
                start = range.Start;
                end = range.End;
                ctx.Response.StatusCode = 206;
                ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
            }
            else
            {
                ctx.Response.StatusCode = 200;
            }

            long partLength = (total == 0) ? 0 : (end - start + 1);
            ctx.Response.ContentLength64 = partLength;

            if (isHead)
            {
                Log?.Invoke($"{ctx.Request.RemoteEndPoint} HEAD /{table} -> {total:n0} bytes");
                return;
            }

            // Walk rows, tracking the byte position, writing only the [start,end]
            // slice. Boundary lines are partially written.
            var nl = new byte[] { (byte)'\n' };
            var outStream = ctx.Response.OutputStream;
            long pos = 0;
            long remaining = partLength;

            using (var bufStream = new BufferedStream(outStream, StreamBufferSize))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT word FROM {Quote(table)};";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read() && remaining > 0)
                    {
                        if (r.IsDBNull(0)) continue;

                        byte[] wb = Encoding.UTF8.GetBytes(r.GetString(0));
                        int lineLen = wb.Length + 1;          // + '\n'
                        long lineStart = pos;
                        long lineEnd = pos + lineLen;          // exclusive
                        pos = lineEnd;

                        if (lineEnd <= start) continue;        // entirely before range
                        if (lineStart > end) break;            // past range

                        // Intersect [lineStart,lineEnd) with [start,end].
                        int from = (int)Math.Max(0, start - lineStart);
                        int toExcl = (int)Math.Min(lineLen, end - lineStart + 1);

                        if (from == 0 && toExcl == lineLen)
                        {
                            // Whole line in range (the common case): bulk write.
                            bufStream.Write(wb, 0, wb.Length);
                            bufStream.WriteByte((byte)'\n');
                            remaining -= lineLen;
                        }
                        else
                        {
                            // Boundary line: write the word slice, then '\n' if included.
                            int wEnd = Math.Min(toExcl, wb.Length);
                            if (wEnd > from)
                            {
                                bufStream.Write(wb, from, wEnd - from);
                                remaining -= (wEnd - from);
                            }
                            if (wb.Length >= from && wb.Length < toExcl) // newline byte in slice
                            {
                                bufStream.WriteByte((byte)'\n');
                                remaining--;
                            }
                        }
                    }
                }
            }

            Log?.Invoke($"{ctx.Request.RemoteEndPoint} GET /{table} -> " +
                        (range.Kind == RangeKind.Partial
                            ? $"bytes {start}-{end}/{total}"
                            : $"{total:n0} bytes"));
        }

        /// <summary>
        /// Total byte length of the streamed word list (UTF-8 word bytes + one
        /// '\n' per non-null row). Read from metadata when available, else
        /// computed with a single aggregate scan.
        /// </summary>
        private static long WordTableByteLength(SQLiteConnection conn, string table)
        {
            // Metadata is the fast path, but the table (or column) may be absent
            // in a database created by another tool — fall through to a scan.
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT total_bytes FROM _wordlist_meta WHERE table_name=@t;";
                    cmd.Parameters.AddWithValue("@t", table);
                    object o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value)
                        return Convert.ToInt64(o);
                }
            }
            catch (SQLiteException) { /* no meta table/column; compute below */ }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    $"SELECT COALESCE(SUM(length(CAST(word AS BLOB)) + 1), 0) " +
                    $"FROM {Quote(table)} WHERE word IS NOT NULL;";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// Serve binary content from a blob table.
        ///  - /table            : the single blob (or a listing if there are many)
        ///  - /table/&lt;name&gt;   : the blob whose stored file name matches
        ///  - /table/&lt;id&gt;     : the blob with that row id
        /// </summary>
        private void ServeBlob(HttpListenerContext ctx, SQLiteConnection conn, string table, string selector)
        {
            string where;
            object key;

            if (!string.IsNullOrEmpty(selector))
            {
                // Numeric selector -> row id; otherwise match the stored name.
                if (long.TryParse(selector, out long id))
                {
                    where = "id = @k";
                    key = id;
                }
                else
                {
                    where = "name = @k";
                    key = selector;
                }
            }
            else
            {
                // No selector: if exactly one blob, serve it; otherwise list them.
                long count;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};";
                    count = Convert.ToInt64(cmd.ExecuteScalar());
                }

                if (count == 0)
                {
                    WriteStatus(ctx, 404, $"'{table}' contains no blobs.\n");
                    return;
                }
                if (count > 1)
                {
                    WriteBlobListing(ctx, conn, table);
                    return;
                }

                where = "1=1"; // the only row
                key = null;
            }

            using (var cmd = conn.CreateCommand())
            {
                // length() first so we can set Content-Length, then stream data.
                cmd.CommandText =
                    $"SELECT name, length(data), data FROM {Quote(table)} WHERE {where} LIMIT 1;";
                if (key != null) cmd.Parameters.AddWithValue("@k", key);

                using (var r = cmd.ExecuteReader(System.Data.CommandBehavior.SequentialAccess))
                {
                    if (!r.Read())
                    {
                        WriteStatus(ctx, 404, $"no blob '{selector}' in '{table}'.\n");
                        return;
                    }

                    string name = r.IsDBNull(0) ? table + ".bin" : r.GetString(0);
                    long len = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    bool isHead = string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

                    ctx.Response.ContentType = ContentTypeFor(name);
                    ctx.Response.AddHeader("Accept-Ranges", "bytes");
                    ctx.Response.AddHeader("Content-Disposition",
                        $"attachment; filename=\"{SanitizeHeader(name)}\"");

                    // Resolve a Range header (single range) against the blob length.
                    long start = 0, end = len - 1;
                    var range = ParseRange(ctx.Request.Headers["Range"], len);
                    if (range.Kind == RangeKind.Unsatisfiable)
                    {
                        ctx.Response.AddHeader("Content-Range", $"bytes */{len}");
                        WriteStatus(ctx, 416, $"requested range not satisfiable for '{name}' ({len} bytes).\n");
                        return;
                    }
                    if (range.Kind == RangeKind.Partial)
                    {
                        start = range.Start;
                        end = range.End;
                        ctx.Response.StatusCode = 206; // Partial Content
                        ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{len}");
                    }
                    else
                    {
                        ctx.Response.StatusCode = 200;
                    }

                    long partLength = (len == 0) ? 0 : (end - start + 1);
                    ctx.Response.ContentLength64 = partLength;

                    if (isHead)
                    {
                        Log?.Invoke($"{ctx.Request.RemoteEndPoint} HEAD /{table}" +
                                    (selector != null ? "/" + selector : "") +
                                    $" -> '{name}' ({len:n0} bytes)");
                        return; // headers only
                    }

                    // Stream the requested byte range in large chunks (no
                    // full-file buffering). Big writes minimize kernel transitions.
                    var outStream = ctx.Response.OutputStream;
                    byte[] buffer = new byte[StreamBufferSize];
                    long offset = start;
                    long remaining = partLength;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        long read = r.GetBytes(2, offset, buffer, 0, want);
                        if (read <= 0) break;
                        outStream.Write(buffer, 0, (int)read);
                        offset += read;
                        remaining -= read;
                    }

                    Log?.Invoke($"{ctx.Request.RemoteEndPoint} GET /{table}" +
                                (selector != null ? "/" + selector : "") +
                                $" -> blob '{name}' " +
                                (range.Kind == RangeKind.Partial
                                    ? $"bytes {start}-{end}/{len}"
                                    : $"({len:n0} bytes)"));
                }
            }
        }

        private void WriteBlobListing(HttpListenerContext ctx, SQLiteConnection conn, string table)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# '{table}' contains multiple blobs. Fetch one with:");
            sb.AppendLine($"#   curl http://localhost:{_port}/{table}/<name>   (or /<id>)");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT id, name, length(data) FROM {Quote(table)} ORDER BY id;";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        long id = r.GetInt64(0);
                        string name = r.IsDBNull(1) ? "" : r.GetString(1);
                        long len = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                        sb.AppendLine($"{id}\t{len}\t{name}");
                    }
                }
            }
            WriteStatus(ctx, 200, sb.ToString());
        }

        private enum RangeKind { None, Partial, Unsatisfiable }

        private struct RangeResult
        {
            public RangeKind Kind;
            public long Start;
            public long End;
        }

        /// <summary>
        /// Parse a single HTTP byte-range header against a known content length.
        /// Supports "bytes=start-end", "bytes=start-" and "bytes=-suffix".
        /// Multiple ranges or anything unparseable is treated as no range (full).
        /// </summary>
        private static RangeResult ParseRange(string header, long length)
        {
            var none = new RangeResult { Kind = RangeKind.None };
            if (string.IsNullOrWhiteSpace(header))
                return none;

            header = header.Trim();
            const string prefix = "bytes=";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return none;

            string spec = header.Substring(prefix.Length).Trim();
            if (spec.Length == 0 || spec.IndexOf(',') >= 0)
                return none; // multi-range not supported -> serve full

            int dash = spec.IndexOf('-');
            if (dash < 0)
                return none;

            string startText = spec.Substring(0, dash).Trim();
            string endText = spec.Substring(dash + 1).Trim();

            // Empty content: any range request is unsatisfiable.
            if (length <= 0)
                return new RangeResult { Kind = RangeKind.Unsatisfiable };

            long start, end;
            if (startText.Length == 0)
            {
                // Suffix range: "-N" => last N bytes.
                if (!long.TryParse(endText, out long suffix) || suffix <= 0)
                    return none;
                if (suffix > length) suffix = length;
                start = length - suffix;
                end = length - 1;
            }
            else
            {
                if (!long.TryParse(startText, out start) || start < 0)
                    return none;
                if (endText.Length == 0)
                    end = length - 1;                 // "start-" => to end
                else if (!long.TryParse(endText, out end) || end < start)
                    return none;
                if (end > length - 1) end = length - 1; // clamp to available
            }

            if (start > length - 1)
                return new RangeResult { Kind = RangeKind.Unsatisfiable };

            return new RangeResult { Kind = RangeKind.Partial, Start = start, End = end };
        }

        private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

        private static string SanitizeHeader(string name)
        {
            // Keep the filename header on one safe line.
            return name.Replace("\"", "'").Replace("\r", "").Replace("\n", "");
        }

        private static string ContentTypeFor(string name)
        {
            string ext = "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0) ext = name.Substring(dot).ToLowerInvariant();
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".gz": return "application/gzip";
                case ".json": return "application/json";
                case ".xml": return "application/xml";
                case ".txt": case ".log": case ".csv": return "text/plain; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".mp4": return "video/mp4";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                default: return "application/octet-stream";
            }
        }

        private static void WriteStatus(HttpListenerContext ctx, int code, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
        }

        private static bool HasColumn(SQLiteConnection conn, string table, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({Quote(table)});";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            return false;
        }

        private SQLiteConnection OpenReadOnly()
        {
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Version = 3,
                ReadOnly = true,
                Pooling = true,
                BusyTimeout = 5000,
            };
            var conn = new SQLiteConnection(builder.ConnectionString);
            conn.Open();

            // Read-tuned pragmas: memory-map the file and use a generous page
            // cache so concurrent readers pull pages fast with few syscalls.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "PRAGMA query_only=1;" +
                    "PRAGMA mmap_size=1073741824;" + // up to 1 GiB mmapped
                    "PRAGMA cache_size=-65536;" +    // 64 MiB page cache
                    "PRAGMA temp_store=MEMORY;";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            Log?.Invoke("Server stopped.");
        }

        public void Dispose() => Stop();
    }
}
