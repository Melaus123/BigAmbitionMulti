namespace BigAmbitionsMP
{
    internal static class MyPluginInfo
    {
        public const string PLUGIN_GUID = "com.bigambitions.multiplayer";
        public const string PLUGIN_NAME = "BigAmbitionsMP";
        public const string PLUGIN_VERSION = "0.1.4";

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
