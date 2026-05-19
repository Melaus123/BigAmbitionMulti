using System.Text.Json;
using Buildings;
using Helpers;
using Entities;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Applies incoming network state to the local GameInstance.
    /// All writes happen on the main Unity thread via the dispatcher queue.
    /// </summary>
    public static class GameStatePatcher
    {
        // ── Thread-safe action queue (network threads enqueue, Unity Update dequeues) ──

        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadQueue = new();

        /// <summary>Called from the Unity Update loop by MainThreadDispatcher.</summary>
        public static void DrainQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[Patcher] Dispatch error: {ex.Message}"); }
            }
        }

        private static void RunOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

        /// <summary>
        /// Public entry point so MPServer and MPClient can dispatch game-start actions
        /// from their background network threads onto the Unity main thread.
        /// </summary>
        public static void EnqueueOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

        // ── Snapshot application ──────────────────────────────────────────────

        public static void ApplyWorldSnapshot(WorldSnapshotPayload snap)
        {
            RunOnMainThread(() =>
            {
                Plugin.Logger.LogInfo("[Patcher] Applying world snapshot...");

                // Mark any buildings owned by other players as unavailable locally
                foreach (var kv in snap.BuildingOwners)
                {
                    if (kv.Value != "" && kv.Value != MPConfig.PlayerId)
                        MarkBuildingUnavailable(kv.Key);
                }

                // Apply market entries
                if (!string.IsNullOrEmpty(snap.MarketEntriesJson))
                    ApplyMarketSnapshot(snap.MarketEntriesJson);

                Plugin.Logger.LogInfo("[Patcher] World snapshot applied.");
            });
        }

        public static void ApplyBuildingOwnership(BuildingOwnershipPayload payload)
        {
            RunOnMainThread(() =>
            {
                if (payload.OwnerPlayerId == MPConfig.PlayerId)
                {
                    // This is confirmation of OUR rent — execute the actual game rent logic
                    ExecuteLocalRent(payload);
                }
                else
                {
                    // Another player rented it — mark unavailable locally
                    MarkBuildingUnavailable(payload.AddressKey);
                }
            });
        }

        public static void ApplyBuildingVacated(string addressKey)
        {
            RunOnMainThread(() =>
            {
                MarkBuildingAvailable(addressKey);
                Plugin.Logger.LogInfo($"[Patcher] Building {addressKey} is now available.");
            });
        }

        public static void ApplyGameTime(GameTimeSyncPayload payload)
        {
            RunOnMainThread(() =>
            {
                try
                {
                    // Apply speed/pause change first (exact, no lerp)
                    if (payload.Speed >= 0f)
                        TimeSync.ApplyNetwork(payload.Speed);

                    // Apply clock alignment (gradual for small drift, snap for large)
                    if (payload.Day > 0 || payload.TimeOfDay > 0f)
                        TimeSync.ReceiveClockSync(payload.Day, payload.TimeOfDay);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Patcher] ApplyGameTime: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Apply only a speed/pause change — no clock data.
        /// Used when relaying an immediate speed-change event.
        /// </summary>
        public static void ApplyTimeSpeed(float speed)
        {
            RunOnMainThread(() =>
            {
                try { TimeSync.ApplyNetwork(speed); }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Patcher] ApplyTimeSpeed: {ex.Message}");
                }
            });
        }

        public static void ApplyMarketSnapshot(string marketJson)
        {
            RunOnMainThread(() =>
            {
                try
                {
                    var dtos = JsonSerializer.Deserialize<List<MarketEntryDto>>(marketJson);
                    if (dtos == null) return;

                    var gi = SaveGameManager.Current;
                    if (gi == null) return;

                    foreach (var dto in dtos)
                    {
                        // Find matching entry and update prices
                        foreach (var entry in gi.productMarketEntries)
                        {
                            if (entry.itemName.ToString() == dto.ItemName)
                            {
                                entry.importPriceIndex = dto.ImportPriceIndex;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Patcher] ApplyMarketSnapshot error: {ex.Message}");
                }
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Marks the BuildingRegistration for this address as unavailable for rent
        /// in the local GameInstance — prevents the local player seeing it as rentable.
        /// </summary>
        private static void MarkBuildingUnavailable(string addressKey)
        {
            var reg = FindRegistration(addressKey);
            if (reg == null) return;
            reg.AvailableForRent = false;
            Plugin.Logger.LogInfo($"[Patcher] {addressKey} marked unavailable.");
        }

        private static void MarkBuildingAvailable(string addressKey)
        {
            var reg = FindRegistration(addressKey);
            if (reg == null) return;
            reg.AvailableForRent = true;
        }

        /// <summary>
        /// Executes the actual BuildingHelper.RentBuilding call for the local player
        /// after the host has confirmed approval.
        /// </summary>
        private static void ExecuteLocalRent(BuildingOwnershipPayload payload)
        {
            try
            {
                var building = FindBuilding(payload.AddressKey);
                if (building == null)
                {
                    Plugin.Logger.LogError($"[Patcher] Could not find building for {payload.AddressKey}");
                    return;
                }

                // Execute the actual game logic — this deducts money, sets RentedByPlayer, etc.
                // The Harmony patch will see MPPatches.SuppressNextRentRequest = true and skip
                // sending another network request for this call.
                MPPatches.SuppressNextRentRequest = true;
                BuildingHelper.RentBuilding(building, payload.DailyRent, payload.LastDeposit);
                Plugin.Logger.LogInfo($"[Patcher] Local rent executed for {payload.AddressKey}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Patcher] ExecuteLocalRent error: {ex.Message}");
            }
        }

        private static BuildingRegistration? FindRegistration(string addressKey)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return null;

            foreach (var reg in gi.BuildingRegistrations)
            {
                if (GameStateReader.AddressKey(reg) == addressKey)
                    return reg;
            }
            return null;
        }

        private static Building? FindBuilding(string addressKey)
        {
            // BuildingHelper.allBuildings is a static list of all city buildings
            foreach (var b in BuildingHelper.allBuildings)
            {
                if (GameStateReader.AddressKey(b) == addressKey)
                    return b;
            }
            return null;
        }
    }
}
