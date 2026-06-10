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

        private static readonly object         _lock  = new();
        private static readonly List<ChatLine> _lines = new();

        /// <summary>Bumped on every append so the UI / unread badge can cheaply
        /// detect changes.</summary>
        public static int Version { get; private set; }

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

        /// <summary>System/notice line, e.g. "X joined".</summary>
        public static void AddNotice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Append(new ChatLine { Text = text.Trim(), Notice = true });
        }

        private static void Append(ChatLine line)
        {
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
    }
}
