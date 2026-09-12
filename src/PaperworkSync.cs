using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 3-A — PAPERWORK PUBLISH (plan §9, D13 approved 2026-09-11).
    ///
    /// The interior/business syncs carry a shop's STRUCTURE and its stock/prices.  They have never
    /// carried the shop's PAPERWORK — the per-business books (orderHistory, today's till, factory
    /// exports, marketing campaigns) and the per-OWNER agreements that name a business (delivery
    /// contracts, import partnerships, the four manager plans, installation/moving contracts,
    /// licensing-fee state) plus the FULL employee records behind the 7-field roster publish.
    /// All of that lives only in the owning member's own save, so when that member is absent the
    /// host has nothing to hand a simulator with.
    ///
    /// THIS BUILD ONLY PUBLISHES AND STORES.  Nothing here changes gameplay: every member's machine
    /// sends its own bundle to the host (client → host only), the host keeps the latest bundle per
    /// member in the mod's session manifest (manifest.bamp.json — NEVER a .hsg field), and P3-B is
    /// the build that hands a stored bundle to a simulator.
    ///
    /// INERTNESS: nothing runs unless MergerSync.IAmMember.  A non-member's tick is one bool read.
    ///
    /// CADENCE (events over timers): a dirty flag set by Postfixes on the game's OWN mutation
    /// points, flushed by the existing 1 Hz canvas tick — at most one bundle per 30 s while dirty,
    /// ALWAYS at the game-day change, ALWAYS right before a coordinated save upload, and once on
    /// the membership rising edge.  There is no one-shot delay anywhere in this file.
    /// </summary>
    public static class PaperworkSync
    {
        /// <summary>A bundle bigger than this is refused rather than shipped (design A3 size guard).</summary>
        public const int MaxBundleBytes = 2 * 1024 * 1024;

        private const float MinPublishInterval = 30f;   // while merely dirty

        private static bool  _dirty;
        private static bool  _wasMember;
        private static float _nextTick;
        private static float _lastPublishAt = -999f;
        private static int   _lastPublishedDay = -1;

        /// <summary>Set by the game's own mutation points (below) and by the day change.  Cheap and
        /// idempotent — the flush decides whether anything actually goes out.</summary>
        public static void MarkDirty() { _dirty = true; }

        // ── Tick (1 Hz, chained off the existing canvas pre-block via MPClient.TickWorldReadyGate) ──

        public static void Tick()
        {
            try
            {
                if (UnityEngine.Time.unscaledTime < _nextTick) return;
                _nextTick = UnityEngine.Time.unscaledTime + 1f;

                // r3 (re-review r2 MAJOR-1): never publish from a world that is not settled - between a load's
                // store restore and the new world going live, SaveGameManager.Current can still be the world
                // being LEFT, and a rising-edge/day publish then would refill the store with abandoned-timeline
                // books (PersistGrantsNow could even persist them). Same gate as ClientSaveBody.
                if (!MPWorldReady.IsSettled) return;

                if (!MergerSync.IAmMember)
                {
                    // Dissolved / never merged: forget the edge so a later join publishes again.
                    _wasMember = false; _dirty = false; _lastPublishedDay = -1;
                    return;
                }

                if (!_wasMember)
                {
                    _wasMember = true;
                    Publish("membership");   // rising edge: the host gets a full bundle at once
                    return;
                }

                int day = -1;
                try { day = GameStateReader.GetGameTime().day; } catch { }
                if (day > 0 && day != _lastPublishedDay) { Publish("day"); return; }

                if (_dirty && UnityEngine.Time.unscaledTime - _lastPublishAt >= MinPublishInterval)
                    Publish("dirty");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] tick: {ex.Message}"); }
        }

        /// <summary>Force one publish now (the coordinated-save hooks and the TestDrive verb).
        /// Returns the bundle that went out, or null when nothing was published.</summary>
        public static BusinessPaperworkPayload? FlushNow(string why) => Publish(why);

        private static BusinessPaperworkPayload? Publish(string why)
        {
            if (!MPWorldReady.IsSettled) { Plugin.Logger.LogInfo($"[Paperwork] publish ({why}) skipped - world not settled"); return null; }   // r3: see Tick
            try
            {
                if (!MergerSync.IAmMember) return null;
                var p = Build();
                if (p == null) return null;

                string json;
                try { json = Newtonsoft.Json.JsonConvert.SerializeObject(p); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] serialise: {ex.Message}"); return null; }
                int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
                if (bytes > MaxBundleBytes)
                {
                    // Review r1 MAJOR-6: stamp the DAY as well. Without it the day-change branch
                    // re-fired on every 1 Hz tick for the rest of that day — a full rebuild plus this
                    // WARN once a second. The bundle stays UN-SENT (the host keeps the last one that
                    // fit); the next day, or the next change once the interval passes, tries again.
                    if (p.Day != _lastPublishedDay)
                        Plugin.Logger.LogWarning($"[Paperwork] bundle REFUSED: {bytes} bytes > {MaxBundleBytes} cap ({why}) — nothing sent; the host's previously stored bundle stands.");
                    _dirty = false; _lastPublishAt = UnityEngine.Time.unscaledTime;
                    _lastPublishedDay = p.Day;
                    return null;
                }

                // r7: counted BEFORE the store - on the host, StorePaperwork may MOVE parts out of this very
                // bundle (filing for an absent owner; the return guard), and the log should say what was built.
                int nBiz = p.Businesses.Count, nItems = CountListItems(p.Lists), nEmp = p.Employees.Count;
                if (MPServer.IsRunning)
                    MPServer.StorePaperwork(p, MPConfig.PlayerId);   // the host is a member too — applied locally
                else if (MPClient.IsConnected)
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BusinessPaperwork, MPConfig.PlayerId, p));
                else
                    return null;   // not in a session: nothing to publish to

                _dirty = false;
                _lastPublishAt = UnityEngine.Time.unscaledTime;
                _lastPublishedDay = p.Day;
                Plugin.Logger.LogInfo($"[Paperwork] published day {p.Day}: {nBiz} businesses, "
                    + $"{nItems} list items, {nEmp} employees, {bytes} bytes ({why}).");
                return p;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] publish: {ex.Message}"); return null; }
        }

        public static int CountListItems(PaperworkOwnerLists? l)
        {
            if (l == null) return 0;
            return (l.DeliveryContracts?.Count ?? 0) + (l.ImportPartnerships?.Count ?? 0)
                 + (l.ItemsOrderedThisWeekByImporter?.Count ?? 0) + (l.LogisticsManagerPlans?.Count ?? 0)
                 + (l.PricingManagerPlans?.Count ?? 0) + (l.HrManagerPlans?.Count ?? 0)
                 + (l.HeadhunterPlans?.Count ?? 0) + (l.InteriorInstallationFirmContracts?.Count ?? 0)
                 + (l.MovingServiceContracts?.Count ?? 0) + (l.DisabledLicensingFees?.Count ?? 0)
                 + (l.PaidLicensingFeesToday?.Count ?? 0);
        }

        // ── Bundle builder ────────────────────────────────────────────────────

        /// <summary>Build this machine's bundle: the businesses that are TRULY this player's (never a
        /// merger-flipped partner shop — that shop's own owner publishes it), the owner lists filtered
        /// to those addresses, and the full records of the staff assigned to them.</summary>
        public static BusinessPaperworkPayload? Build()
        {
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return null;

            var p = new BusinessPaperworkPayload
            {
                PlayerId = MPConfig.PlayerId,
                StableId = MPConfig.StableId,
                Day      = 0,
            };
            try { p.Day = GameStateReader.GetGameTime().day; } catch { }

            var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null) continue;
                string key; try { key = GameStateReader.AddressKey(reg); } catch { continue; }
                if (string.IsNullOrEmpty(key)) continue;
                // P3-B (B3e): a business this machine SIMULATES for an absent owner publishes from here
                // too - it is the only machine running it, so this bundle is the only live record of its
                // books. The settled gate is INHERITED, not re-implemented: Build() is reached only
                // through Publish(), which returns before this whenever MPWorldReady.IsSettled is false
                // (P3-A r3) - so a marked address can never refill the store from an abandoned timeline.
                if (!MergerFlip.TrulyMine(reg) && !MergerAbsence.SimulatesHere(key)) continue;
                if (!mine.Add(key)) continue;
                p.Businesses.Add(BuildOne(reg, key));
            }

            p.Lists     = BuildLists(gi, mine);
            p.Employees = BuildEmployees(gi, mine);
            return p;
        }

        // ══ P3-B r4 (F2) - PER-ADDRESS BUNDLE SURGERY ══════════════════════════════════════════
        // A SIMULATOR publishes an absent owner's shops inside its OWN bundle (the SimulatesHere gate in
        // Build above), so the host has to take them back OUT and file them under the owner. Both halves
        // are address-filtered exactly the way MergerAbsence.InstallListsFor selects what to install, so
        // what is filed is what will be handed back.

        /// <summary>The IMPORTER addresses that follow these HQ addresses: itemsOrderedThisWeekByImporter
        /// is keyed by the importer's building, never by one of the owner's own.</summary>
        private static HashSet<string> ImporterKeysFor(PaperworkOwnerLists? l, HashSet<string> addrs)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (l?.ImportPartnerships == null) return keys;
            foreach (var ip in l.ImportPartnerships)
                if (ip != null && addrs.Contains(ip.HeadquartersAddressKey ?? "") && !string.IsNullOrEmpty(ip.ImportAddressKey))
                    keys.Add(ip.ImportAddressKey);
            return keys;
        }

        /// <summary>Move (or, with a null destination, DROP) every element a predicate picks. Returns how
        /// many moved.</summary>
        private static int Move<T>(List<T>? src, List<T>? dst, System.Func<T, bool> pick)
        {
            if (src == null) return 0;
            int n = 0;
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null && pick(src[i])) { dst?.Add(src[i]); n++; }
            if (n > 0) src.RemoveAll(x => x != null && pick(x));
            return n;
        }

        private static void EnsureParts(BusinessPaperworkPayload b)
        {
            b.Businesses ??= new List<BusinessPaperwork>();
            b.Employees  ??= new List<EmployeeEditPayload>();
            b.Lists      ??= new PaperworkOwnerLists();
        }

        /// <summary>Every part of `addrs` leaves `from` and (when `to` is given) joins it. Returns the
        /// number of parts that moved - 0 means this bundle held nothing of those addresses.</summary>
        private static int Surgery(BusinessPaperworkPayload from, BusinessPaperworkPayload? to, HashSet<string> addrs)
        {
            if (from == null || addrs == null || addrs.Count == 0) return 0;
            EnsureParts(from);
            if (to != null) EnsureParts(to);
            bool A(string? s) => !string.IsNullOrEmpty(s) && addrs.Contains(s!);
            var imp = ImporterKeysFor(from.Lists, addrs);
            int n = 0;
            n += Move(from.Businesses, to?.Businesses, b => A(b.AddressKey));
            n += Move(from.Employees,  to?.Employees,  e => A(e.AddressKey));
            var l = from.Lists; var t = to?.Lists;
            n += Move(l.DeliveryContracts,     t?.DeliveryContracts,     x => A(x.BusinessAddressKey));
            n += Move(l.ImportPartnerships,    t?.ImportPartnerships,    x => A(x.HeadquartersAddressKey));
            n += Move(l.LogisticsManagerPlans, t?.LogisticsManagerPlans, x => A(x.HeadquartersAddressKey));
            n += Move(l.PricingManagerPlans,   t?.PricingManagerPlans,   x => A(x.HeadquartersAddressKey));
            n += Move(l.HrManagerPlans,        t?.HrManagerPlans,        x => A(x.HeadquartersAddressKey));
            n += Move(l.HeadhunterPlans,       t?.HeadhunterPlans,       x => A(x.HeadquartersAddressKey));
            n += Move(l.InteriorInstallationFirmContracts, t?.InteriorInstallationFirmContracts, x => A(x.AddressKey));
            // A move NAMES two addresses and belongs to whichever end is being filed (the installer
            // already refuses to install it twice).
            n += Move(l.MovingServiceContracts, t?.MovingServiceContracts, x => A(x.OriginAddressKey) || A(x.DestinationAddressKey));
            n += Move(l.DisabledLicensingFees,  t?.DisabledLicensingFees,  x => A(x.AddressKey));
            n += Move(l.PaidLicensingFeesToday, t?.PaidLicensingFeesToday, x => A(x.AddressKey));
            n += Move(l.ItemsOrderedThisWeekByImporter, t?.ItemsOrderedThisWeekByImporter, x => imp.Contains(x.AddressKey ?? ""));
            return n;
        }

        /// <summary>HOST (F2): lift these addresses out of an incoming bundle into a bundle of their own.
        /// The parts end up in exactly one of the two.</summary>
        public static BusinessPaperworkPayload SplitOutAddresses(BusinessPaperworkPayload from, HashSet<string> addrs, out int movedParts)
        {
            var moved = new BusinessPaperworkPayload();
            movedParts = Surgery(from, moved, addrs);
            return moved;
        }

        /// <summary>HOST (F2): put a split-out bundle INTO the owner's stored one, replacing whatever that
        /// one held for the SAME addresses and leaving every other address of it exactly as it was.</summary>
        public static void MergeAddresses(BusinessPaperworkPayload into, BusinessPaperworkPayload moved, HashSet<string> addrs)
        {
            if (into == null || moved == null) return;
            Surgery(into, null, addrs);      // the owner's stale copy of exactly these addresses goes
            Surgery(moved, into, addrs);     // and the simulator's fresh one takes its place
        }

        private static BusinessPaperwork BuildOne(BuildingRegistration reg, string key)
        {
            var b = new BusinessPaperwork { AddressKey = key };
            try
            {
                if (reg.orderHistory != null)
                    foreach (var h in reg.orderHistory)
                    {
                        if (h == null) continue;
                        var e = new PwOrderHistoryEntry
                        {
                            DayNumber = h.dayNumber, TotalCustomers = h.totalCustomers, TotalRevenue = h.totalRevenue,
                        };
                        if (h.itemSales != null)
                            foreach (var s in h.itemSales)
                                if (s != null)
                                    e.ItemSales.Add(new PwItemReport
                                    {
                                        ItemName = s.itemName, AmountSold = s.amountSold,
                                        TotalPrice = s.totalPrice, TotalWholesalePrice = s.totalWholesalePrice,
                                    });
                        if (h.hourReports != null)
                            foreach (var r in h.hourReports)
                                if (r != null) e.HourReports.Add(new PwHourReport { Hour = r.hour, Customers = r.customers });
                        b.OrderHistory.Add(e);
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] orderHistory '{key}': {ex.Message}"); }

            try
            {
                if (reg.unprocessedCompletedOrders != null)
                    foreach (var o in reg.unprocessedCompletedOrders)
                    {
                        if (o == null) continue;
                        var po = new PwOrder
                        {
                            Completed = o.completed, CustomerServiceSkill = o.customerServiceSkill,
                            Cleanliness = o.cleanliness, CustomerDemandScore = o.customerDemandScore,
                        };
                        if (o.customerDemandTypes != null) po.CustomerDemandTypes.AddRange(o.customerDemandTypes);
                        if (o.entries != null)
                            foreach (var oe in o.entries)
                                if (oe != null)
                                    po.Entries.Add(new PwOrderEntry
                                    {
                                        ItemName = oe.itemName, Price = oe.price, Available = oe.available,
                                        PriceAcceptable = oe.priceAccceptable, Paid = oe.paid,
                                        Processed = oe.processed, WholesalePrice = oe.wholesalePrice,
                                    });
                        b.UnprocessedCompletedOrders.Add(po);
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] till '{key}': {ex.Message}"); }

            try
            {
                if (reg.factoryExports != null)
                    foreach (var f in reg.factoryExports)
                        if (f != null)
                            b.FactoryExports.Add(new PwFactoryExport
                            {
                                ItemName = f.itemName, Amount = f.amount,
                                TotalIngredientsCost = f.totalIngredientsCost, TotalPrice = f.totalPrice,
                            });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] factoryExports '{key}': {ex.Message}"); }

            try
            {
                if (reg.marketingCampaigns != null)
                    foreach (var c in reg.marketingCampaigns)
                        if (c != null)
                            b.MarketingCampaigns.Add(new PwMarketingCampaign
                            {
                                AgencyAddressKey = Key(c.agencyAddress),
                                MarketingTypeName = c.marketingTypeName.ToString(),
                                Enabled = c.enabled,
                            });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] campaigns '{key}': {ex.Message}"); }

            return b;
        }

        private static PaperworkOwnerLists BuildLists(GameInstance gi, HashSet<string> mine)
        {
            var l = new PaperworkOwnerLists();
            try
            {
                if (gi.DeliveryContracts != null)
                    foreach (var d in gi.DeliveryContracts)
                    {
                        if (d == null || !mine.Contains(Key(d.businessAddress))) continue;
                        var pd = new PwDeliveryContract
                        {
                            BusinessAddressKey = Key(d.businessAddress), WholesaleAddressKey = Key(d.wholesaleAddress),
                            Enabled = d.enabled, IsUrgentOrder = d.isUrgentOrder, NextDeliveryDay = d.nextDeliveryDay,
                            RepeatingOrder = d.repeatingOrder, DeliveryFee = d.deliveryFee,
                        };
                        if (d.items != null)
                            foreach (var it in d.items)
                                if (it != null)
                                    pd.Items.Add(new PwItemOrderLine
                                    {
                                        ItemName = it.itemName, Boxes = it.boxes, Amount = it.amount,
                                        AmountOrderedLastWeek = it.amountOrderedLastWeek,
                                        AmountOrderedThisWeek = it.amountOrderedThisWeek,
                                    });
                        l.DeliveryContracts.Add(pd);
                    }

                // Review r1 MAJOR-5: itemsOrderedThisWeekByImporter below is keyed by the IMPORTER's
                // address (the partnership's importAddress), NOT by one of our businesses — so the
                // sender's own importer keys are the importAddresses of the partnerships it owns.
                // Collected here, used below; filtering that map by 'mine' was always empty.
                var myImportKeys = new HashSet<string>();
                if (gi.importPartnerships != null)
                    foreach (var ip in gi.importPartnerships)
                    {
                        if (ip == null || !mine.Contains(Key(ip.headquartersAddress))) continue;
                        string impKey = Key(ip.importAddress);
                        if (!string.IsNullOrEmpty(impKey)) myImportKeys.Add(impKey);
                        var pi = new PwImportPartnership
                        {
                            Id = ip.id, HeadquartersAddressKey = Key(ip.headquartersAddress),
                            ImportAddressKey = Key(ip.importAddress), EmployeeInstanceId = ip.employeeInstanceId,
                            NextDeliveryDay = ip.nextDeliveryDay, IsRepeatingOrder = ip.isRepeatingOrder,
                            DaysUntilRepeat = ip.daysUntilRepeat, IsActive = ip.isActive,
                            IsUrgentOrder = ip.isUrgentOrder, IsTarget = ip.isTarget,
                        };
                        if (ip.products != null)
                            foreach (var pr in ip.products)
                                if (pr != null)
                                    pi.Products.Add(new PwItemOrderLine
                                    {
                                        ItemName = pr.itemName, Amount = pr.amount,
                                        AmountOrderedLastWeek = pr.amountOrderedLastWeek,
                                        AmountOrderedThisWeek = pr.amountOrderedThisWeek,
                                        AssignedWarehouseKey = Key(pr.assignedWarehouse),
                                    });
                        l.ImportPartnerships.Add(pi);
                    }

                if (gi.itemsOrderedThisWeekByImporter != null)
                    foreach (var kv in gi.itemsOrderedThisWeekByImporter)
                    {
                        string k = Key(kv.Key);
                        if (!myImportKeys.Contains(k)) continue;   // importer address, not ours (MAJOR-5)
                        var row = new PwImporterWeekOrder { AddressKey = k };
                        if (kv.Value != null)
                            foreach (var t in kv.Value)
                                row.Items.Add(new PwItemOrderLine { ItemName = t.itemName, Amount = t.targetAmount });
                        l.ItemsOrderedThisWeekByImporter.Add(row);
                    }

                if (gi.logisticsManagerPlans != null)
                    foreach (var pl in gi.logisticsManagerPlans)
                    {
                        if (pl == null || !mine.Contains(Key(pl.headquartersAddress))) continue;
                        var pp = new PwLogisticsPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = Key(pl.headquartersAddress),
                            TargetAddressKey = Key(pl.targetAddress), IsFactory = pl.isFactory,
                        };
                        if (pl.destinations != null)
                            foreach (var d in pl.destinations)
                            {
                                if (d == null) continue;
                                var pdst = new PwLogisticsDestination { DeliveryTargetAddressKey = Key(d.deliveryTargetAddress) };
                                if (d.stockTargets != null)
                                    foreach (var t in d.stockTargets)
                                        pdst.StockTargets.Add(new PwItemOrderLine { ItemName = t.itemName, Amount = t.targetAmount });
                                pp.Destinations.Add(pdst);
                            }
                        l.LogisticsManagerPlans.Add(pp);
                    }

                if (gi.pricingManagerPlans != null)
                    foreach (var pl in gi.pricingManagerPlans)
                    {
                        if (pl == null || !mine.Contains(Key(pl.headquartersAddress))) continue;
                        l.PricingManagerPlans.Add(new PwPricingPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = Key(pl.headquartersAddress),
                            SupervisedNeighborhood = pl.supervisedNeighborhood,
                            NextUpdateDay = pl.nextUpdateDay, NextUpdateHour = pl.nextUpdateHour,
                            ManuallyPricedItems = pl.manuallyPricedItems == null
                                ? new List<string>() : new List<string>(pl.manuallyPricedItems),
                        });
                    }

                if (gi.hrManagerPlans != null)
                    foreach (var pl in gi.hrManagerPlans)
                    {
                        if (pl == null || !mine.Contains(Key(pl.headquartersAddress))) continue;
                        l.HrManagerPlans.Add(new PwHrPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = Key(pl.headquartersAddress),
                            AssignedEmployees = pl.assignedEmployees == null
                                ? new List<string>() : new List<string>(pl.assignedEmployees),
                            ReplaceAbsentEmployees = pl.replaceAbsentEmployees, TrainingTarget = pl.trainingTarget,
                        });
                    }

                if (gi.headhunterPlans != null)
                    foreach (var pl in gi.headhunterPlans)
                    {
                        if (pl == null || !mine.Contains(Key(pl.headquartersAddress))) continue;
                        l.HeadhunterPlans.Add(new PwHeadhunterPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = Key(pl.headquartersAddress),
                            AssignedHrPlans = pl.assignedHrPlans == null
                                ? new List<string>() : new List<string>(pl.assignedHrPlans),
                            IsRecruiting = pl.isRecruiting, SkillRecruiting = pl.skillRecruiting,
                            SkillValueTarget = pl.skillValueTarget,
                            DealBreakerTypes = pl.dealBreakerTypes == null
                                ? new List<string>() : new List<string>(pl.dealBreakerTypes),
                            AutomaticallyReplaceOnRetire = pl.automaticallyReplaceOnRetire,
                            AutomaticallyReplaceOnResign = pl.automaticallyReplaceOnResign,
                            RemainingCandidatesToRecruit = pl.remainingCandidatesToRecruit,
                            AmountOfCandidatesToRecruitPreference = pl.amountOfCandidatesToRecruitPreference,
                        });
                    }

                if (gi.interiorInstallationFirmContracts != null)
                    foreach (var c in gi.interiorInstallationFirmContracts)
                    {
                        if (c == null || !mine.Contains(Key(c.addressToDoTheInstallation))) continue;
                        l.InteriorInstallationFirmContracts.Add(new PwInstallContract
                        {
                            FirmAddressKey = Key(c.interiorInstallationFirmAddress),
                            AddressKey = Key(c.addressToDoTheInstallation),
                            DesignName = c.designName, IsBlueprint = c.isBlueprint, IsCompatBlueprint = c.isCompatBlueprint,
                            HasDiscontinuedItems = c.hasDiscontinuedItems, DayOfInstallation = c.dayOfInstallation,
                            BusinessTypeName = c.businessTypeName,
                        });
                    }

                if (gi.movingServiceContracts != null)
                    foreach (var c in gi.movingServiceContracts)
                    {
                        if (c == null) continue;
                        string origin = Key(c.originMovingAddress), dest = Key(c.destinationMovingAddress);
                        if (!mine.Contains(origin) && !mine.Contains(dest)) continue;   // a move NAMES two addresses; either being mine makes it my paperwork
                        l.MovingServiceContracts.Add(new PwMovingContract
                        {
                            OriginAddressKey = origin, DestinationAddressKey = dest,
                            MovingCompanyAddressKey = c.movingCompanyRegistration == null
                                ? "" : SafeRegKey(c.movingCompanyRegistration),
                            MovingDay = c.movingDay, MovingHour = c.movingHour,
                            TransferBizManSettings = c.transferBizManSettings,
                        });
                    }

                if (gi.disabledLicensingFees != null)
                    foreach (var f in gi.disabledLicensingFees)
                    {
                        if (f == null || !mine.Contains(Key(f.address))) continue;
                        l.DisabledLicensingFees.Add(new PwLicensingFee
                        { AddressKey = Key(f.address), ItemId = f.itemId, Day = (int)f.day });
                    }

                if (gi.paidLicensingFeesToday != null)
                    foreach (var t in gi.paidLicensingFeesToday)
                    {
                        string k = Key(t.Item1);
                        if (!mine.Contains(k)) continue;
                        l.PaidLicensingFeesToday.Add(new PwLicensingFee { AddressKey = k, ItemId = t.Item2, Day = -1 });
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] owner lists: {ex.Message}"); }
            return l;
        }

        /// <summary>Full records of the staff assigned to MY businesses.  Reuses the merger ADOPT DTO
        /// (EmployeeEditPayload, Action="record") extended with the fields the adopt path stamps by
        /// hand — so P3-B can reconstruct a real EmployeeInstance instead of a fresh hire.</summary>
        private static List<EmployeeEditPayload> BuildEmployees(GameInstance gi, HashSet<string> mine)
        {
            var outp = new List<EmployeeEditPayload>();
            try
            {
                if (gi.EmployeeInstances == null) return outp;
                foreach (var e in gi.EmployeeInstances)
                {
                    if (e == null || e.IsCandidate) continue;
                    string key; try { key = Key(e.assignedAddress); } catch { continue; }
                    if (string.IsNullOrEmpty(key) || !mine.Contains(key)) continue;

                    var r = new EmployeeEditPayload
                    {
                        PlayerId = MPConfig.PlayerId, Action = "record",
                        AddressKey = key, EmployeeId = e.id ?? "",
                        Wage = e.hourlyWage, Satisfaction = e.satisfaction,
                        DayHired = e.dayHired, NextSickDay = e.nextSickDay,
                        WorkedHoursToday = e.workedHoursToday, WorkedHoursThisWeek = e.workedHoursThisWeek,
                        WorkedDays = e.workedDays, AssignedWeeklyHours = e.assignedWeeklyHours,
                        IsAbsent = e.isAbsent, IsReplaced = e.isReplaced, IsBeingReplaced = e.isBeingReplaced,
                        IsTrainingDay = e.isTrainingDay, HasSendQuitWarning = e.hasSendQuitWarning,
                        SendRetirementNotice = e.sendRetirementNotice,
                        AssignedHrManagerPlanId = e.assignedHrManagerPlanId ?? "",
                        InitialCombinedSkillAmount = e.initialCombinedSkillAmount,
                        PresetId = e.presetId ?? "",
                    };
                    try { var cd = e.characterData; if (cd != null) { r.Name = cd.name ?? ""; r.Gender = (int)cd.gender; r.AgeDays = cd.ageInDays; } } catch { }
                    try
                    {
                        var sk = e.characterData?.skills;
                        if (sk != null)
                            foreach (var s in sk)
                                if (s != null)
                                    r.Skills.Add(s.name + "=" + s.value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    catch { }
                    try { if (e.demands != null) r.Demands.AddRange(e.demands); } catch { }
                    try { if (e.assignedWorkStationItems != null) r.AssignedWorkStationItems.AddRange(e.assignedWorkStationItems); } catch { }
                    try { if (e.assignedWeeklyDays != null) foreach (var d in e.assignedWeeklyDays) r.AssignedWeeklyDays.Add((int)d); } catch { }
                    try
                    {
                        if (e.trainingSession != null)
                        { r.TrainingSkill = e.trainingSession.skill ?? ""; r.TrainingStartDay = e.trainingSession.startDay; }
                    }
                    catch { }
                    try
                    {
                        var c = e.complaintData;
                        if (c != null)
                        {
                            r.ComplaintIsComplaining = c.isComplaining;
                            r.ComplaintHoursUntilNext = c.hoursUntilNextComplaint;
                            r.ComplaintDeadlineHours = c.complaintDeadlineHours;
                            r.ComplaintHasRival = c.hasRival;
                        }
                    }
                    catch { }
                    outp.Add(r);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] employees: {ex.Message}"); }
            return outp;
        }

        // == MERGER PHASE 3-C (C2b) - THE BOOKS BACK ONTO THE OWNER'S REGISTRATIONS ==

        /// <summary>THE RETURNED OWNER, MAIN THREAD: replace orderHistory / unprocessedCompletedOrders /
        /// factoryExports / marketingCampaigns of exactly these addresses from the returned bundle - the
        /// inverse of BuildOne, and the owner writing its OWN registrations. Nothing outside `addrs` is
        /// touched (D2), and an address with no registration here is skipped with a line. The element
        /// types are not csproj-named, so each list's own generic argument mints its elements (the same
        /// trick the absence installer uses); every part is independent, so one renamed game field costs
        /// that part only. Returns how many business records were written.</summary>
        public static int ApplyReturnedBusinesses(BusinessPaperworkPayload bundle, HashSet<string> addrs)
        {
            int n = 0;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null || bundle?.Businesses == null || addrs == null || addrs.Count == 0) return 0;
                foreach (var b in bundle.Businesses)
                {
                    if (b == null || string.IsNullOrEmpty(b.AddressKey) || !addrs.Contains(b.AddressKey)) continue;
                    BuildingRegistration? reg = null;
                    foreach (var r in gi.BuildingRegistrations)
                        if (r != null && SafeRegKey(r) == b.AddressKey) { reg = r; break; }
                    if (reg == null)
                    {
                        Plugin.Logger.LogWarning($"[Paperwork] returned books for '{b.AddressKey}': no registration on this "
                                               + "machine - skipped (my own state stands for it).");
                        continue;
                    }
                    int oh = PwFill<PwOrderHistoryEntry>(reg, "orderHistory", b.OrderHistory, (o, h) =>
                    {
                        PwSet(o, "dayNumber", h.DayNumber);
                        PwSet(o, "totalCustomers", h.TotalCustomers);
                        PwSet(o, "totalRevenue", h.TotalRevenue);
                        PwFill<PwItemReport>(o, "itemSales", h.ItemSales, (s, i) =>
                        {
                            PwSet(s, "itemName", i.ItemName);
                            PwSet(s, "amountSold", i.AmountSold);
                            PwSet(s, "totalPrice", i.TotalPrice);
                            PwSet(s, "totalWholesalePrice", i.TotalWholesalePrice);
                        });
                        PwFill<PwHourReport>(o, "hourReports", h.HourReports, (s, r2) =>
                        {
                            PwSet(s, "hour", r2.Hour);
                            PwSet(s, "customers", r2.Customers);
                        });
                    });
                    int till = PwFill<PwOrder>(reg, "unprocessedCompletedOrders", b.UnprocessedCompletedOrders, (o, po) =>
                    {
                        PwSet(o, "completed", po.Completed);
                        PwSet(o, "customerServiceSkill", po.CustomerServiceSkill);
                        PwSet(o, "cleanliness", po.Cleanliness);
                        PwSet(o, "customerDemandScore", po.CustomerDemandScore);
                        PwFillStrings(o, "customerDemandTypes", po.CustomerDemandTypes);
                        PwFill<PwOrderEntry>(o, "entries", po.Entries, (e, oe) =>
                        {
                            PwSet(e, "itemName", oe.ItemName);
                            PwSet(e, "price", oe.Price);
                            PwSet(e, "available", oe.Available);
                            PwSet(e, "priceAccceptable", oe.PriceAcceptable);   // the game's own spelling
                            PwSet(e, "paid", oe.Paid);
                            PwSet(e, "processed", oe.Processed);
                            PwSet(e, "wholesalePrice", oe.WholesalePrice);
                        });
                    });
                    int fx = PwFill<PwFactoryExport>(reg, "factoryExports", b.FactoryExports, (o, f) =>
                    {
                        PwSet(o, "itemName", f.ItemName);
                        PwSet(o, "amount", f.Amount);
                        PwSet(o, "totalIngredientsCost", f.TotalIngredientsCost);
                        PwSet(o, "totalPrice", f.TotalPrice);
                    });
                    int mc = PwFill<PwMarketingCampaign>(reg, "marketingCampaigns", b.MarketingCampaigns, (o, c) =>
                    {
                        var a = MergerAbsence.AddressOfKey(c.AgencyAddressKey);
                        if (a != null) PwFieldOf(o, "agencyAddress")?.SetValue(o, a);
                        PwSetEnumByName(o, "marketingTypeName", c.MarketingTypeName);
                        PwSet(o, "enabled", c.Enabled);
                    });
                    n++;
                    Plugin.Logger.LogInfo($"[Paperwork] returned books for '{b.AddressKey}': {oh} day(s) of history, "
                                        + $"{till} unprocessed order(s), {fx} factory export(s), {mc} campaign(s).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] returned books: {ex.Message}"); }
            return n;
        }

        private const System.Reflection.BindingFlags PwFlags = System.Reflection.BindingFlags.Public
                                                             | System.Reflection.BindingFlags.NonPublic
                                                             | System.Reflection.BindingFlags.Instance;

        private static System.Reflection.FieldInfo? PwFieldOf(object o, string name)
        {
            try
            {
                if (o == null || string.IsNullOrEmpty(name)) return null;
                for (var t = o.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, PwFlags);
                    if (f != null) return f;
                }
            }
            catch { }
            return null;
        }

        private static void PwSet(object o, string name, object val)
        {
            try
            {
                var f = PwFieldOf(o, name);
                if (f == null || val == null) return;
                f.SetValue(o, f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, val) : Convert.ChangeType(val, f.FieldType));
            }
            catch { }
        }

        /// <summary>An enum field written from the NAME BuildOne serialised (ToString()).</summary>
        private static void PwSetEnumByName(object o, string name, string value)
        {
            try
            {
                var f = PwFieldOf(o, name);
                if (f == null || !f.FieldType.IsEnum || string.IsNullOrEmpty(value)) return;
                f.SetValue(o, Enum.Parse(f.FieldType, value, ignoreCase: true));
            }
            catch { }
        }

        /// <summary>Rebuild one list field from DTO rows: the field's own generic argument mints the
        /// elements and `fill` writes them. Returns how many rows the list ended up holding.</summary>
        private static int PwFill<T>(object owner, string fieldName, List<T>? src, System.Action<object, T> fill)
        {
            try
            {
                var f = PwFieldOf(owner, fieldName);
                if (f == null) return 0;
                var list = f.GetValue(owner) as System.Collections.IList;
                if (list == null)
                {
                    list = Activator.CreateInstance(f.FieldType) as System.Collections.IList;
                    if (list == null) return 0;
                    f.SetValue(owner, list);
                }
                var et = f.FieldType.GetGenericArguments();
                if (et.Length != 1) return 0;
                list.Clear();
                foreach (var s in src ?? new List<T>())
                {
                    if (s == null) continue;
                    var e = Activator.CreateInstance(et[0]);
                    fill(e, s);
                    list.Add(e);
                }
                return list.Count;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] returned '{fieldName}': {ex.Message}"); return 0; }
        }

        /// <summary>A string COLLECTION field (List, HashSet or a fixed array), replaced wholesale.</summary>
        private static void PwFillStrings(object owner, string fieldName, List<string>? values)
        {
            try
            {
                var f = PwFieldOf(owner, fieldName);
                if (f == null) return;
                values ??= new List<string>();
                if (f.FieldType.IsArray)
                {
                    var arr = Array.CreateInstance(f.FieldType.GetElementType(), values.Count);
                    for (int i = 0; i < values.Count; i++) arr.SetValue(values[i], i);
                    f.SetValue(owner, arr);
                    return;
                }
                var cur = f.GetValue(owner);
                if (cur == null)
                {
                    cur = Activator.CreateInstance(f.FieldType);
                    if (cur == null) return;
                    f.SetValue(owner, cur);
                }
                var t = cur.GetType();
                t.GetMethod("Clear", Type.EmptyTypes)?.Invoke(cur, null);
                var add = t.GetMethod("Add", new[] { typeof(string) });
                if (add == null) return;
                foreach (var s in values) add.Invoke(cur, new object[] { s ?? "" });
            }
            catch { }
        }

        private static string Key(Address a) { try { return GameStateReader.AddressKey(a); } catch { return ""; } }
        private static string SafeRegKey(BuildingRegistration r) { try { return GameStateReader.AddressKey(r); } catch { return ""; } }

        public static void Reset()
        {
            _dirty = false; _wasMember = false; _lastPublishedDay = -1; _lastPublishAt = -999f;
        }

        // ── Mutation points (the game's OWN events; no timers, no one-shot delays) ──

        /// <summary>Order completion — the one choke point every business simulator funnels through
        /// (RetailBusinessSimulator :219, Gym :70, Office :64, CinemaTheater :54 all Add() the order
        /// that Pay() just settled).  Marks the till + orderHistory paperwork dirty.</summary>
        [HarmonyPatch(typeof(Order), "Pay")]
        internal static class Patch_OrderPay
        {
            private static void Postfix(BuildingRegistration buildingRegistration)
            {
                try
                {
                    if (!MergerSync.IAmMember || buildingRegistration == null) return;
                    if (!MergerFlip.TrulyMine(buildingRegistration)
                        && !MergerAbsence.SimulatesHere(GameStateReader.AddressKey(buildingRegistration))) return;   // P3-B: a simulated shop's till is ours to publish
                    MarkDirty();
                }
                catch { }
            }
        }

        /// <summary>The game's day change.  ALSO the catch-all for the paperwork whose mutation points
        /// are not cheaply patchable (campaign start/stop lives in the marketing-agency UI, and the
        /// contract/plan creations are spread across a dozen BizMan pages) — those parts are published
        /// on the day change only, which is the cadence their own daily passes run at anyway.</summary>
        [HarmonyPatch(typeof(GameManager), "NewDay")]
        internal static class Patch_NewDay
        {
            private static void Postfix()
            {
                try { if (MergerSync.IAmMember) MarkDirty(); } catch { }
            }
        }
    }

    /// <summary>MERGER PHASE 2 WAVE 4 - DISPLAY COPIES (D18, 2026-09-11).
    ///
    /// A co-member must SEE the company's wholesale delivery contracts and HQ logistics plans for the
    /// partner shops the merger flipped onto its machine.  Phase 4b's screen-layer overlay cannot serve
    /// them: both screens read the GAME lists directly -
    ///   BizManDeliveries.cs:53  `SaveGameManager.Current.DeliveryContracts.FindAll((DeliveryContract x) =&gt; x.businessAddress == _bizManBusiness.buildingRegistration.Address)`
    ///   LogisticsManagerHelper.cs:62 `SaveGameManager.Current.logisticsManagerPlans.FindAll((LogisticsManagerPlan x) =&gt; x.headquartersAddress == headquartersAddress)` (via LogisticsManagersPlanList.cs:108 GetFilteredPlans)
    /// - so there is no model list to merge into.  The rows therefore go into the GAME lists through the
    /// ABSENCE installer, TAGGED: the tag already keeps them out of every .hsg (MPSaveCoordinator's and
    /// OfflineForkSave's strip read MergerAbsence.InstalledListItems) and it is what the two EXECUTION
    /// GUARDS in MPPatches test, so a display copy is never run by this machine.
    ///
    /// INERT without a merger: Receive refuses when this machine is not a member, and nothing is
    /// installed for an address that is not FLIPPED here.  Main thread only (every call site enqueues).</summary>
    public static class CompanyLists
    {
        /// <summary>ownerPid -> the last set this machine installed for that owner.  The registry for the
        /// TestDrive verb and for the row tint's "whose business is this?" answer.</summary>
        private static readonly Dictionary<string, CompanyListsPayload> _byOwner = new();

        /// <summary>address key -> owner pid, for the row tint (V3).  Rebuilt on every apply.</summary>
        private static readonly Dictionary<string, string> _ownerOfAddr = new(StringComparer.OrdinalIgnoreCase);

        public static int OwnerCount => _byOwner.Count;

        /// <summary>V3: whose business is this row's?  false = mine (or nothing installed) - no tint.</summary>
        public static bool TryOwnerOfAddress(string addressKey, out string pid)
        {
            pid = "";
            if (_ownerOfAddr.Count == 0 || string.IsNullOrEmpty(addressKey)) return false;
            return _ownerOfAddr.TryGetValue(addressKey, out pid!) && !string.IsNullOrEmpty(pid);
        }

        /// <summary>MAIN THREAD.  One owner's agreement lists arrived (a fan-out or a join replay).</summary>
        public static void Receive(CompanyListsPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid)) return;
                if (p.OwnerPid == MPConfig.PlayerId) return;                       // never overlay my own agreements
                if (string.Equals(p.Action, "clear", StringComparison.OrdinalIgnoreCase))
                { ClearOwner(p.OwnerPid, "the host retired that owner's lists"); return; }
                if (!MergerSync.IAmMember)
                { Plugin.Logger.LogInfo($"[CompanyLists] refused '{p.OwnerPid}': this machine is not a company member."); return; }
                Apply(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] receive: {ex.Message}"); }
        }

        private static void Apply(CompanyListsPayload p)
        {
            string tag = MergerAbsence.DisplayOwnerTag(p.OwnerPid);
            int lifted = 0;
            try { lifted = MergerAbsence.RemoveInstalledForOwner(tag); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] lift of the previous set: {ex.Message}"); }

            // The installer works one address at a time out of a paperwork bundle, so the rows are handed
            // back in exactly that shape.  Only the two families that are INSTALLED are filled; every other
            // list on the bundle stays empty, so nothing else can be installed by accident.  PHASE 4c part 1:
            // the payload also carries the four HEADQUARTERS plan families, and those are deliberately NOT
            // put on this bundle - they go to CompanyPlans' screen-layer registry below, never into a game
            // list (an installed HR/headhunter copy would run off the replicated employee and charge twice).
            var bundle = new BusinessPaperworkPayload();
            bundle.Lists.DeliveryContracts.AddRange(p.DeliveryContracts ?? new List<PwDeliveryContract>());
            bundle.Lists.LogisticsManagerPlans.AddRange(p.LogisticsManagerPlans ?? new List<PwLogisticsPlan>());

            var addrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in p.Addresses ?? new List<string>()) if (!string.IsNullOrEmpty(a)) addrs.Add(a);
            foreach (var d in bundle.Lists.DeliveryContracts) if (!string.IsNullOrEmpty(d?.BusinessAddressKey)) addrs.Add(d.BusinessAddressKey);
            foreach (var g in bundle.Lists.LogisticsManagerPlans) if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey)) addrs.Add(g.HeadquartersAddressKey);

            int installed = 0, skippedNotFlipped = 0, skippedSimulated = 0, nContracts = 0, nPlans = 0;
            foreach (var a in addrs)
            {
                // A display copy exists ONLY for a partner building the merger flipped onto this machine.
                if (!MergerFlip.IsFlipped(a)) { skippedNotFlipped++; continue; }
                // An address this machine SIMULATES already holds that owner's REAL items (the absence
                // installer put them there, where the game IS meant to run them) - a second, inert copy
                // would double every row on the screen.
                if (MergerAbsence.SimulatesHere(a)) { skippedSimulated++; continue; }
                try { installed += MergerAbsence.InstallListsForDisplay(a, bundle, addrs, tag); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] install '{a}': {ex.Message}"); }
                foreach (var d in bundle.Lists.DeliveryContracts)
                    if (string.Equals(d?.BusinessAddressKey ?? "", a, StringComparison.OrdinalIgnoreCase)) nContracts++;
                foreach (var g in bundle.Lists.LogisticsManagerPlans)
                    if (string.Equals(g?.HeadquartersAddressKey ?? "", a, StringComparison.OrdinalIgnoreCase)) nPlans++;
            }

            _byOwner[p.OwnerPid] = p;
            RebuildOwnerMap();
            // 4c part 1 - AFTER the owner map: the registry's open-tab redraw asks TryOwnerOfAddress, which reads that map;
            // before it, the FIRST feed for an HQ new to the map could not redraw an open tab (re-check r2).
            try { CompanyPlans.Receive(p); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] registry update: {ex.Message}"); }
            Plugin.Logger.LogInfo($"[CompanyLists] installed {nContracts} contracts, {nPlans} plans of '{p.OwnerPid}' (display copies; "
                                + $"{installed} item(s) in, {lifted} replaced, {skippedNotFlipped} address(es) not flipped here, {skippedSimulated} simulated here).");
            RefreshOpenScreens();
        }

        /// <summary>Retire one owner's display copies (un-flip, unmerge, that owner left, disconnect).</summary>
        public static void ClearOwner(string ownerPid, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return;
                int n = 0;
                try { n = MergerAbsence.RemoveInstalledForOwner(MergerAbsence.DisplayOwnerTag(ownerPid)); } catch { }
                bool had = _byOwner.Remove(ownerPid);
                try { CompanyPlans.ClearOwner(ownerPid, why); } catch { }   // 4c part 1: the HQ plan overlay goes with them
                RebuildOwnerMap();
                if (n > 0 || had)
                {
                    Plugin.Logger.LogInfo($"[CompanyLists] cleared {n} display copy item(s) of '{ownerPid}' - {why}.");
                    RefreshOpenScreens();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] clear '{ownerPid}': {ex.Message}"); }
        }

        /// <summary>WAVE 4 r2 (review MAJOR-2): this machine has just become the STAND-IN for `ownerPid` -
        /// lift that owner's display copies but KEEP the registry entry, so the return leg can put them back
        /// (and so the row tint still knows whose buildings these are). The two sets never coexist for one
        /// owner: the real, owner-pid-tagged items go in immediately after this.</summary>
        public static void SuspendOwner(string ownerPid, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid) || !_byOwner.ContainsKey(ownerPid)) return;
                try { CompanyPlans.SuspendOwner(ownerPid, why); } catch { }   // 4c part 1: the REAL plans are about to be installed
                int n = MergerAbsence.RemoveInstalledForOwner(MergerAbsence.DisplayOwnerTag(ownerPid));
                if (n > 0)
                {
                    Plugin.Logger.LogInfo($"[CompanyLists] lifted {n} display copy item(s) of '{ownerPid}' - {why} (the registry is kept for the return).");
                    RefreshOpenScreens();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] suspend '{ownerPid}': {ex.Message}"); }
        }

        /// <summary>WAVE 4 r2 (review MAJOR-2): the simulation for `ownerPid` has ended - put that owner's
        /// display copies back from the registry. Apply's own tests decide what is eligible (still flipped
        /// here, not simulated here), so an address that stayed simulated brings nothing back.</summary>
        public static void ReinstallOwner(string ownerPid, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid) || !MergerSync.IAmMember) return;
                if (!_byOwner.TryGetValue(ownerPid, out var p) || p == null) return;
                Plugin.Logger.LogInfo($"[CompanyLists] re-installing the display copies of '{ownerPid}' - {why}.");
                try { CompanyPlans.ReinstallOwner(ownerPid, why); } catch { }   // 4c part 1
                Apply(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] re-install '{ownerPid}': {ex.Message}"); }
        }

        public static void ClearAll(string why)
        {
            try { CompanyPlans.ClearAll(why); } catch { }   // 4c part 1
            if (_byOwner.Count == 0) { _ownerOfAddr.Clear(); return; }
            foreach (var pid in new List<string>(_byOwner.Keys)) ClearOwner(pid, why);
            _byOwner.Clear(); _ownerOfAddr.Clear();
        }

        /// <summary>MergerFlip's OFF edge: an address that stopped being a flipped company building must
        /// not keep showing its owner's agreements.  That owner's whole set goes - the next publish brings
        /// back whatever is still flipped here.</summary>
        public static void OnUnflipped(string addressKey)
        {
            if (_byOwner.Count == 0 || string.IsNullOrEmpty(addressKey)) return;
            if (!_ownerOfAddr.TryGetValue(addressKey, out var pid) || string.IsNullOrEmpty(pid)) return;
            ClearOwner(pid, $"'{addressKey}' is no longer a flipped company building");
        }

        private static void RebuildOwnerMap()
        {
            _ownerOfAddr.Clear();
            // WAVE 4 r2 (review MAJOR-4 + minor a): the registry IS the last-known truth for every partner
            // plan, so it also SEEDS the route's dedupe cache. Without the seed the very first LoadPlan of a
            // display copy routes a no-op back to its owner; with it, nothing is routed until the plan really
            // differs from what the owner last published.
            _planById.Clear();
            _lastSentPlan.Clear();
            foreach (var kv in _byOwner)
            {
                var p = kv.Value;
                if (p == null) continue;
                foreach (var d in p.DeliveryContracts ?? new List<PwDeliveryContract>())
                    if (!string.IsNullOrEmpty(d?.BusinessAddressKey)) _ownerOfAddr[d.BusinessAddressKey] = kv.Key;
                foreach (var g in p.LogisticsManagerPlans ?? new List<PwLogisticsPlan>())
                {
                    if (g == null || string.IsNullOrEmpty(g.Id)) continue;
                    _planById[g.Id] = g;
                    try { _lastSentPlan[g.Id] = Newtonsoft.Json.JsonConvert.SerializeObject(g); } catch { }
                }
                foreach (var g in p.LogisticsManagerPlans ?? new List<PwLogisticsPlan>())
                    if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey)) _ownerOfAddr[g.HeadquartersAddressKey] = kv.Key;
                foreach (var a in p.Addresses ?? new List<string>())
                    if (!string.IsNullOrEmpty(a) && !_ownerOfAddr.ContainsKey(a)) _ownerOfAddr[a] = kv.Key;
            }
        }

        /// <summary>The deliveries screen rebuilds from the game list in OnEnable (BizManDeliveries.cs:35);
        /// a set that lands while it is OPEN is shown by re-running that same public refresh.  No new UI
        /// and no timer.  The logistics list rebuilds on its own OnEnable and has no public equivalent.</summary>
        private static void RefreshOpenScreens()
        {
            try
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<UI.Smartphone.Apps.BizMan.BizManDeliveries>())
                    if (d != null && d.isActiveAndEnabled) d.RefreshData();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] deliveries refresh: {ex.Message}"); }
        }

        /// <summary>V4 lever `lists [ownerPid]`.  Read-only.</summary>
        public static string TestDriveLine(string arg)
        {
            try
            {
                int tagged = 0;
                foreach (var e in MergerAbsence.InstalledListItems)
                    if ((e.Owner ?? "").StartsWith("display:", StringComparison.Ordinal)) tagged++;
                string want = (arg ?? "").Trim();
                if (want.Length == 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in _byOwner)
                        parts.Add($"{kv.Key}: contracts {kv.Value?.DeliveryContracts?.Count ?? 0}, plans {kv.Value?.LogisticsManagerPlans?.Count ?? 0}");
                    return $"OK display copies from {_byOwner.Count} owner(s), tagged {tagged}"
                         + (parts.Count > 0 ? " | " + string.Join(" | ", parts) : "");
                }
                if (!_byOwner.TryGetValue(want, out var one) || one == null) return $"ERR no display copies held for '{want}'";
                return $"OK '{want}': contracts {one.DeliveryContracts?.Count ?? 0}, plans {one.LogisticsManagerPlans?.Count ?? 0}, "
                     + $"addresses {one.Addresses?.Count ?? 0}, tagged {tagged}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // -- MEMBER side: the routed PLAN EDIT (V2c) ---------------------------

        /// <summary>plan id -> the last shape this machine sent, so a plain re-selection of a plan (which
        /// also re-runs LoadPlan) never routes and a real change always does.</summary>
        private static readonly Dictionary<string, string> _lastSentPlan = new();

        /// <summary>WAVE 4 r2 (review MAJOR-4): plan id -> the OWNER's last published DTO. The route compares
        /// against this, so a value the native pass changed by itself (a nulled target) is not a member edit.</summary>
        private static readonly Dictionary<string, PwLogisticsPlan> _planById = new();

        /// <summary>One line per plan whose emptied target was ignored - not once per LoadPlan.</summary>
        private static readonly HashSet<string> _loggedEmptyTarget = new();

        /// <summary>One live plan as the wire DTO — the same mapping PaperworkSync.Build uses.</summary>
        public static PwLogisticsPlan PlanToDto(Buildings.Office.Headquarters.LogisticsManagerPlan pl)
        {
            var pp = new PwLogisticsPlan
            {
                Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                HeadquartersAddressKey = SafeKey(pl.headquartersAddress),
                TargetAddressKey = SafeKey(pl.targetAddress), IsFactory = pl.isFactory,
            };
            if (pl.destinations != null)
                foreach (var d in pl.destinations)
                {
                    if (d == null) continue;
                    var pdst = new PwLogisticsDestination { DeliveryTargetAddressKey = SafeKey(d.deliveryTargetAddress) };
                    if (d.stockTargets != null)
                        foreach (var t in d.stockTargets)
                            pdst.StockTargets.Add(new PwItemOrderLine { ItemName = t.itemName, Amount = t.targetAmount });
                    pp.Destinations.Add(pdst);
                }
            return pp;
        }

        private static string SafeKey(Address a) { try { return GameStateReader.AddressKey(a); } catch { return ""; } }

        /// <summary>Is this plan object one of the DISPLAY COPIES installed here?  Only those are routed -
        /// a plan of my own is edited natively and never leaves this machine.
        /// WAVE 4 r2 (review MAJOR-1): the test is the TAG STRING, not "did this machine install it". A
        /// machine standing in for an absent owner installs that owner's REAL plans through the same
        /// installer under a real pid: those must run, must be routed nowhere, and must be editable
        /// natively. Only a "display:&lt;pid&gt;" tag marks the inert copy.</summary>
        public static bool IsDisplayPlan(object plan)
        {
            if (plan == null) return false;
            try { return MergerAbsence.IsDisplayInstall(plan); } catch { return false; }
        }

        /// <summary>MEMBER, MAIN THREAD (V2c): send the WHOLE plan to whoever runs its headquarters. The
        /// member's own list is NOT written by this — the game's UI has already mutated the display copy in
        /// place, and the operator's next publish replaces it wholesale (V1). Deduped by shape, so the
        /// screen's own refresh calls cost nothing. Cross-owner is PRE-CHECKED here with the existing
        /// wording and re-checked at the host and on the operator.</summary>
        public static void RoutePlanEdit(Buildings.Office.Headquarters.LogisticsManagerPlan plan, string why)
        {
            try
            {
                if (plan == null || !IsDisplayPlan(plan)) return;
                var dto = PlanToDto(plan);
                if (string.IsNullOrEmpty(dto.HeadquartersAddressKey)) return;
                // WAVE 4 r2 (review MAJOR-4): an EMPTY target is never a member's edit. The native pass nulls
                // targetAddress itself whenever the plan's warehouse is not RentedByPlayer on THIS machine
                // (LogisticsManagerPlan.GetPlannedDeliveries, decompile :56-60), and routing that would take
                // the owner's real warehouse off their plan. The plan-load route only ever carries a target
                // the registry also knows about; clearing one is not a routed edit in wave 4.
                if (string.IsNullOrEmpty(dto.TargetAddressKey)
                    && _planById.TryGetValue(dto.Id ?? "", out var known) && !string.IsNullOrEmpty(known.TargetAddressKey))
                {
                    if (_loggedEmptyTarget.Add(dto.Id ?? ""))
                        Plugin.Logger.LogInfo($"[Merger] plan {dto.Id} not routed: its target was cleared locally (the owner's warehouse is '{known.TargetAddressKey}') - a display copy's emptied target is never an edit.");
                    return;
                }
                string shape;
                try { shape = Newtonsoft.Json.JsonConvert.SerializeObject(dto); } catch { shape = ""; }
                if (shape.Length > 0 && _lastSentPlan.TryGetValue(dto.Id ?? "", out var was) && was == shape) return;   // nothing changed

                // Every end of a routed plan must belong to the SAME COMPANY as its headquarters. PHASE 5 r2
                // (J2): a CO-MEMBER's building - my own included - is a legal end now, because since 4c part
                // 2b the leg gate hands the mixed-owner leg it makes to the ROUTED CARGO TRANSFER at delivery
                // time. This is the MEMBER's own send-path check, the third of the three (picker, here, host):
                // leaving it on "same member" would have let the picker accept an end this then refused to
                // send. An end outside the company, or one the maps cannot place, is still refused here.
                if (!TryOwnerOfAddress(dto.HeadquartersAddressKey, out var hqOwner)) return;
                bool hqIsMyCompany = MergerSync.MergedRuntime(hqOwner, MPConfig.PlayerId);
                foreach (var key in EndKeys(dto))
                {
                    if (TryOwnerOfAddress(key, out var endOwner) && !string.IsNullOrEmpty(endOwner)
                        && (endOwner == hqOwner || MergerSync.MergedRuntime(endOwner, hqOwner))) continue;
                    // Not in the partner map: my OWN building is the one legal case, and only inside the
                    // company that owns the plan.
                    var ereg = GameStatePatcher.FindRegistration(key);
                    if (hqIsMyCompany && ereg != null && ereg.RentedByPlayer && MergerFlip.TrulyMine(ereg)) continue;
                    Plugin.Logger.LogWarning($"[Merger] plan REFUSED cross-owner for '{dto.HeadquartersAddressKey}' (plan {dto.Id}): "
                                           + $"'{key}' is not run by '{hqOwner}' or a co-member of theirs — an end outside the company.");
                    return;
                }
                if (shape.Length > 0) _lastSentPlan[dto.Id ?? ""] = shape;
                SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                { PlayerId = MPConfig.PlayerId, AddressKey = dto.HeadquartersAddressKey, Op = "mergerplan", Plan = dto });
                Plugin.Logger.LogInfo($"[Merger] plan edit routed to '{hqOwner}' for '{dto.HeadquartersAddressKey}' (plan {dto.Id}) — {why}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] plan edit route: {ex.Message}"); }
        }

        /// <summary>V2c CREATION: a member pressing "add plan" on a PARTNER's headquarters. Nothing is added
        /// locally — the operator creates the plan and the next fan-out brings the display copy back.</summary>
        public static bool RoutePlanCreate(string hqAddressKey, bool isFactory)
        {
            try
            {
                if (!TryOwnerOfAddress(hqAddressKey, out var hqOwner))
                { Plugin.Logger.LogWarning($"[Merger] logistics plan at '{hqAddressKey}' refused - company building operated elsewhere and no owner known here."); return false; }
                var dto = new PwLogisticsPlan
                { Id = Guid.NewGuid().ToString("N"), HeadquartersAddressKey = hqAddressKey, IsFactory = isFactory };
                SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                { PlayerId = MPConfig.PlayerId, AddressKey = hqAddressKey, Op = "mergerplan", Plan = dto });
                Plugin.Logger.LogInfo($"[Merger] plan edit routed to '{hqOwner}' for '{hqAddressKey}' (plan {dto.Id}) — creation");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] plan create route: {ex.Message}"); return false; }
        }

        private static IEnumerable<string> EndKeys(PwLogisticsPlan dto)
        {
            if (!string.IsNullOrEmpty(dto.TargetAddressKey)) yield return dto.TargetAddressKey;
            foreach (var d in dto.Destinations ?? new List<PwLogisticsDestination>())
                if (!string.IsNullOrEmpty(d?.DeliveryTargetAddressKey)) yield return d.DeliveryTargetAddressKey;
        }

        // -- HOST side: the extraction the fan-out ships -----------------------

        /// <summary>HOST: the two agreement families out of ONE owner's stored bundle.  The bundle the
        /// host holds is already filed per owner (MPServer.HostFileSimulatedPaperwork), so no further
        /// filtering is needed - what is in it belongs to that owner.</summary>
        public static CompanyListsPayload Extract(BusinessPaperworkPayload bundle, string ownerPid)
        {
            var p = new CompanyListsPayload { PlayerId = "host", OwnerPid = ownerPid ?? "", Action = "lists" };
            var l = bundle?.Lists;
            if (l != null)
            {
                if (l.DeliveryContracts != null) p.DeliveryContracts.AddRange(l.DeliveryContracts);
                if (l.LogisticsManagerPlans != null) p.LogisticsManagerPlans.AddRange(l.LogisticsManagerPlans);
                // 4c part 1: the other four HEADQUARTERS families travel too (screen layer on the receiver).
                if (l.PricingManagerPlans != null) p.PricingManagerPlans.AddRange(l.PricingManagerPlans);
                if (l.ImportPartnerships != null) p.ImportPartnerships.AddRange(l.ImportPartnerships);
                if (l.HrManagerPlans != null) p.HrManagerPlans.AddRange(l.HrManagerPlans);
                if (l.HeadhunterPlans != null) p.HeadhunterPlans.AddRange(l.HeadhunterPlans);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bundle?.Businesses ?? new List<BusinessPaperwork>())
                if (!string.IsNullOrEmpty(b?.AddressKey) && seen.Add(b.AddressKey)) p.Addresses.Add(b.AddressKey);
            foreach (var d in p.DeliveryContracts)
                if (!string.IsNullOrEmpty(d?.BusinessAddressKey) && seen.Add(d.BusinessAddressKey)) p.Addresses.Add(d.BusinessAddressKey);
            foreach (var g in p.LogisticsManagerPlans)
                if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey) && seen.Add(g.HeadquartersAddressKey)) p.Addresses.Add(g.HeadquartersAddressKey);
            // 4c part 1: a headquarters that only has (say) a pricing plan must still map to its owner, or
            // the member's page cannot tell whose headquarters it has open.
            foreach (var g in p.PricingManagerPlans)
                if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey) && seen.Add(g.HeadquartersAddressKey)) p.Addresses.Add(g.HeadquartersAddressKey);
            foreach (var g in p.ImportPartnerships)
                if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey) && seen.Add(g.HeadquartersAddressKey)) p.Addresses.Add(g.HeadquartersAddressKey);
            foreach (var g in p.HrManagerPlans)
                if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey) && seen.Add(g.HeadquartersAddressKey)) p.Addresses.Add(g.HeadquartersAddressKey);
            foreach (var g in p.HeadhunterPlans)
                if (!string.IsNullOrEmpty(g?.HeadquartersAddressKey) && seen.Add(g.HeadquartersAddressKey)) p.Addresses.Add(g.HeadquartersAddressKey);
            return p;
        }
    }
}
