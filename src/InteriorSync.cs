using System;
using System.Collections.Generic;
using Buildings;
using Entities;
using LiteNetLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Host-side interior state pusher (Phase 2 of the building-sync feature).
    ///
    /// Subscription model: when a client enters building X, it sends an
    /// InteriorRequest{X}.  The host adds that client to X's subscriber set
    /// and immediately sends an InteriorSnapshot.  While subscribed, every
    /// Tick the host re-reads the building's interior fields, hashes them,
    /// and broadcasts a fresh snapshot if anything changed.  On exit, the
    /// client sends PlayerExitedBuilding{X} and the host removes them from
    /// the subscriber set.  If the set becomes empty, host stops polling X.
    ///
    /// Phase 2a covers Layout / interiorDesigns / retailPrices / dirtSpots.
    /// Phase 2b will add itemInstances (the full ItemInstance graph).
    /// </summary>
    public static class InteriorSync
    {
        private const float PollIntervalSeconds = 2f;

        // addressKey → set of peer ids currently subscribed (i.e. inside that building).
        // A peer can be subscribed to at most one building at a time (we drop
        // its previous sub when a new request comes in).
        private static readonly Dictionary<string, HashSet<int>> _subsByBuilding = new();
        // Inverse map: peer id → addressKey it's currently subscribed to.
        private static readonly Dictionary<int, string>          _buildingByPeer  = new();
        // addressKey → last-broadcast hash, so we only push when something changed.
        private static readonly Dictionary<string, int>          _lastHashByAddr  = new();
        private static float _lastPollAt;

        /// <summary>Reset all subscription + cache state.  Called on host shutdown / new game.</summary>
        public static void Reset()
        {
            _subsByBuilding.Clear();
            _buildingByPeer.Clear();
            _lastHashByAddr.Clear();
            _lastPollAt = 0f;
        }

        // ── Subscription management ───────────────────────────────────────────

        /// <summary>
        /// Handle a client's InteriorRequest.  Adds them to the subscriber set
        /// for that building (removing any prior subscription) and sends the
        /// initial snapshot.
        /// </summary>
        public static void HandleRequest(NetPeer peer, string playerId, string addressKey)
        {
            if (peer == null || string.IsNullOrEmpty(addressKey)) return;
            try
            {
                // Drop any prior subscription for this peer.
                if (_buildingByPeer.TryGetValue(peer.Id, out var oldAddr))
                {
                    if (_subsByBuilding.TryGetValue(oldAddr, out var oldSet))
                    {
                        oldSet.Remove(peer.Id);
                        if (oldSet.Count == 0) _subsByBuilding.Remove(oldAddr);
                    }
                }

                if (!_subsByBuilding.TryGetValue(addressKey, out var set))
                {
                    set = new HashSet<int>();
                    _subsByBuilding[addressKey] = set;
                }
                set.Add(peer.Id);
                _buildingByPeer[peer.Id] = addressKey;

                Plugin.Logger.LogInfo($"[InteriorSync] Sub: peer={peer.Id} player='{playerId}' addr='{addressKey}' (now {set.Count} subscriber(s) on this building, {_subsByBuilding.Count} active building(s)).");

                // Send initial snapshot to this peer only.
                var snap = BuildSnapshot(addressKey);
                if (snap == null) return;
                _lastHashByAddr[addressKey] = ComputeHash(snap);
                MPServer.SendInteriorSnapshotTo(peer, snap);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleRequest: {ex.Message}"); }
        }

        /// <summary>Handle a client's PlayerExitedBuilding.  Drops them from the subscriber set.</summary>
        public static void HandleExit(NetPeer peer, string playerId, string addressKey)
        {
            if (peer == null) return;
            try
            {
                if (_buildingByPeer.TryGetValue(peer.Id, out var cur))
                {
                    _buildingByPeer.Remove(peer.Id);
                    if (_subsByBuilding.TryGetValue(cur, out var set))
                    {
                        set.Remove(peer.Id);
                        if (set.Count == 0)
                        {
                            _subsByBuilding.Remove(cur);
                            _lastHashByAddr.Remove(cur);   // stop tracking; will reseed on next subscriber
                        }
                    }
                }
                Plugin.Logger.LogInfo($"[InteriorSync] Unsub: peer={peer.Id} player='{playerId}' addr='{addressKey}' ({_subsByBuilding.Count} active building(s) remaining).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleExit: {ex.Message}"); }
        }

        /// <summary>Called when a peer disconnects — clean up any lingering subscription.</summary>
        public static void HandlePeerDisconnected(int peerId)
        {
            if (!_buildingByPeer.TryGetValue(peerId, out var cur)) return;
            _buildingByPeer.Remove(peerId);
            if (_subsByBuilding.TryGetValue(cur, out var set))
            {
                set.Remove(peerId);
                if (set.Count == 0)
                {
                    _subsByBuilding.Remove(cur);
                    _lastHashByAddr.Remove(cur);
                }
            }
        }

        // ── Tick (poll subscribed buildings, broadcast diffs) ─────────────────

        public static void Tick()
        {
            if (!MPServer.IsRunning) return;
            if (_subsByBuilding.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastPollAt < PollIntervalSeconds) return;
            _lastPollAt = now;

            try
            {
                // Snapshot the keys so we can safely mutate _lastHashByAddr while iterating.
                var addrs = new List<string>(_subsByBuilding.Keys);
                foreach (var addr in addrs)
                {
                    var snap = BuildSnapshot(addr);
                    if (snap == null) continue;
                    int hash = ComputeHash(snap);
                    if (_lastHashByAddr.TryGetValue(addr, out var prev) && prev == hash) continue;
                    _lastHashByAddr[addr] = hash;
                    if (_subsByBuilding.TryGetValue(addr, out var set))
                        MPServer.BroadcastInteriorSnapshotTo(set, snap);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] Tick: {ex.Message}"); }
        }

        // ── Snapshot construction ─────────────────────────────────────────────

        public static InteriorSnapshotPayload? BuildSnapshot(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return null;
                BuildingRegistration? reg = null;
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null) continue;
                    if (GameStateReader.AddressKey(r) == addressKey) { reg = r; break; }
                }
                if (reg == null)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] BuildSnapshot: no reg for '{addressKey}'");
                    return null;
                }

                var snap = new InteriorSnapshotPayload
                {
                    AddressKey = addressKey,
                    Layout     = reg.Layout?.ToString() ?? "",
                };

                // Interior designs
                if (reg.interiorDesigns != null)
                {
                    for (int i = 0; i < reg.interiorDesigns.Count; i++)
                    {
                        var d = reg.interiorDesigns[i];
                        if (d == null) continue;
                        var dto = new InteriorDesignInfo { UUID = d.UUID?.ToString() ?? "" };
                        if (d.materials != null)
                        {
                            for (int j = 0; j < d.materials.Length; j++)
                            {
                                var m = d.materials[j];
                                dto.Materials.Add(new InteriorMaterialInfo
                                {
                                    MaterialID    = m.MaterialID?.ToString() ?? "",
                                    MaterialIndex = m.MaterialIndex,
                                    ColorIndex    = m.ColorIndex,
                                });
                            }
                        }
                        snap.InteriorDesigns.Add(dto);
                    }
                }

                // Retail prices
                if (reg.retailPrices != null)
                {
                    for (int i = 0; i < reg.retailPrices.Count; i++)
                    {
                        var rp = reg.retailPrices[i];
                        if (rp == null) continue;
                        snap.RetailPrices.Add(new RetailPriceInfo
                        {
                            ItemName = rp.itemName ?? "",
                            Price    = rp.price,
                        });
                    }
                }

                // Dirt spots
                if (reg.dirtSpots != null)
                {
                    for (int i = 0; i < reg.dirtSpots.Count; i++)
                    {
                        var ds = reg.dirtSpots[i];
                        if (ds == null) continue;
                        snap.DirtSpots.Add(new DirtSpotInfo
                        {
                            X         = ds.x,
                            Z         = ds.z,
                            Dirtiness = ds.dirtiness,
                        });
                    }
                }

                // ItemInstances (Phase 2b).  Walk the reg.itemInstances dict
                // and serialize each ItemInstance into a flat ItemInstanceInfo.
                try
                {
                    if (reg.itemInstances != null)
                    {
                        foreach (var kv in reg.itemInstances)
                        {
                            var ii = kv.Value;
                            if (ii == null) continue;
                            snap.ItemInstances.Add(SerializeItemInstance(ii));
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] item read for {addressKey}: {ex.Message}"); }

                return snap;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] BuildSnapshot('{addressKey}'): {ex.Message}"); return null; }
        }

        // ── ItemInstance serialization (Phase 2b) ────────────────────────────

        private static ItemInstanceInfo SerializeItemInstance(BigAmbitions.Items.ItemInstance ii)
        {
            var info = new ItemInstanceInfo
            {
                Id                  = ii.id?.ToString() ?? "",
                ItemName            = ii.itemName ?? "",
                Px = ii.position.x,  Py = ii.position.y,  Pz = ii.position.z,
                Qx = ii.rotation.x,  Qy = ii.rotation.y,  Qz = ii.rotation.z,  Qw = ii.rotation.w,
                YRotation           = ii.yRotation,
                ParentId            = ii.parentId?.ToString() ?? "",
                StreetName          = ii.streetName ?? "",
                StreetNumber        = ii.streetNumber,
                LinkedItemName      = ii.linkedItemName ?? "",
                IsSecured           = ii.isSecured,
                WorldSpaceTextValue = ii.worldSpaceTextValue?.ToString() ?? "",
                StateIndex          = ii.stateIndex,
                Alias               = ii.alias?.ToString() ?? "",
                CustomValue         = ii.customValue?.ToString() ?? "",
                PriceOnPurchase     = ii.priceOnPurchase,
            };

            // Stacked items (sub-items attached to this one).
            if (ii.stackedItems != null)
            {
                for (int i = 0; i < ii.stackedItems.Count; i++)
                {
                    var s = ii.stackedItems[i];
                    if (s == null) continue;
                    info.StackedItems.Add(new AttachableChildInfo
                    {
                        ChildId         = s.childId?.ToString() ?? "",
                        ChildItemName   = s.childItemName ?? "",
                        AttachmentIndex = s.attachmentIndex,
                    });
                }
            }

            // Cargo (products sitting on a shelf, ingredients in a fridge, etc).
            if (ii.cargoInstances != null)
            {
                for (int i = 0; i < ii.cargoInstances.Count; i++)
                {
                    var c = ii.cargoInstances[i];
                    if (c == null) continue;
                    var cdto = new CargoInstanceInfo
                    {
                        ItemName     = c.itemName ?? "",
                        Amount       = c.amount,
                        PricePerUnit = c.pricePerUnit,
                        Paid         = c.paid,
                    };
                    if (c.customColors != null)
                    {
                        for (int j = 0; j < c.customColors.Count; j++)
                        {
                            var cc = c.customColors[j];
                            if (cc == null) continue;
                            cdto.CustomColors.Add(new CustomColorInfo { Channel = (int)cc.channel, ColorPacked = cc.color.color });
                        }
                    }
                    if (c.nestedCargoInstances != null)
                    {
                        for (int j = 0; j < c.nestedCargoInstances.Count; j++)
                        {
                            var n = c.nestedCargoInstances[j];
                            if (n == null) continue;
                            var ndto = new NestedCargoInstanceInfo
                            {
                                ItemName     = n.itemName ?? "",
                                Amount       = n.amount,
                                PricePerUnit = n.pricePerUnit,
                            };
                            if (n.customColors != null)
                            {
                                for (int k = 0; k < n.customColors.Count; k++)
                                {
                                    var nc = n.customColors[k];
                                    if (nc == null) continue;
                                    ndto.CustomColors.Add(new CustomColorInfo { Channel = (int)nc.channel, ColorPacked = nc.color.color });
                                }
                            }
                            cdto.NestedCargoInstances.Add(ndto);
                        }
                    }
                    info.CargoInstances.Add(cdto);
                }
            }

            // Dirt-spot indices this item affects (overlapping floor tiles).
            if (ii.dirtSpotsThatAffects != null)
            {
                for (int i = 0; i < ii.dirtSpotsThatAffects.Count; i++)
                    info.DirtSpotsThatAffects.Add(ii.dirtSpotsThatAffects[i]);
            }

            // Custom positions (used by multi-element items like cinema seating).
            if (ii.customPositions != null)
            {
                for (int i = 0; i < ii.customPositions.Count; i++)
                {
                    var p = ii.customPositions[i];
                    info.CustomPositions.Add(new Vector3Info { X = p.x, Y = p.y, Z = p.z });
                }
            }

            // Top-level custom colors (paint applied directly to the item).
            if (ii.customColors != null)
            {
                for (int i = 0; i < ii.customColors.Count; i++)
                {
                    var cc = ii.customColors[i];
                    if (cc == null) continue;
                    info.CustomColors.Add(new CustomColorInfo { Channel = (int)cc.channel, ColorPacked = cc.color.color });
                }
            }

            // Purchaser settings (for ordering machines etc.).
            if (ii.playerItemPurchaserSettings != null)
            {
                var p = ii.playerItemPurchaserSettings;
                info.PurchaserSettings = new PlayerItemPurchaserSettingsInfo
                {
                    Name         = p.name?.ToString() ?? "",
                    Enabled      = p.enabled,
                    ItemName     = p.itemName ?? "",
                    ItemQuantity = p.itemQuantity,
                };
            }

            return info;
        }

        /// <summary>
        /// Order-sensitive hash over the snapshot's contents.  Collisions just
        /// mean one missed broadcast, recoverable on the next change.
        /// </summary>
        internal static int ComputeHash(InteriorSnapshotPayload snap)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + MPAudit.StableHash(snap.Layout);
                foreach (var d in snap.InteriorDesigns)
                {
                    h = h * 31 + MPAudit.StableHash(d.UUID);
                    foreach (var m in d.Materials)
                    {
                        h = h * 31 + MPAudit.StableHash(m.MaterialID);
                        h = h * 31 + m.MaterialIndex;
                        h = h * 31 + m.ColorIndex;
                    }
                }
                foreach (var rp in snap.RetailPrices)
                {
                    h = h * 31 + MPAudit.StableHash(rp.ItemName);
                    h = h * 31 + rp.Price.GetHashCode();
                }
                foreach (var ds in snap.DirtSpots)
                {
                    h = h * 31 + ds.X;
                    h = h * 31 + ds.Z;
                    // Round Dirtiness to 1 decimal place so micro-fluctuations
                    // from NPCs walking around don't trigger broadcasts every
                    // poll cycle.  We still see meaningful changes (0.1 step).
                    h = h * 31 + ((int)System.Math.Round(ds.Dirtiness * 10f)).GetHashCode();
                }
                // Items — hash core identity + position so adds/moves/removes
                // trigger broadcasts.  Cargo amounts/prices are part of items'
                // visible state and worth hashing too.
                foreach (var it in snap.ItemInstances)
                {
                    h = h * 31 + MPAudit.StableHash(it.Id);
                    h = h * 31 + MPAudit.StableHash(it.ItemName);
                    // Round positions to nearest cm so 0.001f jitter doesn't trigger.
                    h = h * 31 + ((int)System.Math.Round(it.Px * 100f)).GetHashCode();
                    h = h * 31 + ((int)System.Math.Round(it.Py * 100f)).GetHashCode();
                    h = h * 31 + ((int)System.Math.Round(it.Pz * 100f)).GetHashCode();
                    h = h * 31 + ((int)System.Math.Round(it.YRotation * 10f)).GetHashCode();
                    h = h * 31 + it.StateIndex;
                    h = h * 31 + MPAudit.StableHash(it.Alias);
                    foreach (var c in it.CargoInstances)
                    {
                        h = h * 31 + MPAudit.StableHash(c.ItemName);
                        h = h * 31 + c.Amount;
                        h = h * 31 + ((int)System.Math.Round(c.PricePerUnit * 100f)).GetHashCode();
                    }
                }
                return h;
            }
        }
    }
}
