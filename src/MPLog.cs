using System;
using System.IO;
using System.Text;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Observability layer on top of ModLog.  Two jobs:
    ///   1. A shared SESSION ID — the host generates it on Start and ships it to
    ///      clients in the world snapshot; both machines log a banner with it so
    ///      two separate Player.logs can be correlated to the same session.
    ///   2. An always-on RING BUFFER of the most recent log lines, dumped to a
    ///      file on an unexpected disconnect — so a field bug carries its own
    ///      recent history without needing a special diagnostic build pre-armed.
    /// Every Plugin.Logger.* call feeds Record() (wired in ModLog), so the ring
    /// captures the whole mod's output with no per-call-site changes.
    /// </summary>
    public static class MPLog
    {
        public static string SessionId { get; private set; } = "";

        private const int RingSize = 500;
        private static readonly object _lock = new();
        private static readonly string[] _ring = new string[RingSize];
        private static int _head;     // next write slot
        private static int _count;

        /// <summary>Record one line into the ring.  Cheap + thread-safe; called
        /// from ModLog for every log call, so it must never throw.</summary>
        public static void Record(string level, string msg)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
                lock (_lock)
                {
                    _ring[_head] = line;
                    _head = (_head + 1) % RingSize;
                    if (_count < RingSize) _count++;
                }
            }
            catch { }
        }

        /// <summary>Adopt a session id and log the correlation banner.  Host calls
        /// this on Start with a fresh id; clients call it from the world snapshot
        /// with the host's id so both logs share it.</summary>
        public static void BeginSession(string id, string role)
        {
            SessionId = id ?? "";
            Plugin.Logger.LogInfo(
                $"[Session] id={SessionId} role={role} mod=v{MyPluginInfo.PLUGIN_VERSION} player='{MPConfig.PlayerId}' — correlate host/client logs by this id.");
        }

        /// <summary>Write the ring buffer to a file (recent history at the moment
        /// of a disconnect/anomaly).  Returns the path, or "" on failure.</summary>
        public static string Dump(string reason)
        {
            try
            {
                string dir = LogDir();
                Directory.CreateDirectory(dir);
                string sid  = string.IsNullOrEmpty(SessionId) ? "nosession" : SessionId;
                string path = Path.Combine(dir, $"bamp-ring-{sid}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                string role = MPServer.IsRunning ? "host" : (MPClient.IsConnected ? "client" : "offline");
                var sb = new StringBuilder();
                sb.AppendLine($"# BAMP ring dump — reason='{reason}' session={sid} role={role} player='{MPConfig.PlayerId}' at {DateTime.Now:O}");
                lock (_lock)
                {
                    int start = (_count < RingSize) ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        sb.AppendLine(_ring[(start + i) % RingSize]);
                }
                File.WriteAllText(path, sb.ToString());
                Plugin.Logger.LogInfo($"[Session] ring dump ({_count} lines, reason='{reason}') → {path}");
                return path;
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogWarning($"[Session] ring dump failed: {ex.Message}"); } catch { }
                return "";
            }
        }

        private static string LogDir()
        {
            try
            {
                string root = MPConfig.DataRootPath;
                if (!string.IsNullOrEmpty(root) && root != ".") return Path.Combine(root, "logs");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "BigAmbitionsMP-logs");
        }
    }
}
