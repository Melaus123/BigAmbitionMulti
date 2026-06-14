using System;

namespace BigAmbitionsMP
{
    /// <summary>
    /// The single resolver from a multiplayer PlayerId (lobby/network identity) to
    /// the CHARACTER NAME shown in-game.  Project rule: PlayerId must NEVER reach
    /// visible in-game text — every in-world label, notice and chat sender routes
    /// through here; PlayerId stays the routing/ownership key only.
    ///
    /// Role-aware: the host process resolves via its character-name map, a client
    /// via its rival-name map, and the LOCAL player always via its own live
    /// CharacterData.  Falls back to the PlayerId only when no name is known yet
    /// (player still in character creation, or their profile hasn't arrived).
    ///
    /// MAIN-THREAD ONLY for the local-player path (reads IL2CPP CharacterData).
    /// </summary>
    public static class MPNames
    {
        private static string _localName = "";

        /// <summary>The local player's in-character name (cached); PlayerId if not
        /// available yet.  Reads CharacterData — call on the main thread.</summary>
        public static string LocalCharacterName()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.charactersData != null && gi.charactersData.Count > 0)
                {
                    var cn = gi.charactersData[0]?.name?.ToString();
                    if (!string.IsNullOrWhiteSpace(cn)) { _localName = cn; return cn; }
                }
            }
            catch { }
            return string.IsNullOrEmpty(_localName) ? MPConfig.PlayerId : _localName;
        }

        /// <summary>PlayerId → in-character display name.  Never returns empty.</summary>
        public static string Resolve(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "";
            if (playerId == MPConfig.PlayerId) return LocalCharacterName();
            string n = MPServer.IsRunning
                ? MPServer.DisplayNameFor(playerId)
                : RemotePlayerManager.ResolveDisplayName(playerId);
            return string.IsNullOrWhiteSpace(n) ? playerId : n;
        }
    }
}
