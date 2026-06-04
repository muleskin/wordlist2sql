using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace wordlist2sql
{
    /// <summary>
    /// All SQLite access for the app. One instance owns one open database file.
    /// Tuned for importing / exporting multi-gigabyte one-word-per-line lists.
    /// </summary>
    public sealed class WordlistDb : IDisposable
    {
        // Name of the bookkeeping table. Underscore-prefixed so it is easy to
        // hide from the user-facing table list.
        private const string MetaTable = "_wordlist_meta";

        // How many rows to buffer inside a single transaction before committing.
        // Large batches keep the WAL/journal churn down on huge imports.
        private const int BatchSize = 200_000;

        private readonly SQLiteConnection _conn;

        public string DatabasePath { get; }

        private WordlistDb(SQLiteConnection conn, string path)
        {
            _conn = conn;
            DatabasePath = path;
        }

        /// <summary>Open (creating if necessary) a database file.</summary>
        public static WordlistDb Open(string path)
        {
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                Version = 3,
                // Pool connections; the embedded HTTP server opens its own.
                Pooling = true,
                // 256 MiB page cache (negative = KiB) to speed bulk work.
                CacheSize = -262144,
            };

            var conn = new SQLiteConnection(builder.ConnectionString);
            conn.Open();

            ApplyFastPragmas(conn);

            var db = new WordlistDb(conn, path);
            db.EnsureMeta();
            return db;
        }

        private static void ApplyFastPragmas(SQLiteConnection conn)
        {
            // These trade crash-durability for raw throughput. Acceptable: the
            // input wordlist file is the source of truth and an interrupted
            // import can simply be re-run.
            Exec(conn,
                "PRAGMA journal_mode=OFF;" +
                "PRAGMA synchronous=OFF;" +
                "PRAGMA temp_store=MEMORY;" +
                "PRAGMA locking_mode=NORMAL;");
        }

        private void EnsureMeta()
        {
            Exec(_conn,
                $"CREATE TABLE IF NOT EXISTS \"{MetaTable}\" (" +
                "table_name TEXT PRIMARY KEY," +
                "word_count INTEGER NOT NULL," +
                "source_file TEXT," +
                "deduped INTEGER NOT NULL DEFAULT 0," +
                "imported_utc TEXT NOT NULL);");
        }

        // ---- Table identifier handling -------------------------------------

        /// <summary>
        /// Turn an arbitrary string (usually a file name) into a safe SQLite
        /// identifier: letters/digits/underscore only, never starting with a
        /// digit, never empty.
        /// </summary>
        public static string SanitizeTableName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "wordlist";

            string baseName = Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = raw;

            string cleaned = Regex.Replace(baseName, @"[^A-Za-z0-9_]", "_").Trim('_');
            if (string.IsNullOrEmpty(cleaned))
                cleaned = "wordlist";
            if (char.IsDigit(cleaned[0]))
                cleaned = "t_" + cleaned;
            return cleaned;
        }

        // Quote an identifier for inline SQL. Identifiers are already
        // sanitized, but quoting + doubling is the correct, safe form.
        private static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

        public bool TableExists(string tableName)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1;";
                cmd.Parameters.AddWithValue("@n", tableName);
                return cmd.ExecuteScalar() != null;
            }
        }

        // ---- Listing -------------------------------------------------------

        public sealed class TableInfo
        {
            public string Name;
            public long WordCount;
            public string SourceFile;
            public bool Deduped;
            public string ImportedUtc;
        }

        /// <summary>List user wordlist tables with metadata (cheap, no scans).</summary>
        public List<TableInfo> ListTables()
        {
            var result = new List<TableInfo>();

            // Pull metadata first into a lookup.
            var meta = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    $"SELECT table_name, word_count, source_file, deduped, imported_utc FROM \"{MetaTable}\";";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var ti = new TableInfo
                        {
                            Name = r.GetString(0),
                            WordCount = r.GetInt64(1),
                            SourceFile = r.IsDBNull(2) ? "" : r.GetString(2),
                            Deduped = r.GetInt64(3) != 0,
                            ImportedUtc = r.IsDBNull(4) ? "" : r.GetString(4),
                        };
                        meta[ti.Name] = ti;
                    }
                }
            }

            // Enumerate actual tables so manually-added or partially-imported
            // tables still appear.
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' " +
                    "AND name NOT LIKE 'sqlite_%' AND name <> @meta ORDER BY name;";
                cmd.Parameters.AddWithValue("@meta", MetaTable);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string name = r.GetString(0);
                        if (meta.TryGetValue(name, out var ti))
                            result.Add(ti);
                        else
                            result.Add(new TableInfo { Name = name, WordCount = -1 });
                    }
                }
            }

            return result;
        }

        // ---- Import --------------------------------------------------------

        public sealed class ImportProgress
        {
            public long BytesRead;
            public long TotalBytes;
            public long WordsInserted;
            public long LinesRead;
        }

        public sealed class ImportResult
        {
            public long WordsInserted;
            public long LinesRead;
            public bool Deduped;
        }

        /// <summary>
        /// Stream a one-word-per-line file into <paramref name="tableName"/>.
        /// Existing table of that name is dropped first (replace semantics).
        /// </summary>
        public ImportResult ImportWordlist(
            string filePath,
            string tableName,
            bool dedupe,
            IProgress<ImportProgress> progress,
            CancellationToken ct)
        {
            long totalBytes = new FileInfo(filePath).Length;

            DropTable(tableName);

            if (dedupe)
            {
                // WITHOUT ROWID + text PK is the compact form for a unique set.
                Exec(_conn, $"CREATE TABLE {Q(tableName)} (word TEXT PRIMARY KEY) WITHOUT ROWID;");
            }
            else
            {
                Exec(_conn, $"CREATE TABLE {Q(tableName)} (word TEXT);");
            }

            string insertSql = dedupe
                ? $"INSERT OR IGNORE INTO {Q(tableName)} (word) VALUES (@w);"
                : $"INSERT INTO {Q(tableName)} (word) VALUES (@w);";

            long inserted = 0;
            long lines = 0;
            var prog = new ImportProgress { TotalBytes = totalBytes };

            // 1 MiB read buffer over the raw file stream; StreamReader handles
            // BOM detection and line splitting.
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var reader = new StreamReader(fs, Encoding.UTF8, true, 1 << 20))
            {
                var tx = _conn.BeginTransaction();
                var cmd = _conn.CreateCommand();
                cmd.CommandText = insertSql;
                var p = cmd.CreateParameter();
                p.ParameterName = "@w";
                cmd.Parameters.Add(p);
                cmd.Prepare();

                long sinceCommit = 0;
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines++;

                        // Skip blank lines; trim trailing whitespace/CR.
                        string word = line.Trim();
                        if (word.Length == 0)
                            continue;

                        p.Value = word;
                        inserted += cmd.ExecuteNonQuery();
                        sinceCommit++;

                        if (sinceCommit >= BatchSize)
                        {
                            tx.Commit();
                            tx.Dispose();
                            ct.ThrowIfCancellationRequested();

                            prog.BytesRead = fs.Position;
                            prog.WordsInserted = inserted;
                            prog.LinesRead = lines;
                            progress?.Report(prog);

                            tx = _conn.BeginTransaction();
                            cmd.Transaction = tx;
                            sinceCommit = 0;
                        }
                    }

                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* ignore */ }
                    throw;
                }
                finally
                {
                    cmd.Dispose();
                    tx.Dispose();
                }
            }

            UpsertMeta(tableName, inserted, filePath, dedupe);

            prog.BytesRead = totalBytes;
            prog.WordsInserted = inserted;
            prog.LinesRead = lines;
            progress?.Report(prog);

            return new ImportResult { WordsInserted = inserted, LinesRead = lines, Deduped = dedupe };
        }

        private void UpsertMeta(string tableName, long count, string sourceFile, bool dedupe)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    $"INSERT INTO \"{MetaTable}\" (table_name, word_count, source_file, deduped, imported_utc) " +
                    "VALUES (@t, @c, @s, @d, @u) " +
                    "ON CONFLICT(table_name) DO UPDATE SET " +
                    "word_count=@c, source_file=@s, deduped=@d, imported_utc=@u;";
                cmd.Parameters.AddWithValue("@t", tableName);
                cmd.Parameters.AddWithValue("@c", count);
                cmd.Parameters.AddWithValue("@s", sourceFile ?? "");
                cmd.Parameters.AddWithValue("@d", dedupe ? 1 : 0);
                cmd.Parameters.AddWithValue("@u", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Export --------------------------------------------------------

        public sealed class ExportProgress
        {
            public long WordsWritten;
            public long TotalWords;
        }

        /// <summary>Stream every word of a table to a UTF-8 text file, one per line.</summary>
        public long ExportTable(
            string tableName,
            string outFilePath,
            IProgress<ExportProgress> progress,
            CancellationToken ct)
        {
            long total = TryGetWordCount(tableName);
            var prog = new ExportProgress { TotalWords = total };
            long written = 0;

            using (var fs = new FileStream(outFilePath, FileMode.Create, FileAccess.Write,
                       FileShare.None, 1 << 20))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false), 1 << 20))
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT word FROM {Q(tableName)};";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (!r.IsDBNull(0))
                            writer.WriteLine(r.GetString(0));
                        written++;

                        if ((written & 0x3FFFF) == 0) // every ~262k rows
                        {
                            ct.ThrowIfCancellationRequested();
                            prog.WordsWritten = written;
                            progress?.Report(prog);
                        }
                    }
                }
            }

            prog.WordsWritten = written;
            progress?.Report(prog);
            return written;
        }

        /// <summary>
        /// Stream words of a table to a TextWriter (used by the HTTP server).
        /// Returns words written. Honors cancellation between rows.
        /// </summary>
        public long StreamTable(string tableName, TextWriter writer, CancellationToken ct)
        {
            long written = 0;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT word FROM {Q(tableName)};";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!r.IsDBNull(0))
                            writer.WriteLine(r.GetString(0));
                        written++;
                    }
                }
            }
            return written;
        }

        // ---- Search --------------------------------------------------------

        public sealed class SearchResult
        {
            public List<string> TablesContaining = new List<string>();
            public int TablesSearched;
        }

        /// <summary>
        /// Look up <paramref name="word"/> across every user table and return
        /// which tables contain it. The word itself is never stored or echoed.
        /// </summary>
        /// <param name="caseInsensitive">Match regardless of letter case (ASCII).</param>
        /// <param name="partial">
        /// Substring match (anywhere in a word) instead of whole-word equality.
        /// </param>
        public SearchResult SearchWord(
            string word,
            bool caseInsensitive,
            bool partial,
            IProgress<string> progress,
            CancellationToken ct)
        {
            string needle = (word ?? string.Empty).Trim();
            var result = new SearchResult();

            // Build the predicate once:
            //  - exact/sensitive  : word = @w                  (uses PK index when deduped)
            //  - exact/insensitive: word = @w COLLATE NOCASE   (ASCII case-fold)
            //  - partial          : instr(...) > 0             (literal substring; full scan)
            // instr() keeps any %, _, *, ? in the input literal — no wildcard
            // escaping needed.
            string predicate;
            if (partial)
            {
                predicate = caseInsensitive
                    ? "instr(lower(word), lower(@w)) > 0"
                    : "instr(word, @w) > 0";
            }
            else
            {
                predicate = caseInsensitive
                    ? "word = @w COLLATE NOCASE"
                    : "word = @w";
            }

            var tables = ListTables();
            result.TablesSearched = tables.Count;

            foreach (var t in tables)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(t.Name);

                using (var cmd = _conn.CreateCommand())
                {
                    // EXISTS short-circuits on the first match.
                    cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {Q(t.Name)} WHERE {predicate});";
                    cmd.Parameters.AddWithValue("@w", needle);
                    object o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value && Convert.ToInt64(o) != 0)
                        result.TablesContaining.Add(t.Name);
                }
            }

            return result;
        }

        // ---- Maintenance ---------------------------------------------------

        public void DropTable(string tableName)
        {
            Exec(_conn, $"DROP TABLE IF EXISTS {Q(tableName)};");
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM \"{MetaTable}\" WHERE table_name=@t;";
                cmd.Parameters.AddWithValue("@t", tableName);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Rename a table (and its metadata row). <paramref name="newName"/> is
        /// sanitized first; the cleaned identifier actually used is returned.
        /// Throws if the target name already exists.
        /// </summary>
        public string RenameTable(string oldName, string newName)
        {
            string target = SanitizeTableName(newName);

            if (string.Equals(target, oldName, StringComparison.Ordinal))
                return target;

            if (!TableExists(oldName))
                throw new InvalidOperationException($"Table \"{oldName}\" does not exist.");
            if (TableExists(target))
                throw new InvalidOperationException($"A table named \"{target}\" already exists.");

            using (var tx = _conn.BeginTransaction())
            {
                Exec(_conn, $"ALTER TABLE {Q(oldName)} RENAME TO {Q(target)};");
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"UPDATE \"{MetaTable}\" SET table_name=@new WHERE table_name=@old;";
                    cmd.Parameters.AddWithValue("@new", target);
                    cmd.Parameters.AddWithValue("@old", oldName);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            return target;
        }

        /// <summary>Cheap word count: metadata first, COUNT(*) only as fallback.</summary>
        public long TryGetWordCount(string tableName)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT word_count FROM \"{MetaTable}\" WHERE table_name=@t;";
                cmd.Parameters.AddWithValue("@t", tableName);
                object o = cmd.ExecuteScalar();
                if (o != null && o != DBNull.Value)
                    return Convert.ToInt64(o);
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {Q(tableName)};";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private static void Exec(SQLiteConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            try { _conn?.Dispose(); } catch { /* ignore */ }
        }
    }
}
