using System;
using System.Collections.Generic;
using Buildings;
using Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace BigAmbitionsMP
{
    /// <summary>Phase 3 slice 3 (round-41) — SHARED CUSTOMER BODIES when two+ session players are inside
    /// the same player building. One machine (the "simulator") runs native customers; every other player
    /// inside suppresses its local spawner and renders kinematic PUPPETS from the simulator's ~4 Hz
    /// stream — both players watch the same shopper at the same shelf.
    ///
    /// AUTHORITY (host-arbitrated, user-agreed design): the register worker first (serving requires
    /// simulating — the serve loop needs real customer AI locally), else the earliest-arrived player
    /// still inside (host-clock arrival order; no race). Transfers happen on exactly two human-legible
    /// events — someone starts working the register, or the simulator leaves — and render as walk-out/
    /// walk-in churn (v1): the outgoing crowd paths to the exit while the new simulator's spawner
    /// repopulates from the same shared schedule. "Authority persists after leaving" is impossible by
    /// engine design: interiors only exist around the local player.
    ///
    /// ECONOMY interplay (why this is safe): while a helper simulates, the owner-as-follower's spawner
    /// is suppressed, so the owner's entry table stays unclaimed and every helper-served order forwards
    /// cleanly (round-39f); while the owner simulates, sales record natively and the follower-helper's
    /// machine has no real customers to double-anything with.</summary>
    internal static class CustomerPuppets
    {
        // ── Authority state (all machines; host is the writer) ──────────────────────────────────────
        private static readonly Dictionary<string, string> _authority = new();   // addressKey → simulator pid

        // ── My local mode ────────────────────────────────────────────────────────────────────────────
        private static string _myBldg = "";
        private static bool _followerHere;

        // ── Host election bookkeeping ────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, (string bldg, float since)> _arrival = new();
        private static float _nextElectAt, _nextResendAt;

        // ── Simulator streaming ──────────────────────────────────────────────────────────────────────
        private static float _nextStreamAt;

        // ── Puppets (follower side) ──────────────────────────────────────────────────────────────────
        private class Puppet
        {
            public GameObject go = null!;
            public ThirdPersonCharacter? tpc;
            public Animator? anim;
            public Vector3 target;
            public float yaw;
            public float lastSeen;
            public bool leaving;
            public float leaveAt;
            public string held = "";
            public int fill = -1;
            public GameObject? heldGo;
            public CustomerPuppetLookPayload? look;   // round-44: the customer's appearance — survives handoffs
        }
        private static readonly Dictionary<string, Puppet> _puppets = new();

        // Simulator-side per-customer HandContent cache (round-42 hand-prop mirroring).
        private static readonly Dictionary<int, Transform?> _handNodes = new();

        // Simulator-side per-customer entry-id cache (round-43 cross-machine identity).
        // Round-45: validated against the ORDER reference — customer bodies are POOLED (ReleaseCustomer →
        // reuse), so an instance id alone maps a recycled body to the PREVIOUS customer's identity: the
        // follower kept the old puppet+look for a brand-new person ("looks drifted"), and the new
        // person's look never shipped (the stale id sat in the sent-set).
        private static readonly Dictionary<int, (Order order, string id)> _custEntryIds = new();

        // Round-43: emotes that arrived before their puppet existed (complaints fire at the customer's
        // SPAWN instant — one state-tick before the follower's body appears). Applied at puppet spawn.
        private static readonly Dictionary<string, (int emoji, float seconds, float expires)> _pendingEmotes = new();

        // Round-44c: SESSION-LIFETIME look cache by customer identity — a look ships once, but puppets
        // get REBUILT (room re-entry, handoffs, stream gaps) and the consume-once pending queue lost the
        // look after first use, so rebuilt bodies fell back to random faces (field: "matched at first,
        // diverged by the end"). Never consumed, only overwritten; cleared with the session.
        private static readonly Dictionary<string, CustomerPuppetLookPayload> _looksById = new();
        private static readonly HashSet<string> _looksSent = new();

        // Round-44: adopted natives get their position HELD for a beat — the native spawn-init
        // repositions the body and assigns objectives a frame or two later, which raced our single warp
        // (field: adopted customers teleported and bolted in odd directions).
        private static readonly List<(Customer c, Vector3 pos, float until)> _warpHolds = new();

        public static void Reset()
        {
            try
            {
                DestroyAllPuppets();
                _authority.Clear();
                _arrival.Clear();
                _looksById.Clear();
                _looksSent.Clear();
                _myBldg = "";
                if (_followerHere) { try { IndoorCustomerSpawner.EnableCustomersSpawn(); } catch { } }
                _followerHere = false;
            }
            catch { }
        }

        /// <summary>Once per frame from MPCanvasUI.Update. MAIN THREAD.</summary>
        public static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected)
                {
                    if (_puppets.Count > 0 || _followerHere) Reset();
                    return;
                }
                if (MPServer.IsRunning) HostElectionTick();
                TrackMyBuilding();
                SimulatorStreamTick();
                UpdatePuppets();
                TickWarpHolds();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] puppet tick: {ex.Message}"); }
        }

        // ── Host: simulator election ─────────────────────────────────────────────────────────────────
        private static void HostElectionTick()
        {
            if (Time.unscaledTime < _nextElectAt) return;
            _nextElectAt = Time.unscaledTime + 1f;
            bool resend = Time.unscaledTime >= _nextResendAt;
            if (resend) _nextResendAt = Time.unscaledTime + 5f;

            // Presence: every session player's current building (host clock stamps arrivals).
            var inside = new Dictionary<string, List<(string pid, float since)>>();
            foreach (var pid in MPRestSync.AllPlayers())
            {
                string b = pid == MPConfig.PlayerId ? (MPRegisterSync.CurrentShopAddress ?? "")
                                                    : RemotePlayerManager.BuildingOf(pid);
                if (!_arrival.TryGetValue(pid, out var a) || a.bldg != b)
                    _arrival[pid] = (b, Time.unscaledTime);
                if (string.IsNullOrEmpty(b)) continue;
                if (!MPServer.BuildingOwners.ContainsKey(b)) continue;   // only session-player buildings
                if (!inside.TryGetValue(b, out var list)) inside[b] = list = new List<(string, float)>();
                list.Add((pid, _arrival[pid].since));
            }

            // Elect per occupied building: register-duty holder first, else earliest arrival.
            foreach (var kv in inside)
            {
                string sim = "";
                string duty = MPRegisterSync.PersonalDutyHolderAt(kv.Key);
                if (!string.IsNullOrEmpty(duty))
                    foreach (var (pid, _) in kv.Value) if (pid == duty) { sim = duty; break; }
                if (sim.Length == 0)
                {
                    float best = float.MaxValue;
                    foreach (var (pid, since) in kv.Value)
                        if (since < best) { best = since; sim = pid; }
                }
                bool changed = !_authority.TryGetValue(kv.Key, out var cur) || cur != sim;
                if (changed || resend)
                {
                    _authority[kv.Key] = sim;
                    MPServer.BroadcastCustomerAuthority(new CustomerSimAuthorityPayload { AddressKey = kv.Key, SimulatorPid = sim });
                    if (changed)
                    {
                        Plugin.Logger.LogInfo($"[Customers] simulator for '{kv.Key}' → '{sim}' ({kv.Value.Count} inside).");
                        if (kv.Key == _myBldg) ReactToAuthority();
                    }
                }
            }

            // Emptied buildings: clear the assignment so everyone reverts to native.
            var stale = new List<string>();
            foreach (var kv in _authority)
                if (!inside.ContainsKey(kv.Key)) stale.Add(kv.Key);
            foreach (var k in stale)
            {
                _authority.Remove(k);
                MPServer.BroadcastCustomerAuthority(new CustomerSimAuthorityPayload { AddressKey = k, SimulatorPid = "" });
            }
        }

        // ── All machines: react to authority + my own movement ──────────────────────────────────────
        public static void ApplyAuthority(CustomerSimAuthorityPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (string.IsNullOrEmpty(p.SimulatorPid)) _authority.Remove(p.AddressKey);
                else _authority[p.AddressKey] = p.SimulatorPid;
                if (p.AddressKey == _myBldg) ReactToAuthority();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply authority: {ex.Message}"); }
        }

        private static void TrackMyBuilding()
        {
            string cur = MPRegisterSync.CurrentShopAddress ?? "";
            if (cur == _myBldg) return;
            // Leaving a building: the interior (and any puppets in it) is gone for me — restore native
            // spawning unconditionally and drop puppet objects immediately (they died with the interior).
            if (_followerHere)
            {
                try { IndoorCustomerSpawner.EnableCustomersSpawn(); } catch { }
                _followerHere = false;
            }
            DestroyAllPuppets();
            _myBldg = cur;
            ReactToAuthority();
        }

        private static void ReactToAuthority()
        {
            if (string.IsNullOrEmpty(_myBldg)) return;
            _authority.TryGetValue(_myBldg, out var sim);
            bool follow = !string.IsNullOrEmpty(sim) && sim != MPConfig.PlayerId;
            if (follow && !_followerHere)
            {
                _followerHere = true;
                try { IndoorCustomerSpawner.DisableCustomersSpawn(); } catch { }
                // Round-43 SMOOTH HANDOFF (losing side): swap each native customer for a puppet AT ITS
                // POSITION — the new simulator adopts the same entries in place, so its stream picks
                // these bodies up where they stand instead of despawn/respawn churn.
                int swapped = 0, dropped = 0;
                try
                {
                    var reg = FindReg(_myBldg);
                    var mine = new List<Customer>(IndoorCustomerSpawner.Customers);
                    foreach (var c in mine)
                    {
                        if (c == null) continue;
                        string id = RowIdOf(c, reg);
                        Vector3 pos = c.transform.position;
                        float yaw = c.transform.eulerAngles.y;
                        var (held, heldFill) = HeldStateOf(c);
                        var look = CaptureLook(c.tpc);
                        try { c.ReleaseCustomer(); } catch { }
                        if (!id.StartsWith("i", StringComparison.Ordinal) && !_puppets.ContainsKey(id))
                        {
                            var pup = SpawnPuppet(pos, yaw);
                            if (pup != null)
                            {
                                _puppets[id] = pup;
                                if (!string.IsNullOrEmpty(held)) { UpdateHeld(pup, held); ApplyFill(pup, heldFill); }
                                // Round-44: the puppet WEARS the native's exact look (local capture — no wire).
                                if (look != null) { pup.look = look; _looksById[id] = look; ApplyLookTo(pup.tpc, look); }
                                swapped++;
                                continue;
                            }
                        }
                        dropped++;   // no entry identity → the rare churn fallback
                    }
                }
                catch { }
                Plugin.Logger.LogInfo($"[Customers] following '{sim}' in '{_myBldg}' — spawner suppressed; {swapped} native(s) swapped to puppets in place{(dropped > 0 ? $", {dropped} without identity dropped" : "")}.");
            }
            else if (!follow && _followerHere)
            {
                _followerHere = false;
                try { IndoorCustomerSpawner.EnableCustomersSpawn(); } catch { }
                // Round-43 SMOOTH HANDOFF (gaining side): adopt each puppet's schedule entry as a REAL
                // customer AT the puppet's position — bodies stay put, the AI resumes its routine from
                // there (a mid-checkout customer re-approaches; the agreed settle behavior). Puppets
                // whose entry can't be found locally walk out (the churn fallback).
                int adopted = 0, walked = 0;
                var regNow = FindReg(_myBldg);
                var keys = new List<string>(_puppets.Keys);
                foreach (var key in keys)
                {
                    var pup = _puppets[key];
                    if (AdoptPuppetAsNative(regNow, key, pup))
                    {
                        try { if (pup.heldGo != null) UnityEngine.Object.Destroy(pup.heldGo); } catch { }
                        try { if (pup.go != null) UnityEngine.Object.Destroy(pup.go); } catch { }
                        _puppets.Remove(key);
                        adopted++;
                    }
                    else { StartLeaving(pup); walked++; }
                }
                Plugin.Logger.LogInfo($"[Customers] native mode in '{_myBldg}' — spawner restored; {adopted} puppet(s) adopted in place{(walked > 0 ? $", {walked} walking out (no local entry)" : "")}.");
            }
        }

        /// <summary>Round-43: spawn the puppet's schedule entry as a real customer and move the body to
        /// the puppet's spot. Uses the game's own (private) SpawnCustomer(CustomerEntry).</summary>
        private static bool AdoptPuppetAsNative(BuildingRegistration? reg, string entryId, Puppet pup)
        {
            try
            {
                if (pup.go == null || entryId.StartsWith("i", StringComparison.Ordinal)) return false;
                var entry = CustomerEntrySync.TryFindEntry(reg, entryId);
                if (entry == null) return false;
                int before = IndoorCustomerSpawner.Customers.Count;
                _spawnCustomerM ??= HarmonyLib.AccessTools.Method(typeof(IndoorCustomerSpawner), "SpawnCustomer",
                    new[] { typeof(AI.Customers.CustomerEntries.CustomerEntry) });
                if (_spawnCustomerM == null) return false;
                _spawnCustomerM.Invoke(null, new object[] { entry });
                entry.completed = true;   // consumed — the regular spawner must not spawn it again
                var list = IndoorCustomerSpawner.Customers;
                if (list.Count <= before) return false;   // spawn refused (capacity etc.)
                var c = list[list.Count - 1];
                if (c == null) return true;
                Vector3 pos = pup.go.transform.position;
                // Round-44: HOLD the position — the native spawn-init repositions the body + assigns
                // objectives over the next frames; a single warp raced it (field: teleport + bolting).
                _warpHolds.Add((c, pos, Time.unscaledTime + 0.75f));
                try
                {
                    var ag = c.tpc != null ? c.tpc.navmeshAgent : null;
                    if (ag != null && ag.enabled) ag.Warp(pos);
                    else c.transform.position = pos;
                }
                catch { c.transform.position = pos; }
                // Round-44: the adopted native KEEPS the puppet's look (looks survive handoffs).
                if (pup.look != null) ApplyLookTo(c.tpc, pup.look);
                _custEntryIds[c.GetInstanceID()] = (c.order, entryId);   // identity continuity for my own stream
                return true;   // ([AdoptProbe] retired 2026-07-07 — adoption field-confirmed healthy)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] adopt puppet: {ex.Message}"); return false; }
        }

        /// <summary>Round-44: keep adopted bodies pinned while the native spawn-init settles.</summary>
        private static void TickWarpHolds()
        {
            if (_warpHolds.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = _warpHolds.Count - 1; i >= 0; i--)
            {
                var (c, pos, until) = _warpHolds[i];
                if (c == null || now > until) { _warpHolds.RemoveAt(i); continue; }
                try
                {
                    if ((c.transform.position - pos).sqrMagnitude > 0.09f)
                    {
                        var ag = c.tpc != null ? c.tpc.navmeshAgent : null;
                        if (ag != null && ag.enabled) ag.Warp(pos);
                        else c.transform.position = pos;
                    }
                }
                catch { _warpHolds.RemoveAt(i); }
            }
        }

        /// <summary>Round-44b: dress a body from the structured look via native SetAppearance.
        /// (look-drift probes retired 2026-07-07 — pool-reuse identity fix field-confirmed.)</summary>
        private static void ApplyLookTo(ThirdPersonCharacter? tpc, CustomerPuppetLookPayload p)
        {
            try
            {
                if (tpc == null || p == null || p.Elements.Count == 0) return;
                var data = new CharacterData
                {
                    gender    = (BigAmbitions.Characters.Gender)p.Gender,
                    strength  = p.Strength,
                    fatness   = p.Fatness,
                    color     = new Color32((byte)((p.ColorPacked >> 24) & 0xFF), (byte)((p.ColorPacked >> 16) & 0xFF), (byte)((p.ColorPacked >> 8) & 0xFF), (byte)(p.ColorPacked & 0xFF)),
                    eyesColor = new Color32((byte)((p.EyesPacked >> 24) & 0xFF), (byte)((p.EyesPacked >> 16) & 0xFF), (byte)((p.EyesPacked >> 8) & 0xFF), (byte)(p.EyesPacked & 0xFF)),
                };
                foreach (var e in p.Elements)
                    if (e != null)
                        data.elements.Add(new BigAmbitions.Characters.Appearance.AppearanceElementData
                        { type = (BigAmbitions.Characters.Appearance.AppearanceElementType)e.Type, variantId = e.VariantId, colorId = e.ColorId });
                foreach (var b in p.Blends)
                    if (b != null)
                        data.blendshapes.Add(new BigAmbitions.Characters.FacialBlendshape { name = b.Name, value = b.Value });
                tpc.appearanceSetter.SetAppearance(data);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply look: {ex.Message}"); }
        }

        private static System.Reflection.MethodInfo? _spawnCustomerM;

        private static BuildingRegistration? FindReg(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null || string.IsNullOrEmpty(addressKey)) return null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) return r;
            }
            catch { }
            return null;
        }

        /// <summary>Round-43: a live customer's cross-machine row id — the schedule entry id when known,
        /// else a machine-local fallback ("i&lt;instanceId&gt;", churns on handoff). Round-45: the cache
        /// hit requires the SAME Order reference — a pooled body reused for a new customer carries a new
        /// order, which invalidates the stale mapping.</summary>
        private static string RowIdOf(Customer c, BuildingRegistration? reg)
        {
            int iid = c.GetInstanceID();
            var order = c.order;
            if (_custEntryIds.TryGetValue(iid, out var cached) && ReferenceEquals(cached.order, order))
                return cached.id;
            string id = "";
            try { id = CustomerEntrySync.EntryIdForOrder(reg, order); } catch { }
            if (string.IsNullOrEmpty(id)) id = "i" + iid + "-" + (order?.GetHashCode() ?? 0);
            _custEntryIds[iid] = (order!, id);
            return id;
        }

        // ── Simulator: stream my live customers ─────────────────────────────────────────────────────
        private static void SimulatorStreamTick()
        {
            if (string.IsNullOrEmpty(_myBldg)) return;
            if (!_authority.TryGetValue(_myBldg, out var sim) || sim != MPConfig.PlayerId) return;
            if (Time.unscaledTime < _nextStreamAt) return;
            _nextStreamAt = Time.unscaledTime + 0.25f;

            var p = new CustomerPuppetStatePayload { AddressKey = _myBldg, SimulatorPid = MPConfig.PlayerId };
            try
            {
                var reg = FindReg(_myBldg);
                foreach (var c in IndoorCustomerSpawner.Customers)
                {
                    if (c == null) continue;
                    var t = c.transform;
                    string rid = RowIdOf(c, reg);
                    var (heldName, heldFill) = HeldStateOf(c);
                    p.Rows.Add(new PuppetRowInfo
                    {
                        Id   = rid,
                        X    = t.position.x, Y = t.position.y, Z = t.position.z,
                        Yaw  = t.eulerAngles.y,
                        Held = heldName,
                        Fill = heldFill,
                    });
                    // Round-44: ship each customer's look ONCE so followers dress the same person.
                    if (!_looksSent.Contains(rid))
                    {
                        _looksSent.Add(rid);
                        var lp = CaptureLook(c.tpc);
                        if (lp != null)
                        {
                            lp.AddressKey = _myBldg; lp.SimulatorPid = MPConfig.PlayerId; lp.CustomerId = rid;
                            if (MPServer.IsRunning) MPServer.BroadcastCustomerLook(lp);
                            else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerPuppetLook, MPConfig.PlayerId, lp));
                        }
                        else Plugin.Logger.LogInfo($"[Customers] look capture EMPTY for {rid} (no appearance data).");
                    }
                }
            }
            catch { }
            if (MPServer.IsRunning) MPServer.BroadcastCustomerPuppets(p);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerPuppetState, MPConfig.PlayerId, p));
        }

        /// <summary>The prop in a simulated customer's hand ("" = empty) — first active child of the
        /// TPC's HandContent bone, name-normalized — plus its FILL (active direct children: shopping
        /// baskets visually fill with child item meshes as the customer shops, round-45).</summary>
        private static (string name, int fill) HeldStateOf(Customer c)
        {
            try
            {
                int id = c.GetInstanceID();
                if (!_handNodes.TryGetValue(id, out var hand))
                {
                    hand = null;
                    var root = (c.tpc != null ? c.tpc.transform : c.transform);
                    foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                        if (tr != null && tr.name == "HandContent") { hand = tr; break; }
                    _handNodes[id] = hand;
                }
                if (hand == null) return ("", 0);
                for (int i = 0; i < hand.childCount; i++)
                {
                    var ch = hand.GetChild(i);
                    if (ch == null || !ch.gameObject.activeSelf) continue;
                    // Round-45b ([PropProbe]-taught): fill = the ACTIVE "Products*" children ONLY — the
                    // basket's other children (IK attach point, shadow caster) are always-on and counting
                    // them made puppets look full at pickup.
                    int fill = 0;
                    for (int j = 0; j < ch.childCount; j++)
                    {
                        var k = ch.GetChild(j);
                        if (k != null && k.name.StartsWith("Products", StringComparison.Ordinal) && k.gameObject.activeSelf) fill++;
                    }
                    return (RemotePlayerManager.CleanProp(ch.name), fill);
                }
            }
            catch { }
            return ("", 0);
        }

        // (prop-mirror probes retired 2026-07-07 — findings folded into the code: basket fill =
        // Products*-only children; templates require an ItemController; bags mint via GetRandomBag.)

        /// <summary>Round-42 — replay a simulated customer's emoji (complaints etc.) on the puppet.</summary>
        public static void ApplyEmote(CustomerPuppetEmotePayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId) return;
                if (p.AddressKey != _myBldg || !_followerHere) return;
                if (string.IsNullOrEmpty(p.CustomerId)) return;
                if (!_puppets.TryGetValue(p.CustomerId, out var pup) || pup.tpc == null)
                {
                    // Round-43: the body may not exist yet (complaints fire at spawn instant, one state
                    // tick before the puppet appears) — hold the emote briefly.
                    _pendingEmotes[p.CustomerId] = (p.Emoji, p.Seconds <= 0f ? 3f : p.Seconds, Time.unscaledTime + 4f);
                    return;
                }
                PlayEmoteOn(pup, p.Emoji, p.Seconds);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply emote: {ex.Message}"); }
        }

        /// <summary>Simulator side (called by the ShowExpression patch): stream a customer's emoji.
        /// (emote-chain probes retired 2026-07-07 — pipeline + visuals field-confirmed.)</summary>
        internal static void OnLocalCustomerEmote(ThirdPersonCharacter tpc, int emoji, float seconds)
        {
            try
            {
                if (string.IsNullOrEmpty(_myBldg)) return;
                if (!_authority.TryGetValue(_myBldg, out var sim) || sim != MPConfig.PlayerId) return;
                var cust = tpc != null ? tpc.GetComponentInParent<Customer>() : null;
                if (cust == null) return;
                var p = new CustomerPuppetEmotePayload
                {
                    AddressKey = _myBldg, SimulatorPid = MPConfig.PlayerId,
                    CustomerId = RowIdOf(cust, FindReg(_myBldg)), Emoji = emoji, Seconds = seconds,
                };
                if (MPServer.IsRunning) MPServer.BroadcastCustomerEmote(p);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerPuppetEmote, MPConfig.PlayerId, p));
            }
            catch { }
        }

        /// <summary>Round-44 — a customer's look arrived: dress the puppet (or hold until it spawns).</summary>
        public static void ApplyLook(CustomerPuppetLookPayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId || string.IsNullOrEmpty(p.CustomerId)) return;
                _looksById[p.CustomerId] = p;   // round-44c: cache always — rebuilt puppets re-dress from here
                if (p.AddressKey != _myBldg || !_followerHere) return;
                if (_puppets.TryGetValue(p.CustomerId, out var pup))
                {
                    pup.look = p;
                    ApplyLookTo(pup.tpc, p);
                }
            }
            catch { }
        }

        /// <summary>Round-44b — play an emoji on a puppet DIRECTLY via the emoji system. The native
        /// ShowExpression wrapper NREs on puppets (its internal coroutine-runner is Start-assigned and
        /// Start never runs on the disabled TPC — field-proven stack at ShowExpression MoveNext 0xa9);
        /// the head bone is prefab-serialized, so the static ShowEmoji path has everything it needs.</summary>
        private static void PlayEmoteOn(Puppet pup, int emoji, float seconds)
        {
            try
            {
                if (pup.tpc == null || pup.tpc.head == null) return;
                pup.tpc.StartCoroutine(Characters.EmojiSystem.CharacterEmojiSystem.ShowEmoji(
                    pup.tpc.head, (CharacterEmojiName)emoji, showText: true, seconds <= 0f ? 3f : seconds));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] emote play: {ex.Message}"); }
        }

        /// <summary>Round-44b — structured appearance capture (raw CharacterData JSON died silently on
        /// its embedded ItemInstance graph).</summary>
        private static CustomerPuppetLookPayload? CaptureLook(ThirdPersonCharacter? tpc)
        {
            try
            {
                var d = tpc?.appearanceSetter?.data;
                if (d == null) return null;
                var p = new CustomerPuppetLookPayload
                {
                    Gender      = (int)d.gender,
                    Strength    = d.strength,
                    Fatness     = d.fatness,
                    ColorPacked = (d.color.r << 24) | (d.color.g << 16) | (d.color.b << 8) | d.color.a,
                    EyesPacked  = (d.eyesColor.r << 24) | (d.eyesColor.g << 16) | (d.eyesColor.b << 8) | d.eyesColor.a,
                };
                if (d.elements != null)
                    foreach (var e in d.elements)
                        if (e != null) p.Elements.Add(new LookElementInfo { Type = (int)e.type, VariantId = e.variantId ?? "", ColorId = e.colorId ?? "" });
                if (d.blendshapes != null)
                    foreach (var b in d.blendshapes)
                        if (b != null) p.Blends.Add(new LookBlendInfo { Name = b.name ?? "", Value = b.value });
                return p.Elements.Count > 0 ? p : null;   // no elements = nothing to dress with
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] look capture: {ex.Message}"); return null; }
        }

        // ── Follower: render the stream ──────────────────────────────────────────────────────────────
        public static void ApplyState(CustomerPuppetStatePayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId) return;   // my own echo
                if (p.AddressKey != _myBldg || !_followerHere) return;          // not my room / I'm native
                var seen = new HashSet<string>();
                foreach (var r in p.Rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    seen.Add(r.Id);
                    if (!_puppets.TryGetValue(r.Id, out var pup))
                    {
                        pup = SpawnPuppet(new Vector3(r.X, r.Y, r.Z), r.Yaw);
                        if (pup == null) continue;
                        _puppets[r.Id] = pup;
                        // Round-43: replay an emote that arrived before this body existed (complaints
                        // fire at the customer's spawn instant).
                        if (_pendingEmotes.TryGetValue(r.Id, out var pe))
                        {
                            _pendingEmotes.Remove(r.Id);
                            if (Time.unscaledTime < pe.expires) PlayEmoteOn(pup, pe.emoji, pe.seconds);
                        }
                        // Round-44c: dress the body from the session look cache (survives rebuilds).
                        if (_looksById.TryGetValue(r.Id, out var lk))
                        {
                            pup.look = lk;
                            ApplyLookTo(pup.tpc, lk);
                        }
                    }
                    pup.target   = new Vector3(r.X, r.Y, r.Z);
                    pup.yaw      = r.Yaw;
                    pup.lastSeen = Time.unscaledTime;
                    if (pup.leaving) pup.leaving = false;   // simulator says they're still here
                    if (pup.held != (r.Held ?? "")) UpdateHeld(pup, r.Held ?? "");
                    if (pup.fill != r.Fill) ApplyFill(pup, r.Fill);
                }
                // Rows that vanished = customers who left/were served away → walk out.
                foreach (var kv in _puppets)
                    if (!seen.Contains(kv.Key) && !kv.Value.leaving) StartLeaving(kv.Value);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply puppets: {ex.Message}"); }
        }

        private static Puppet? SpawnPuppet(Vector3 pos, float yaw)
        {
            try
            {
                var tpc = PrefabHelper.CreatePrefab<ThirdPersonCharacter>("Characters/HumanDefinitionLow", null);
                if (tpc == null) return null;
                var go = tpc.gameObject;
                go.name = "BAMP_CustomerPuppet";
                go.SetActive(true);
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
                try
                {
                    var gender = UnityEngine.Random.value < 0.5f
                        ? BigAmbitions.Characters.Gender.Male
                        : BigAmbitions.Characters.Gender.Female;
                    tpc.appearanceSetter.SetRandomAppearance(gender,
                        new[] { BigAmbitions.Characters.Appearance.AppearanceTag.Casual });
                }
                catch { }
                // Kinematic mannequin: no pathfinding, no physics pushback (round-13 avatar-shove class),
                // no click surface — the stream is the only thing that moves it.
                try { foreach (var a in go.GetComponentsInChildren<NavMeshAgent>(true)) a.enabled = false; } catch { }
                try { foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true; } catch { }
                try { foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false; } catch { }
                // Round-42 (field: puppets slid instead of walking): ThirdPersonCharacter.Update drives
                // the locomotion float itself — animator.SetFloat(BaseHuman.Forward, 0) every frame when
                // ITS movement logic sees no input — overwriting ours. The component's logic is dead
                // weight on a stream-driven body; disable it so our Forward value sticks. (Plain method
                // calls on the disabled component — SetHandContent, ShowExpression — still work.)
                tpc.enabled = false;
                return new Puppet { go = go, tpc = tpc, anim = tpc.animator, lastSeen = Time.unscaledTime, target = pos, yaw = yaw };
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] puppet spawn: {ex.Message}"); return null; }
        }

        private static void StartLeaving(Puppet pup)
        {
            pup.leaving = true;
            pup.leaveAt = Time.unscaledTime + 6f;   // hard stop even if no exit is reachable
            try
            {
                var zones = InstanceBehavior<BuildingManager>.Instance?.exitZones;
                if (zones != null && zones.Count > 0 && zones[0] != null)
                    pup.target = zones[0].transform.position;
            }
            catch { }
        }

        private static void UpdatePuppets()
        {
            if (_puppets.Count == 0) return;
            var dead = new List<string>();
            float now = Time.unscaledTime;
            foreach (var kv in _puppets)
            {
                var pup = kv.Value;
                if (pup.go == null) { dead.Add(kv.Key); continue; }
                // Stale stream (simulator disconnect / hitch) → walk out rather than freeze mid-stride.
                if (!pup.leaving && now - pup.lastSeen > 2.5f) StartLeaving(pup);

                var tr = pup.go.transform;
                Vector3 to = pup.target - tr.position;
                to.y = 0f;
                float dist = to.magnitude;
                float speed = Mathf.Clamp(dist / 0.25f, 0f, 4f);   // cover the gap by the next tick, capped
                if (dist > 0.02f)
                {
                    tr.position = Vector3.MoveTowards(tr.position, pup.target, speed * Time.deltaTime);
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.LookRotation(to.normalized), 10f * Time.deltaTime);
                }
                else
                {
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, pup.yaw, 0f), 10f * Time.deltaTime);
                    speed = 0f;
                }
                try { pup.anim?.SetFloat(BaseHuman.Forward, Mathf.Clamp01(speed / 3f)); } catch { }

                if (pup.leaving && (dist < 0.6f || now > pup.leaveAt)) dead.Add(kv.Key);
            }
            foreach (var k in dead)
            {
                if (_puppets.TryGetValue(k, out var pup))
                {
                    try { if (pup.heldGo != null) UnityEngine.Object.Destroy(pup.heldGo); } catch { }
                    try { if (pup.go != null) UnityEngine.Object.Destroy(pup.go); } catch { }
                    _puppets.Remove(k);
                }
            }
        }

        /// <summary>Round-42 — mirror the customer's hand prop (basket/box) via the carry-template pool.</summary>
        private static void UpdateHeld(Puppet pup, string held)
        {
            try
            {
                pup.held = held;
                pup.fill = -1;   // fresh prop — fill re-applies on the next row
                if (pup.heldGo != null) { try { UnityEngine.Object.Destroy(pup.heldGo); } catch { } pup.heldGo = null; }
                if (pup.tpc == null) return;
                if (string.IsNullOrEmpty(held)) { try { pup.tpc.SetHandContent(null); } catch { } return; }
                var clone = RemotePlayerManager.CloneHeldPropFor(held);
                if (clone == null) return;
                pup.heldGo = clone;
                // SetHandContent requires an ItemController on the prop and derefs its members
                // (TogglePhysics, Item.playerMountPosition, IK points) — keep the LOUD catch permanently:
                // a silent throw here cost three diagnosis rounds (2026-07-07).
                try
                {
                    pup.tpc.SetHandContent(clone.transform);   // native parenting + mount offsets + holding anim
                }
                catch (Exception shx)
                {
                    Plugin.Logger.LogWarning($"[Customers] SetHandContent('{held}') threw {shx.GetType().Name}: {shx.Message} — manual hand-parent fallback.");
                    ManualHandParent(pup, clone);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] UpdateHeld('{held}'): {ex.Message}"); }
        }

        /// <summary>Round-45e — fallback attach: parent the prop to the puppet's HandContent bone
        /// directly (identity local pose) when the native SetHandContent path refuses.</summary>
        private static void ManualHandParent(Puppet pup, GameObject clone)
        {
            try
            {
                if (pup.go == null) return;
                Transform? hand = null;
                foreach (var tr in pup.go.GetComponentsInChildren<Transform>(true))
                    if (tr != null && tr.name == "HandContent") { hand = tr; break; }
                if (hand == null) return;
                clone.transform.SetParent(hand, false);
                // Round-45f: the item's own mount offsets (what native SetHandContent applies) — identity
                // pose left the bag floating in front of the body.
                Vector3 mountPos = Vector3.zero; Vector3 mountRot = Vector3.zero;
                try
                {
                    var ic = clone.GetComponent<ItemController>();
                    var item = ic != null ? BigAmbitions.Items.ItemsGetter.GetByName(ic.itemName) : null;
                    if (item != null) { mountPos = item.playerMountPosition; mountRot = item.playerMountRotation; }
                }
                catch { }
                clone.transform.localPosition = mountPos;
                clone.transform.localEulerAngles = mountRot;
                try { pup.anim?.SetBool(BaseHuman.HoldingBox, true); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] manual hand-parent: {ex.Message}"); }
        }

        /// <summary>Round-45b — mirror basket fill ([PropProbe]-taught structure): toggle ONLY the
        /// "Products*" children; everything else on the prop (IK attach, shadow caster) stays untouched.</summary>
        private static void ApplyFill(Puppet pup, int fill)
        {
            try
            {
                pup.fill = fill;
                if (pup.heldGo == null) return;
                var t = pup.heldGo.transform;
                int seen = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    var ch = t.GetChild(i);
                    if (ch == null || !ch.name.StartsWith("Products", StringComparison.Ordinal)) continue;
                    ch.gameObject.SetActive(seen < fill);
                    seen++;
                }
            }
            catch { }
        }

        private static void DestroyAllPuppets()
        {
            foreach (var kv in _puppets)
            {
                try { if (kv.Value.heldGo != null) UnityEngine.Object.Destroy(kv.Value.heldGo); } catch { }
                try { if (kv.Value.go != null) UnityEngine.Object.Destroy(kv.Value.go); } catch { }
            }
            _puppets.Clear();
            _handNodes.Clear();
            _custEntryIds.Clear();
            _pendingEmotes.Clear();
            _warpHolds.Clear();
            // _looksById deliberately NOT cleared here — this runs on every building exit, and the whole
            // point of the cache is dressing REBUILT puppets on re-entry. Session Reset clears it.
        }
    }

    /// <summary>Round-42 — emoji/complaint parity for followers: every customer expression on the
    /// SIMULATING machine streams to the puppets (Customer.Init complaints, no-paperbag reactions, …).
    /// ShowExpression is the single native funnel for customer emoji bubbles.</summary>
    [HarmonyLib.HarmonyPatch(typeof(ThirdPersonCharacter), nameof(ThirdPersonCharacter.ShowExpression))]
    public static class Patch_ShowExpression_PuppetMirror
    {
        static void Postfix(ThirdPersonCharacter __instance, CharacterEmojiName characterEmojiName, float secondsToShow)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                CustomerPuppets.OnLocalCustomerEmote(__instance, (int)characterEmojiName, secondsToShow);
            }
            catch { }
        }
    }
}
