using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace BigAmbitionsMP
{
    /// <summary>
    /// File picker for bug-report attachments. Shows the OS "Open file" dialog OUT OF PROCESS
    /// (via Windows PowerShell's WinForms OpenFileDialog), so it can NEVER crash the game: the
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

                var psi = new ProcessStartInfo
                {
                    FileName        = "powershell.exe",
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
    }
}
