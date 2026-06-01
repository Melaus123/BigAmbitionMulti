using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// In-game chat hub (Phase 6).  Holds the rolling chat log and routes locally
    /// submitted lines onto the wire.  Lines arrive on the network poll thread
    /// (AddLine) and are read on the Unity main thread (Tail) — guarded by a lock.
    ///
    /// Routing model (same as the rest of the host-authoritative protocol):
    ///   * Host submits  → append to own log + Broadcast Chat to all clients.
    ///   * Client submits → send Chat to host (no optimistic local echo); the host
    ///                      relays it back so the sender sees it once, in host order.
    ///   * On Chat received → append to the local log.
    /// </summary>
    public static class MPChat
    {
        private const int MaxLines = 100;

        private static readonly object       _lock  = new();
        private static readonly List<string> _lines = new();

        /// <summary>Bumped on every append so the UI can cheaply detect changes
        /// and only rebuild the visible log when something new arrived.</summary>
        public static int Version { get; private set; }

        /// <summary>True while the in-game MP window is being typed in / interacted with.
        /// Harmony patches force GameManager.HasInputSelected / ShouldBlockKeyboardShortcuts
        /// true while this is set, so the game treats the window like one of its own text
        /// fields — suppressing movement, camera and hotkeys (and world click-through).</summary>
        public static bool SuppressGameInput;

        /// <summary>Append a received/own line to the rolling log.  Thread-safe.</summary>
        public static void AddLine(string who, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length > 200) text = text.Substring(0, 200);
            string line = string.IsNullOrEmpty(who) ? text : $"{who}:  {text}";
            lock (_lock)
            {
                _lines.Add(line);
                if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
                Version++;
            }
        }

        /// <summary>System/notice line (no sender prefix), e.g. "X joined".</summary>
        public static void AddNotice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (_lock)
            {
                _lines.Add("— " + text.Trim());
                if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
                Version++;
            }
        }

        /// <summary>The last <paramref name="n"/> lines, oldest-first.  Thread-safe copy.</summary>
        public static List<string> Tail(int n)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _lines.Count - n);
                return _lines.GetRange(start, _lines.Count - start);
            }
        }

        /// <summary>The user submitted a chat line from the in-game window.  Routes
        /// it according to role.  Safe to call on the main thread.</summary>
        public static void SendFromLocal(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string who = MPConfig.PlayerId;
            try
            {
                if (MPServer.IsRunning)
                {
                    AddLine(who, text);                 // host sees its own line immediately
                    MPServer.BroadcastChat(who, text);  // relay to every client
                }
                else if (MPClient.IsConnected)
                {
                    MPClient.SendChat(text);            // host relays it back to us
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] SendFromLocal: {ex.Message}"); }
        }
    }
}
