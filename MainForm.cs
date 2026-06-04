using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace wordlist2sql
{
    public sealed class MainForm : Form
    {
        private WordlistDb _db;
        private ExportServer _server;
        private CancellationTokenSource _cts;

        // --- Controls ---
        private TextBox _dbPathBox;
        private Button _openDbBtn;
        private ListView _tablesView;
        private Button _refreshBtn;
        private Button _renameBtn;
        private Button _dropBtn;
        private Button _importBtn;
        private CheckBox _dedupeChk;
        private Button _exportBtn;
        private TextBox _searchBox;
        private CheckBox _revealChk;
        private CheckBox _ignoreCaseChk;
        private CheckBox _partialChk;
        private Button _searchBtn;
        private Label _searchResult;
        private NumericUpDown _portBox;
        private CheckBox _lanChk;
        private Button _serverBtn;
        private Button _lanSetupBtn;
        private Label _serverStatus;
        private TextBox _logBox;
        private ProgressBar _progress;
        private Label _statusLabel;
        private Button _cancelBtn;

        public MainForm()
        {
            Text = "wordlist2sql";
            ClientSize = new Size(900, 620);
            MinimumSize = new Size(760, 560);
            Font = new Font("Segoe UI", 9f);
            LoadAppIcon();
            BuildUi();
            UpdateEnabled();
        }

        private void LoadAppIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var st = asm.GetManifestResourceStream("wordlist2sql.app.ico"))
                {
                    if (st != null)
                        Icon = new Icon(st);
                }
            }
            catch { /* fall back to default icon */ }
        }

        // ---------------------------------------------------------------- UI

        private void BuildUi()
        {
            // ---- Database row ----
            var dbGroup = new GroupBox { Text = "Database", Dock = DockStyle.Top, Height = 64, Padding = new Padding(10) };
            _dbPathBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Text = "(no database open)" };
            _openDbBtn = new Button { Text = "Open / Create…", Dock = DockStyle.Right, Width = 130 };
            _openDbBtn.Click += OnOpenDb;
            var dbInner = new Panel { Dock = DockStyle.Fill };
            dbInner.Controls.Add(_dbPathBox);
            dbInner.Controls.Add(_openDbBtn);
            dbGroup.Controls.Add(dbInner);

            // ---- Tables list ----
            var tablesGroup = new GroupBox { Text = "Tables", Dock = DockStyle.Fill, Padding = new Padding(10) };
            _tablesView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            _tablesView.Columns.Add("Table", 200);
            _tablesView.Columns.Add("Words", 110, HorizontalAlignment.Right);
            _tablesView.Columns.Add("Unique", 70);
            _tablesView.Columns.Add("Source file", 260);
            _tablesView.Columns.Add("Imported (UTC)", 170);
            _tablesView.SelectedIndexChanged += (s, e) => UpdateEnabled();

            var tablesButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight };
            _refreshBtn = new Button { Text = "Refresh", Width = 90 };
            _refreshBtn.Click += (s, e) => RefreshTables();
            _renameBtn = new Button { Text = "Rename…", Width = 90 };
            _renameBtn.Click += OnRenameTable;
            _dropBtn = new Button { Text = "Drop table", Width = 100 };
            _dropBtn.Click += OnDropTable;
            _exportBtn = new Button { Text = "Export to file…", Width = 130 };
            _exportBtn.Click += OnExport;
            tablesButtons.Controls.Add(_refreshBtn);
            tablesButtons.Controls.Add(_renameBtn);
            tablesButtons.Controls.Add(_dropBtn);
            tablesButtons.Controls.Add(_exportBtn);

            tablesGroup.Controls.Add(_tablesView);
            tablesGroup.Controls.Add(tablesButtons);

            // ---- Import controls ----
            var importGroup = new GroupBox { Text = "Import", Dock = DockStyle.Top, Height = 72, Padding = new Padding(10) };
            var importInner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            _importBtn = new Button { Text = "Import wordlist file(s)…", Width = 180, Height = 30 };
            _importBtn.Click += OnImport;
            _dedupeChk = new CheckBox { Text = "De-duplicate (unique words only)", AutoSize = true, Margin = new Padding(16, 8, 0, 0) };
            var importHint = new Label
            {
                Text = "Each file becomes its own table (named after the file).",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(16, 9, 0, 0)
            };
            importInner.Controls.Add(_importBtn);
            importInner.Controls.Add(_dedupeChk);
            importInner.Controls.Add(importHint);
            importGroup.Controls.Add(importInner);

            // ---- Search ----
            var searchGroup = new GroupBox { Text = "Search", Dock = DockStyle.Top, Height = 64, Padding = new Padding(10) };
            var searchInner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            searchInner.Controls.Add(new Label { Text = "Find word:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
            _searchBox = new TextBox { Width = 220, UseSystemPasswordChar = true };
            _searchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnSearch(s, e); }
            };
            _revealChk = new CheckBox { Text = "Show", AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
            _revealChk.CheckedChanged += (s, e) => _searchBox.UseSystemPasswordChar = !_revealChk.Checked;
            _ignoreCaseChk = new CheckBox { Text = "Ignore case", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
            _partialChk = new CheckBox { Text = "Partial match", AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
            _searchBtn = new Button { Text = "Search all tables", Width = 130 };
            _searchBtn.Click += OnSearch;
            _searchResult = new Label { Text = "", AutoSize = true, Margin = new Padding(14, 8, 0, 0), ForeColor = SystemColors.GrayText };
            searchInner.Controls.Add(_searchBox);
            searchInner.Controls.Add(_revealChk);
            searchInner.Controls.Add(_ignoreCaseChk);
            searchInner.Controls.Add(_partialChk);
            searchInner.Controls.Add(_searchBtn);
            searchInner.Controls.Add(_searchResult);
            searchGroup.Controls.Add(searchInner);

            // ---- HTTP server ----
            var serverGroup = new GroupBox { Text = "Curl / HTTP server", Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(10) };
            var serverTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            serverTop.Controls.Add(new Label { Text = "Port:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
            _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 8088, Width = 80 };
            _lanChk = new CheckBox { Text = "Allow LAN access", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
            _serverBtn = new Button { Text = "Start server", Width = 110 };
            _serverBtn.Click += OnToggleServer;
            _lanSetupBtn = new Button { Text = "Set up LAN access (admin)…", Width = 190 };
            _lanSetupBtn.Click += OnSetupLan;
            _serverStatus = new Label { Text = "Stopped.", AutoSize = true, Margin = new Padding(12, 8, 0, 0), ForeColor = SystemColors.GrayText };
            serverTop.Controls.Add(_portBox);
            serverTop.Controls.Add(_lanChk);
            serverTop.Controls.Add(_serverBtn);
            serverTop.Controls.Add(_lanSetupBtn);
            serverTop.Controls.Add(_serverStatus);

            _logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 8.5f) };
            serverGroup.Controls.Add(_logBox);
            serverGroup.Controls.Add(serverTop);

            // ---- Status bar ----
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(10, 6, 10, 6) };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
            var statusRow = new Panel { Dock = DockStyle.Fill };
            _statusLabel = new Label { Dock = DockStyle.Fill, Text = "Ready.", TextAlign = ContentAlignment.MiddleLeft };
            _cancelBtn = new Button { Text = "Cancel", Dock = DockStyle.Right, Width = 90, Enabled = false };
            _cancelBtn.Click += (s, e) => _cts?.Cancel();
            statusRow.Controls.Add(_statusLabel);
            statusRow.Controls.Add(_cancelBtn);
            statusBar.Controls.Add(statusRow);
            statusBar.Controls.Add(_progress);

            // Add in z-order: Fill first, then Top groups inner→outer.
            Controls.Add(tablesGroup);   // Fill
            Controls.Add(searchGroup);   // Top (just above the table list)
            Controls.Add(importGroup);   // Top
            Controls.Add(dbGroup);       // Top
            Controls.Add(serverGroup);   // Bottom
            Controls.Add(statusBar);     // Bottom
        }

        // ----------------------------------------------------------- Helpers

        private void Log(string msg)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => Log(msg))); return; }
            _logBox.AppendText(DateTime.Now.ToString("HH:mm:ss ") + msg + Environment.NewLine);
        }

        private void SetStatus(string msg)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => SetStatus(msg))); return; }
            _statusLabel.Text = msg;
        }

        private bool _busy;
        private void SetBusy(bool busy)
        {
            _busy = busy;
            _cancelBtn.Enabled = busy;
            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            bool hasDb = _db != null;
            bool hasSel = _tablesView != null && _tablesView.SelectedItems.Count > 0;
            bool idle = !_busy;

            _openDbBtn.Enabled = idle;
            _importBtn.Enabled = hasDb && idle;
            _refreshBtn.Enabled = hasDb && idle;
            _renameBtn.Enabled = hasDb && hasSel && idle;
            _dropBtn.Enabled = hasDb && hasSel && idle;
            _exportBtn.Enabled = hasDb && hasSel && idle;
            _serverBtn.Enabled = hasDb;
            _dedupeChk.Enabled = hasDb && idle;
            _searchBox.Enabled = hasDb && idle;
            _searchBtn.Enabled = hasDb && idle;
            _ignoreCaseChk.Enabled = hasDb && idle;
            _partialChk.Enabled = hasDb && idle;
        }

        private string SelectedTable()
            => _tablesView.SelectedItems.Count > 0 ? _tablesView.SelectedItems[0].Text : null;

        // -------------------------------------------------------- DB actions

        private void OnOpenDb(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "Open or create a SQLite database",
                Filter = "SQLite database (*.db;*.sqlite)|*.db;*.sqlite|All files (*.*)|*.*",
                OverwritePrompt = false,
                FileName = "wordlists.db",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    StopServerIfRunning();
                    _db?.Dispose();
                    _db = WordlistDb.Open(dlg.FileName);
                    _dbPathBox.Text = dlg.FileName;
                    SetStatus("Opened " + Path.GetFileName(dlg.FileName));
                    Log("Opened database: " + dlg.FileName);
                    RefreshTables();
                }
                catch (Exception ex)
                {
                    Error("Could not open database", ex);
                }
            }
            UpdateEnabled();
        }

        private void RefreshTables()
        {
            if (_db == null) return;
            _tablesView.BeginUpdate();
            _tablesView.Items.Clear();
            try
            {
                foreach (var t in _db.ListTables())
                {
                    var item = new ListViewItem(t.Name);
                    item.SubItems.Add(t.WordCount >= 0 ? t.WordCount.ToString("n0") : "?");
                    item.SubItems.Add(t.WordCount >= 0 ? (t.Deduped ? "yes" : "no") : "");
                    item.SubItems.Add(t.SourceFile ?? "");
                    item.SubItems.Add(string.IsNullOrEmpty(t.ImportedUtc) ? "" : t.ImportedUtc.Replace("T", " ").Substring(0, Math.Min(19, t.ImportedUtc.Length)));
                    _tablesView.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Error("Could not list tables", ex);
            }
            _tablesView.EndUpdate();
            UpdateEnabled();
        }

        private void OnRenameTable(object sender, EventArgs e)
        {
            string table = SelectedTable();
            if (table == null || _db == null) return;

            string input = Prompt("Rename table", $"New name for \"{table}\":", table);
            if (input == null) return; // cancelled

            try
            {
                string actual = _db.RenameTable(table, input);
                Log($"Renamed table: \"{table}\" → \"{actual}\"");
                SetStatus($"Renamed to \"{actual}\".");
                RefreshTables();

                // Re-select the renamed table for convenience.
                foreach (ListViewItem it in _tablesView.Items)
                {
                    if (it.Text == actual) { it.Selected = true; it.EnsureVisible(); break; }
                }
            }
            catch (Exception ex)
            {
                Error("Could not rename table", ex);
            }
        }

        private void OnDropTable(object sender, EventArgs e)
        {
            string table = SelectedTable();
            if (table == null) return;
            if (MessageBox.Show(this, $"Drop table \"{table}\"? This cannot be undone.",
                    "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                _db.DropTable(table);
                Log("Dropped table: " + table);
                RefreshTables();
            }
            catch (Exception ex)
            {
                Error("Could not drop table", ex);
            }
        }

        // ---------------------------------------------------------- Import

        private async void OnImport(object sender, EventArgs e)
        {
            if (_db == null) return;
            string[] files;
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose wordlist file(s)",
                Filter = "Text wordlists (*.txt;*.lst;*.dic;*.words)|*.txt;*.lst;*.dic;*.words|All files (*.*)|*.*",
                Multiselect = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                files = dlg.FileNames;
            }

            bool dedupe = _dedupeChk.Checked;
            _cts = new CancellationTokenSource();
            SetBusy(true);

            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    string table = WordlistDb.SanitizeTableName(file);

                    // Prompt only if a name clashes with an existing table.
                    if (_db.TableExists(table))
                    {
                        var ans = MessageBox.Show(this,
                            $"Table \"{table}\" already exists. Replace it with \"{Path.GetFileName(file)}\"?",
                            "Table exists", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (ans == DialogResult.Cancel) break;
                        if (ans == DialogResult.No) continue;
                    }

                    int index = i + 1;
                    SetStatus($"Importing {Path.GetFileName(file)} → \"{table}\" ({index}/{files.Length})…");
                    Log($"Import start: {file} → \"{table}\" (dedupe={dedupe})");

                    var progress = new Progress<WordlistDb.ImportProgress>(p =>
                    {
                        int pct = p.TotalBytes > 0 ? (int)(p.BytesRead * 100 / p.TotalBytes) : 0;
                        _progress.Value = Math.Min(100, Math.Max(0, pct));
                        SetStatus($"\"{table}\" ({index}/{files.Length}): {p.WordsInserted:n0} words, {pct}%");
                    });

                    var token = _cts.Token;
                    var result = await Task.Run(
                        () => _db.ImportWordlist(file, table, dedupe, progress, token), token);

                    Log($"Import done: \"{table}\" — {result.WordsInserted:n0} words from {result.LinesRead:n0} lines.");
                    RefreshTables();
                }

                SetStatus("Import complete.");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Import cancelled.");
                Log("Import cancelled by user.");
            }
            catch (Exception ex)
            {
                Error("Import failed", ex);
            }
            finally
            {
                _progress.Value = 0;
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
                RefreshTables();
            }
        }

        // ---------------------------------------------------------- Export

        private async void OnExport(object sender, EventArgs e)
        {
            string table = SelectedTable();
            if (table == null || _db == null) return;

            string outPath;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export table to text file",
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = table + ".txt",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                outPath = dlg.FileName;
            }

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                SetStatus($"Exporting \"{table}\"…");
                Log($"Export start: \"{table}\" → {outPath}");

                var progress = new Progress<WordlistDb.ExportProgress>(p =>
                {
                    int pct = p.TotalWords > 0 ? (int)(p.WordsWritten * 100 / p.TotalWords) : 0;
                    _progress.Value = Math.Min(100, Math.Max(0, pct));
                    SetStatus($"Exporting \"{table}\": {p.WordsWritten:n0} words, {pct}%");
                });

                var token = _cts.Token;
                long written = await Task.Run(
                    () => _db.ExportTable(table, outPath, progress, token), token);

                SetStatus($"Exported {written:n0} words to {Path.GetFileName(outPath)}.");
                Log($"Export done: {written:n0} words → {outPath}");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Export cancelled.");
                Log("Export cancelled by user.");
            }
            catch (Exception ex)
            {
                Error("Export failed", ex);
            }
            finally
            {
                _progress.Value = 0;
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        // ---------------------------------------------------------- Search

        private async void OnSearch(object sender, EventArgs e)
        {
            if (_db == null) return;

            // Read and immediately clear the box so the secret isn't left on screen.
            string word = _searchBox.Text;
            _searchBox.Clear();

            if (string.IsNullOrWhiteSpace(word))
            {
                _searchResult.Text = "Type a word to search for.";
                _searchResult.ForeColor = SystemColors.GrayText;
                return;
            }

            bool ignoreCase = _ignoreCaseChk.Checked;
            bool partial = _partialChk.Checked;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            _searchResult.Text = "Searching…";
            _searchResult.ForeColor = SystemColors.GrayText;
            // Note: the searched word is never logged or displayed — only the mode.
            string mode = (partial ? "partial" : "exact") + (ignoreCase ? ", ignore case" : ", case-sensitive");
            Log($"Search started across all tables ({mode}).");

            try
            {
                // Progress reports the current table name only — not the word.
                var progress = new Progress<string>(table => SetStatus($"Searching \"{table}\"…"));
                var token = _cts.Token;

                var res = await Task.Run(() => _db.SearchWord(word, ignoreCase, partial, progress, token), token);

                if (res.TablesContaining.Count == 0)
                {
                    _searchResult.Text = $"Not found in any of {res.TablesSearched} table(s).";
                    _searchResult.ForeColor = Color.Firebrick;
                    Log($"Search complete: no match in {res.TablesSearched} table(s).");
                    ClearTableHighlight();
                }
                else
                {
                    string list = string.Join(", ", res.TablesContaining);
                    _searchResult.Text = $"Found in {res.TablesContaining.Count} of {res.TablesSearched}: {list}";
                    _searchResult.ForeColor = Color.ForestGreen;
                    Log($"Search complete: found in {res.TablesContaining.Count} table(s): {list}");
                    HighlightTables(res.TablesContaining);
                }

                SetStatus("Search complete.");
            }
            catch (OperationCanceledException)
            {
                _searchResult.Text = "Search cancelled.";
                _searchResult.ForeColor = SystemColors.GrayText;
                SetStatus("Search cancelled.");
                Log("Search cancelled by user.");
            }
            catch (Exception ex)
            {
                Error("Search failed", ex);
            }
            finally
            {
                // Scrub the captured secret from memory's reach as best we can.
                word = null;
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        private void ClearTableHighlight()
        {
            foreach (ListViewItem it in _tablesView.Items)
                it.BackColor = _tablesView.BackColor;
        }

        private void HighlightTables(System.Collections.Generic.List<string> names)
        {
            var set = new System.Collections.Generic.HashSet<string>(names, StringComparer.Ordinal);
            foreach (ListViewItem it in _tablesView.Items)
                it.BackColor = set.Contains(it.Text) ? Color.FromArgb(216, 244, 216) : _tablesView.BackColor;
        }

        // ---------------------------------------------------------- Server

        private void OnToggleServer(object sender, EventArgs e)
        {
            if (_server != null) { StopServerIfRunning(); return; }
            if (_db == null) return;

            try
            {
                int port = (int)_portBox.Value;
                bool lan = _lanChk.Checked;
                _server = new ExportServer(_db.DatabasePath, port, lan);
                _server.Log += Log;
                _server.Start();
                _serverBtn.Text = "Stop server";
                _serverStatus.Text = (lan ? "LAN" : "Local") + $" — running at {_server.BaseUrl}";
                _serverStatus.ForeColor = Color.ForestGreen;
                _portBox.Enabled = false;
                _lanChk.Enabled = false;
                Log($"Try:  curl {_server.BaseUrl}{(SelectedTable() ?? "<table>")} -o out.txt");
            }
            catch (Exception ex)
            {
                _server = null;
                Error("Could not start server", ex);
            }
        }

        /// <summary>
        /// One-time elevated setup so LAN binding works without running the
        /// whole app as admin: reserve the URL ACL and open the firewall port.
        /// Triggers a single UAC prompt.
        /// </summary>
        private void OnSetupLan(object sender, EventArgs e)
        {
            int port = (int)_portBox.Value;

            if (MessageBox.Show(this,
                    $"This will (as Administrator) allow LAN access on port {port}:\r\n\r\n" +
                    $"  • Reserve URL  http://+:{port}/  (so the app can bind without admin)\r\n" +
                    $"  • Add an inbound firewall rule for TCP {port} (Private network)\r\n\r\n" +
                    "Windows will show a UAC prompt. Continue?",
                    "Set up LAN access", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
                != DialogResult.OK)
                return;

            // Build both commands; '&' so the firewall rule is still added even
            // if the URL ACL already exists. /c keeps the window only as long as
            // needed; we read the exit code to report success.
            string cmd =
                $"netsh http add urlacl url=http://+:{port}/ user=Everyone & " +
                $"netsh advfirewall firewall add rule name=\"wordlist2sql {port}\" " +
                $"dir=in action=allow protocol=TCP localport={port} profile=private";

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    UseShellExecute = true,   // required for Verb=runas
                    Verb = "runas",           // elevate (UAC)
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0)
                    {
                        Log($"LAN setup applied for port {port} (URL ACL + firewall rule).");
                        SetStatus("LAN access configured. Tick 'Allow LAN access' and start the server.");
                        MessageBox.Show(this,
                            $"LAN access is set up for port {port}.\r\n\r\n" +
                            "Now tick \"Allow LAN access\" and click Start server.",
                            "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        _lanChk.Checked = true;
                    }
                    else
                    {
                        Log($"LAN setup finished with exit code {p.ExitCode} (a rule/ACL may already exist).");
                        SetStatus("LAN setup ran; check the log.");
                    }
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                Log("LAN setup cancelled at the UAC prompt.");
                SetStatus("LAN setup cancelled.");
            }
            catch (Exception ex)
            {
                Error("LAN setup failed", ex);
            }
        }

        private void StopServerIfRunning()
        {
            if (_server == null) return;
            try { _server.Stop(); } catch { }
            _server = null;
            _serverBtn.Text = "Start server";
            _serverStatus.Text = "Stopped.";
            _serverStatus.ForeColor = SystemColors.GrayText;
            _portBox.Enabled = true;
            _lanChk.Enabled = true;
        }

        // ------------------------------------------------------------- misc

        /// <summary>Simple modal text-input dialog. Returns null if cancelled.</summary>
        private string Prompt(string title, string label, string initial)
        {
            using (var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(380, 110),
            })
            {
                var lbl = new Label { Text = label, AutoSize = true, Left = 12, Top = 14 };
                var txt = new TextBox { Left = 12, Top = 36, Width = 356, Text = initial };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 212, Top = 70, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 293, Top = 70, Width = 75 };
                dlg.Controls.Add(lbl);
                dlg.Controls.Add(txt);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                txt.SelectAll();

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                string value = txt.Text.Trim();
                return value.Length == 0 ? null : value;
            }
        }

        private void Error(string title, Exception ex)
        {
            Log($"ERROR: {title}: {ex.Message}");
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus(title + " — see log.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            StopServerIfRunning();
            _db?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
