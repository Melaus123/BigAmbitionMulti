#if BAMP_DEV
using System;

namespace BigAmbitionsMP
{
    /// <summary>T-34 (bundle bamp-bug-20261107-223938) DEV lever.
    /// `stampset &lt;addr&gt; &lt;rivalId|pid|clear&gt;` writes the game's own deed stamp (BuildingRegistration.buildingOwnerRivalId)
    /// on THIS machine only - nothing is sent and the deed ledger is untouched - so the stamp and the ledger can be made to
    /// disagree (the bundle's premise: two of a guest's deeds stamped to computer rivals on the host). The answer names the
    /// ledger's deed (host) so a scenario can assert the premise; the rivals app's count is read with the `rivals` lever.
    /// `stampset &lt;addr&gt; read` writes nothing: it answers the stamp as this machine's game holds it (T-34 root fix: the
    /// host's stamp after a guest's buy, without the lever having written it).</summary>
    internal static class DevT34
    {
        internal static string StampSet(string arg)
        {
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return "ERR usage: stampset <addr> <rivalId|pid|clear|read>";
            string who = parts[parts.Length - 1], addr = string.Join(" ", parts, 0, parts.Length - 1);
            var reg = GameStatePatcher.FindRegistration(addr);
            if (reg == null) return $"ERR no registration for '{addr}'";
            string key = GameStateReader.AddressKey(reg);
            string before = reg.buildingOwnerRivalId ?? "";
            string deed = MPServer.IsRunning
                ? (MPServer.BuildingRealEstateOwners.TryGetValue(key, out var d) ? d ?? "" : "")
                : "n/a";
            if (who == "read") return $"OK stampread addr='{key}' stamp='{before}' deed='{deed}'";
            reg.buildingOwnerRivalId = who == "clear" ? string.Empty : who;
            return $"OK stampset addr='{key}' stamp='{reg.buildingOwnerRivalId ?? ""}' was='{before}' deed='{deed}'";
        }
    }
}
#endif
