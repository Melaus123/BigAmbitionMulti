using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace BigAmbitionsMP
{
    /// <summary>
    /// File picker for bug-report attachments. Shows the OS "Open file" dialog OUT OF PROCESS
    /// (Windows: PowerShell's WinForms OpenFileDialog; macOS: osascript's "choose file"), so it
    /// can NEVER crash the game: the
    /// dialog lives in a separate process and never touches the game's render device or threads.
    /// The previous in-process Win32 COM dialog hard-crashed the game when shown over it (the
    /// game's main thread was blocked while a native modal dialog ran over the fullscreen surface
    /// — uncatchable, 2026-06-19).
    ///
    /// Blocks until the user finishes picking — call it from a BACKGROUND thread, never the main
    /// thread (the caller marshals the result back to the main thread).
    /// </summary>
    internal static class NativeFilePicker
    {
        public static string[] PickBugReportAttachments()
        {
            // macOS has no PowerShell/WinForms; same out-of-process idea, through osascript.
            if (UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXPlayer ||
                UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXEditor)
                return PickMac();

            string tmp     = Path.GetTempPath();
            string stamp   = "bamp-pick-" + Guid.NewGuid().ToString("N");
            string outFile = Path.Combine(tmp, stamp + ".txt");
            string ps1     = Path.Combine(tmp, stamp + ".ps1");

            try
            {
                // The dialog writes the chosen paths (one per line) to outFile; cancel writes nothing.
                string outLiteral = outFile.Replace("'", "''");
                string script =
                    "Add-Type -AssemblyName System.Windows.Forms\n" +
                    "$d = New-Object System.Windows.Forms.OpenFileDialog\n" +
                    "$d.Multiselect = $true\n" +
                    "$d.Title = 'Attach files to your BigAmbitionsMP bug report'\n" +
                    "$d.Filter = 'Report files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.txt;*.log;*.json;*.zip|All files|*.*'\n" +
                    "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $d.FileNames | Set-Content -LiteralPath '" + outLiteral + "' -Encoding UTF8 }\n";
                File.WriteAllText(ps1, script);

                // Field 20260821-181447: with UseShellExecute=false the bare name resolves against
                // the GAME PROCESS's PATH, and on that machine (Steam-launched, pt-BR Windows) it
                // wasn't there — "O sistema não pode encontrar o arquivo especificado". The System32
                // path is fixed on every Windows; the bare name stays as the fallback.
                string psExe = "powershell.exe";
                try
                {
                    string sys = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        @"System32\WindowsPowerShell\v1.0\powershell.exe");
                    if (File.Exists(sys)) psExe = sys;
                }
                catch { }
                var psi = new ProcessStartInfo
                {
                    FileName        = psExe,
                    Arguments       = "-NoProfile -ExecutionPolicy Bypass -Sta -WindowStyle Hidden -File \"" + ps1 + "\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return Array.Empty<string>();
                    if (!proc.WaitForExit(180000)) { try { proc.Kill(); } catch { } return Array.Empty<string>(); }
                }

                if (!File.Exists(outFile)) return Array.Empty<string>();   // user cancelled

                var files = new List<string>();
                foreach (var raw in File.ReadAllLines(outFile))
                {
                    string p = raw.Trim();
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) files.Add(p);
                }
                return files.ToArray();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] file picker failed: {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                try { if (File.Exists(ps1))     File.Delete(ps1); }     catch { }
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        /// <summary>macOS half: Finder's own multi-select "choose file" sheet, run out of process
        /// by osascript, which prints one POSIX path per line. Same contract as the Windows path —
        /// blocks, returns only files that exist, empty array on cancel or failure.</summary>
        private static string[] PickMac()
        {
            string tmp    = Path.GetTempPath();
            string stamp  = "bamp-pick-" + Guid.NewGuid().ToString("N");
            string script = Path.Combine(tmp, stamp + ".applescript");

            try
            {
                File.WriteAllText(script,
                    "set fs to choose file with prompt \"Attach files to your BigAmbitionsMP bug report\" with multiple selections allowed\n" +
                    "set out to \"\"\n" +
                    "repeat with f in fs\n" +
                    "set out to out & POSIX path of f & linefeed\n" +
                    "end repeat\n" +
                    "return out\n");

                var psi = new ProcessStartInfo
                {
                    FileName               = "/usr/bin/osascript",
                    Arguments              = "\"" + script + "\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                };

                string stdout;
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return Array.Empty<string>();
                    // Drain the pipe WHILE waiting: a full pipe would deadlock the wait, and a
                    // blocking ReadToEnd before the wait would make the 180 s guard unreachable
                    // (review 2026-09-16) - the async read completes when the process exits or is killed.
                    var read = proc.StandardOutput.ReadToEndAsync();
                    if (!proc.WaitForExit(180000)) { try { proc.Kill(); } catch { } return Array.Empty<string>(); }
                    stdout = read.GetAwaiter().GetResult();
                    if (proc.ExitCode != 0) return Array.Empty<string>();   // user cancelled
                }

                if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<string>();

                var files = new List<string>();
                foreach (var raw in stdout.Split('\n'))
                {
                    string p = raw.Trim();
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) files.Add(p);
                }
                return files.ToArray();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] file picker failed: {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                try { if (File.Exists(script)) File.Delete(script); } catch { }
            }
        }
    }
}
