using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace wordlist2sql_launcher
{
    /// <summary>
    /// Native-AOT bootstrapper for wordlist2sql.
    ///
    /// Because it is AOT-compiled it has no .NET runtime dependency of its own,
    /// so it can run on a machine where the runtime is missing. It:
    ///   1. checks whether the .NET 8+ Desktop Runtime is installed,
    ///   2. if not, offers to download and install it from Microsoft, then
    ///   3. launches the framework-dependent wordlist2sql.exe.
    /// </summary>
    internal static class Program
    {
        private const string AppExe = "wordlist2sql.exe";
        private const int RequiredMajor = 8;

        // Official Microsoft redirector → latest .NET 8 Desktop Runtime (x64).
        private const string DownloadUrl =
            "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
        private const string ManualPage =
            "https://dotnet.microsoft.com/download/dotnet/8.0/runtime";

        // --- Win32 message boxes (WinForms isn't AOT-friendly) ---
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private const uint MB_OK = 0x0, MB_YESNO = 0x4;
        private const uint MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFO = 0x40;
        private const int IDYES = 6;

        private const string Caption = "wordlist2sql launcher";

        private static int Main(string[] args)
        {
            string baseDir = AppContext.BaseDirectory;
            string appPath = Path.Combine(baseDir, AppExe);

            if (!File.Exists(appPath))
            {
                MessageBoxW(IntPtr.Zero,
                    $"Could not find {AppExe} next to this launcher.\n\nExpected at:\n{appPath}",
                    Caption, MB_OK | MB_ICONERROR);
                return 1;
            }

            if (!DesktopRuntimeInstalled())
            {
                int choice = MessageBoxW(IntPtr.Zero,
                    "wordlist2sql needs the Microsoft .NET 8 Desktop Runtime, which doesn't " +
                    "appear to be installed on this PC.\n\n" +
                    "Download (~55 MB) and install it now?",
                    Caption, MB_YESNO | MB_ICONQUESTION);

                if (choice != IDYES)
                    return 2; // user declined

                if (!DownloadAndInstall())
                    return 3; // install failed/cancelled (message already shown)

                if (!DesktopRuntimeInstalled())
                {
                    MessageBoxW(IntPtr.Zero,
                        "The .NET Desktop Runtime still isn't detected after installation.\n\n" +
                        "Please install it manually and run wordlist2sql again.",
                        Caption, MB_OK | MB_ICONERROR);
                    OpenUrl(ManualPage);
                    return 4;
                }
            }

            // Runtime present — start the app and exit.
            try
            {
                var psi = new ProcessStartInfo(appPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = baseDir,
                };
                foreach (string a in args)
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBoxW(IntPtr.Zero, "Failed to start wordlist2sql:\n\n" + ex.Message,
                    Caption, MB_OK | MB_ICONERROR);
                return 5;
            }
        }

        /// <summary>
        /// True if a Microsoft.WindowsDesktop.App shared framework of the required
        /// major version (or newer) is installed in any standard dotnet location.
        /// </summary>
        private static bool DesktopRuntimeInstalled()
        {
            foreach (string root in DotnetRoots())
            {
                string fxDir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(fxDir))
                    continue;

                foreach (string verDir in Directory.GetDirectories(fxDir))
                {
                    string name = Path.GetFileName(verDir);
                    int dot = name.IndexOf('.');
                    string majorText = dot > 0 ? name.Substring(0, dot) : name;
                    if (int.TryParse(majorText, out int major) && major >= RequiredMajor)
                        return true;
                }
            }
            return false;
        }

        private static System.Collections.Generic.IEnumerable<string> DotnetRoots()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new System.Collections.Generic.List<string>();

            void Add(string p)
            {
                if (!string.IsNullOrEmpty(p) && seen.Add(p))
                    roots.Add(p);
            }

            Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            string pf = Environment.GetEnvironmentVariable("ProgramW6432")
                        ?? Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(pf))
                Add(Path.Combine(pf, "dotnet"));
            Add(@"C:\Program Files\dotnet");
            return roots;
        }

        private static bool DownloadAndInstall()
        {
            string installer = Path.Combine(Path.GetTempPath(),
                "windowsdesktop-runtime-8-x64.exe");

            try
            {
                DownloadFile(DownloadUrl, installer).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                int r = MessageBoxW(IntPtr.Zero,
                    "Could not download the .NET Desktop Runtime automatically:\n\n" +
                    ex.Message + "\n\nOpen the download page in your browser instead?",
                    Caption, MB_YESNO | MB_ICONERROR);
                if (r == IDYES) OpenUrl(ManualPage);
                return false;
            }

            try
            {
                // The bundle auto-elevates (UAC). /passive shows a progress bar
                // but needs no clicks; /norestart avoids a surprise reboot.
                var psi = new ProcessStartInfo(installer)
                {
                    UseShellExecute = true,
                    Arguments = "/install /passive /norestart",
                };
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit();
                    int code = proc.ExitCode;
                    // 0 = success, 3010 = success but reboot required.
                    if (code != 0 && code != 3010)
                    {
                        MessageBoxW(IntPtr.Zero,
                            $"The runtime installer exited with code {code}.\n\n" +
                            "Installation may have been cancelled or failed.",
                            Caption, MB_OK | MB_ICONERROR);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // Most likely the UAC prompt was declined.
                MessageBoxW(IntPtr.Zero,
                    "The runtime installer could not be started:\n\n" + ex.Message,
                    Caption, MB_OK | MB_ICONERROR);
                return false;
            }
            finally
            {
                try { if (File.Exists(installer)) File.Delete(installer); } catch { }
            }
        }

        private static async Task DownloadFile(string url, string destPath)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(10);
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                               FileShare.None, 1 << 20))
                    {
                        await src.CopyToAsync(dst);
                    }
                }
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* ignore */ }
        }
    }
}
