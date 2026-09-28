using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>One chat entry — structured so the UI can color/route properly.</summary>
    public sealed class ChatLine
    {
        public string From = "";    // sender player id ("" for notices)
        public string To   = "";    // recipient player id ("" = everyone)
        public string Text = "";
        public bool   Notice;       // system line ("X joined")
        // In-game day + clock when the line was added LOCALLY (Day 0 = unknown). Display only (the chat window's
        // 'Day {n} · {hh:mm}' time lines) - never on the wire.
        public int    Day, Hour, Minute;
    }

    /// <summary>
    /// In-game chat hub.  Holds the rolling structured log and routes locally
    /// submitted lines onto the wire.  Lines arrive on the network poll thread
    /// and are read on the Unity main thread — guarded by a lock.
    ///
    /// Routing model (host-authoritative):
    ///   * PUBLIC — host appends + broadcasts; clients send to host, who relays
    ///     to everyone (sender included → host-ordered echo).
    ///   * PRIVATE — delivered only to the recipient (host relays client→client);
    ///     the sender renders a local echo immediately.  The HOST process relays
    ///     all traffic, so "private" means private from other PLAYERS.
    /// </summary>
    public static class MPChat
    {
        private const int MaxLines = 200;

        /// <summary>Sender id of Business Hub notes (MPHub.NotifyParty). It starts with a LINE BREAK, which no honest player id
        /// can hold (ids come from the single-line name box or a Steam name), so no real player can collide with it (review
        /// R1: "Hub" could be anyone's name). Only a hand-made Hello could claim it - the host drops that player's chat.</summary>
        internal const string HubSender = "\nhub";

        private static readonly object         _lock  = new();
        private static readonly List<ChatLine> _lines = new();

        /// <summary>Bumped on every append so the UI / unread badge can cheaply
        /// detect changes.</summary>
        public static int Version { get; private set; }

        // The game's day + clock (SaveGameManager.Current Day/Hour/Minute), handed in on the MAIN thread twice a
        // second (MPCanvasUI.CwTickClock) and packed into one int so Append can stamp lines on any thread.
        private static volatile int _clock;
        internal static void SetClock(int day, int hour, int minute)
        {
            try { _clock = day > 0 ? day * 10000 + Math.Max(0, Math.Min(23, hour)) * 100 + Math.Max(0, Math.Min(59, minute)) : 0; }
            catch { }
        }
        internal static int ClockPacked => _clock;

        /// <summary>True while the in-game MP window is being typed in / interacted with.
        /// Harmony patches force GameManager.HasInputSelected / ShouldBlockKeyboardShortcuts
        /// true while this is set, so the game treats the window like one of its own text
        /// fields — suppressing movement, camera and hotkeys (and world click-through).</summary>
        public static bool SuppressGameInput;

        /// <summary>Append a message.  Thread-safe.</summary>
        public static void AddMessage(string from, string to, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length > 200) text = text.Substring(0, 200);
            Append(new ChatLine { From = from ?? "", To = to ?? "", Text = text });
        }

        /// <summary>Legacy entry point (network receive paths).</summary>
        public static void AddLine(string who, string text) => AddMessage(who, "", text);

        /// <summary>System/status feedback.  RE-ROUTED (user 2026-07-20): chat is a
        /// player-to-player channel ONLY — status lines in it made the phone blink
        /// like a received message.  Former notices now flash as a transient toast
        /// (marshalled — callers include poll-thread handlers); nothing is appended
        /// to the chat log and the unread badge never moves.  Response-required
        /// events (loans/gifts) badge the Business Hub icon, which their handlers
        /// already do via MPHub.Version.</summary>
        public static void AddNotice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string t = text.Trim();
            GameStatePatcher.EnqueueOnMainThread(() => PassengerHud.Toast(t, 3f));
        }

        private static void Append(ChatLine line)
        {
            if (line.Day <= 0)
            {
                int c = _clock;
                if (c > 0) { line.Day = c / 10000; line.Hour = c / 100 % 100; line.Minute = c % 100; }
            }
            lock (_lock)
            {
                _lines.Add(line);
                if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
                Version++;
            }
        }

        /// <summary>The last <paramref name="n"/> lines, oldest-first.  Thread-safe copy.</summary>
        public static List<ChatLine> Snapshot(int n)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _lines.Count - n);
                return _lines.GetRange(start, _lines.Count - start);
            }
        }

        /// <summary>All lines, oldest-first, into a reused list (no allocation once it has grown).</summary>
        public static void CopyAll(List<ChatLine> dst)
        {
            lock (_lock) { dst.Clear(); dst.AddRange(_lines); }
        }

        /// <summary>The user submitted a chat line from the in-game window.
        /// to = "" → everyone; otherwise a player id for a private message.</summary>
        public static void SendFromLocal(string text, string to = "")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string who = MPConfig.PlayerId;
            to ??= "";
            try
            {
                if (MPServer.IsRunning)
                {
                    AddMessage(who, to, text);                       // host sees its line immediately
                    if (string.IsNullOrEmpty(to))   MPServer.BroadcastChat(who, text);
                    else if (to != who)             MPServer.SendChatPrivate(who, to, text);
                }
                else if (MPClient.IsConnected)
                {
                    // Public: the host relays it back (host-ordered echo).
                    // Private: the host will NOT echo it back — local echo now.
                    if (!string.IsNullOrEmpty(to)) AddMessage(who, to, text);
                    MPClient.SendChat(text, to);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] SendFromLocal: {ex.Message}"); }
        }
#if BAMP_DEV
        /// <summary>DEV (TestDrive `uifill chat`): append a LOCAL display-only line. Never sent, never saved.</summary>
        internal static void DevAppendLocal(ChatLine line)
        {
            try { if (line != null && !string.IsNullOrEmpty(line.Text)) Append(line); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] DevAppendLocal: {ex.Message}"); }
        }

        /// <summary>DEV (TestDrive `uifill clear`): remove lines that `uifill chat` appended. Returns the count, -1 on error.</summary>
        internal static int DevRemove(Predicate<ChatLine> match)
        {
            try { lock (_lock) { int n = _lines.RemoveAll(match); if (n > 0) Version++; return n; } }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] DevRemove: {ex.Message}"); return -1; }
        }
#endif
    }
}
