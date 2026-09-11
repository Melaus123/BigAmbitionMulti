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
}
