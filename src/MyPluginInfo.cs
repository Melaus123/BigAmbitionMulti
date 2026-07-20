namespace BigAmbitionsMP
{
    internal static class MyPluginInfo
    {
        public const string PLUGIN_GUID = "com.bigambitions.multiplayer";
        public const string PLUGIN_NAME = "BigAmbitionsMP";
        public const string PLUGIN_VERSION = "0.1.12";

        /// <summary>Official public name (Steam Workshop title, UI, bug reports,
        /// release notes).  NAMING POLICY (2026-07-09): only DISPLAY text uses
        /// this — every technical identifier stays "BigAmbitionsMP"/"BAMP_"
        /// because it is install- or save-persisted: the assembly + ModsLocal
        /// folder name (existing installs), PLUGIN_GUID/Harmony id, _BAMP_MP
        /// save folders, BAMP_DUTY_ employee ids (repair sweeps key on the
        /// prefix), config paths, the "BAMP:version:" wire tag, and the
        /// X-BAMP-Key relay header.</summary>
        public const string DISPLAY_NAME = "Going Public: Multiplayer for Big Ambitions";
        /// <summary>Short form for tight UI spots.</summary>
        public const string SHORT_NAME = "Going Public";

        /// <summary>True only in a `-c Dev` build (BAMP_DEV defined) — the
        /// test/diagnostic scaffolding is compiled in.  Shipped builds are false.</summary>
        public const bool IsDevBuild =
#if BAMP_DEV
            true;
#else
            false;
#endif

        /// <summary>Human-readable build tag.  Logged at startup AND embedded in the
        /// DLL (the log reference inlines the literal), so the packaging script can
        /// byte-scan the DLL and refuse to ship a "DEV build" by mistake.</summary>
        public const string BuildTag =
#if BAMP_DEV
            "DEV build (diagnostics on)";
#else
            "release build";
#endif
    }
}
