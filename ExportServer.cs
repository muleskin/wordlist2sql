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
        private readonly string _dbPath;
        private readonly int _port;
        private readonly bool _lanAccess;
        private readonly HttpListener _listener = new HttpListener();
        private Thread _thread;
        private volatile bool _running;

        /// <summary>Raised (on a background thread) with a one-line log message.</summary>
        public event Action<string> Log;

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

                try { Handle(ctx); }
                catch (Exception ex) { Log?.Invoke("Request error: " + ex.Message); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).Trim('/');

            if (string.IsNullOrEmpty(path))
            {
                WriteIndex(ctx);
                return;
            }

            string table = WordlistDb.SanitizeTableName(path);
            ServeTable(ctx, table);
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

        private void ServeTable(HttpListenerContext ctx, string table)
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

                // This endpoint streams a one-word-per-line list, so the table
                // must have a 'word' column. Blob tables don't.
                if (!HasWordColumn(conn, table))
                {
                    WriteStatus(ctx, 400,
                        $"'{table}' is not a word-list table (no 'word' column); cannot stream as text.\n");
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.SendChunked = true;
                ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{table}.txt\"");

                long written = 0;
                using (var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false), 1 << 20))
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT word FROM \"{table.Replace("\"", "\"\"")}\";";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            if (!r.IsDBNull(0))
                                writer.WriteLine(r.GetString(0));
                            written++;
                        }
                    }
                }

                Log?.Invoke($"{ctx.Request.RemoteEndPoint} GET /{table} -> {written:n0} words");
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

        private static void WriteStatus(HttpListenerContext ctx, int code, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
        }

        private static bool HasWordColumn(SQLiteConnection conn, string table)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        if (string.Equals(r.GetString(1), "word", StringComparison.OrdinalIgnoreCase))
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
