#if BAMP_DEV
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>T-28 EVIDENCE probe (dev build only; registered probe, no fix): "the other player seems to hold a
    /// box they don't have". Read 2026-11-13: RemotePlayerManager.SpawnRemotePlayer copies the LOCAL character's
    /// whole "Model" (Instantiate, ~1862-1865) - HandContent included - and ApplyHeldProp removes only its own
    /// "BAMP_Held_*" children (~1356). Lever `handbox`:
    ///   hold            - the local player takes a closed cardboard box through the game's own carry path
    ///                     (PlayerHelper.ItemInstanceInHands setter -> AddItemToHands -> ItemPanelUI.SetItemInstance
    ///                     -> PrefabHelper.CreatePrefabItem + ThirdPersonCharacter.SetHandContent)
    ///   drop            - the game's own RemoveItemsFromHands (ItemInstanceInHands = null)
    ///   respawn &lt;pid&gt;   - RemotePlayerManager.Remove(pid); the next position packet re-spawns the figure through
    ///                     SpawnOrUpdate -> SpawnRemotePlayer (the body copy). Side effect of Remove: that
    ///                     player's vehicles are despawned on this machine (VehicleManager.DespawnAllOwnedBy).
    ///   list [&lt;pid&gt;]     - every object under the figure's HandContent and every rendered object under a hand bone,
    ///                     marked mod (BAMP_*) or foreign; no pid = the local player's own hand.</summary>
    internal static class DevT28
    {
        private const string BoxItem = "ba:itemname_closedcardboardbox";
        private static readonly Dictionary<string, int> _removedRootIds = new();

        public static string Run(string arg)
        {
            var ap = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = ap.Length > 0 ? ap[0].ToLowerInvariant() : "";
            string pid = ap.Length > 1 ? ap[1] : "";
            try
            {
                switch (sub)
                {
                    case "hold":    return Hold();
                    case "drop":    return Drop();
                    case "respawn": return Respawn(pid);
                    case "list":    return pid.Length == 0 ? ListLocal() : ListFigure(pid);
                    default:        return "ERR handbox: usage handbox hold|drop|respawn <pid>|list [<pid>]";
                }
            }
            catch (Exception ex)
            {
                return "ERR handbox " + sub + ": " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string Hold()
        {
            if (PlayerHelper.PlayerController == null) return "ERR handbox hold: no local player in the world";
            if (PlayerHelper.IsHoldingItem)
                return "ERR handbox hold: hands already hold '" + PlayerHelper.ItemInHands?.itemName + "'";
            PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeNewInstance(BoxItem);
            string hand = DescribeLocalHand(out int active);
            string r = "OK handbox hold holding=" + (PlayerHelper.ItemInHands?.itemName ?? "none")
                       + " handActive=" + active + " hand=[" + hand + "]";
            Plugin.Logger.LogInfo("[T28] " + r);
            return r;
        }

        private static string Drop()
        {
            if (!PlayerHelper.IsHoldingItem) return "OK handbox drop (hands already empty)";
            string was = PlayerHelper.ItemInHands?.itemName ?? "?";
            PlayerHelper.ItemInstanceInHands = null;
            string hand = DescribeLocalHand(out int active);
            string r = "OK handbox drop was=" + was + " holding=" + (PlayerHelper.IsHoldingItem ? "yes" : "no")
                       + " handActive=" + active + " hand=[" + hand + "]";
            Plugin.Logger.LogInfo("[T28] " + r);
            return r;
        }

        private static string Respawn(string pid)
        {
            if (pid.Length == 0) return "ERR handbox respawn: player id required";
            var root = FigureRoot(pid);
            if (root == null) return "ERR handbox respawn: no figure for '" + pid + "' on this machine";
            _removedRootIds[pid] = root.GetInstanceID();
            string localHeld = PlayerHelper.IsHoldingItem ? (PlayerHelper.ItemInHands?.itemName ?? "?") : "none";
            RemotePlayerManager.Remove(pid);
            string r = "OK handbox respawn '" + pid + "' removed (old root " + _removedRootIds[pid]
                       + "); local hands at removal=" + localHeld + "; the next position packet re-spawns it";
            Plugin.Logger.LogInfo("[T28] " + r);
            return r;
        }

        private static string ListLocal()
        {
            if (PlayerHelper.PlayerController == null) return "ERR handbox list: no local player in the world";
            string hand = DescribeLocalHand(out int active);
            string r = "OK handbox list self holding=" + (PlayerHelper.IsHoldingItem ? PlayerHelper.ItemInHands?.itemName : "none")
                       + " handActive=" + active + " hand=[" + hand + "]";
            Plugin.Logger.LogInfo("[T28] " + r);
            return r;
        }

        private static string ListFigure(string pid)
        {
            var root = FigureRoot(pid);
            if (root == null) return "ERR handbox list: no figure for '" + pid + "' on this machine (not re-spawned yet?)";
            string fresh = _removedRootIds.TryGetValue(pid, out int oldId)
                ? (oldId != root.GetInstanceID() ? "yes" : "no") : "n/a";
            var items = new List<string>();
            int mod = 0, foreign = 0;
            bool handContentFound = false;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                bool isHandContent = t.name == "HandContent";
                bool isHandBone = !isHandContent && t.name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isHandContent && !isHandBone) continue;
                if (isHandContent) handContentFound = true;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c == null) continue;
                    // Under a hand bone only rendered objects count (finger bones carry no renderer).
                    // HandContent (and any nested hand node) is reported by its own pass, never as a hand-bone item.
                    if (isHandBone && (c.name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0
                                       || c.GetComponentsInChildren<Renderer>(true).Length == 0)) continue;
                    bool isMod = c.name.StartsWith("BAMP_");
                    if (isMod) mod++; else foreign++;
                    items.Add((isMod ? "mod:" : "FOREIGN:") + c.name + "@" + t.name
                              + (c.gameObject.activeInHierarchy ? "/active" : "/inactive")
                              + (c.GetComponent<ItemController>() != null ? "/itemctl" : ""));
                }
            }
            string r = "OK handbox list '" + pid + "' fresh=" + fresh + " handContent=" + (handContentFound ? "yes" : "no")
                       + " mod=" + mod + " foreign=" + foreign
                       + " localHolding=" + (PlayerHelper.IsHoldingItem ? PlayerHelper.ItemInHands?.itemName : "none")
                       + " items=[" + string.Join(", ", items) + "]";
            Plugin.Logger.LogInfo("[T28] figure " + r);
            return r;
        }

        /// <summary>The local HandContent's children, as the game holds them (ThirdPersonCharacter.GetHandContent).</summary>
        private static string DescribeLocalHand(out int active)
        {
            active = 0;
            var hc = PlayerHelper.PlayerController?.Character?.GetHandContent();
            if (hc == null) return "no HandContent";
            var sb = new StringBuilder();
            for (int i = 0; i < hc.childCount; i++)
            {
                var c = hc.GetChild(i);
                if (c == null) continue;
                if (c.gameObject.activeInHierarchy) active++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(c.name).Append(c.gameObject.activeInHierarchy ? "/active" : "/inactive");
            }
            return sb.ToString();
        }

        /// <summary>The figure root from the manager's own registry (it may be inactive under the interior mask,
        /// where GameObject.Find cannot see it).</summary>
        private static GameObject? FigureRoot(string pid)
        {
            var f = typeof(RemotePlayerManager).GetField("_players", BindingFlags.NonPublic | BindingFlags.Static);
            if (f?.GetValue(null) is Dictionary<string, GameObject> players
                && players.TryGetValue(pid, out var go) && go != null)
                return go;
            return null;
        }
    }
}
#endif
