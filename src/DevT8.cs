#if BAMP_DEV
using System;
using System.Collections.Generic;
using System.IO;
using Buildings;                                 // BuildingRegistration
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>T-8 (evidence only) DEV lever, dev build only; it drives tools/scenarios/t-logoleave.json.
    /// `logoregen [count]` hands the game's own logo maker (BusinessLogoGenerator.Create) `count` renders of the
    /// businesses the local player rents (round-robin; any business with logo settings if it rents none), so a
    /// render is still in flight when the next rig step leaves to the menu. Each request's callback reads the
    /// business's logo folder the way a sign's callback does (LogoHelper.GetPlayerBusinessLogoPath reads
    /// SaveGameManager.Current), so a step that ran after the save was cleared faults exactly as the field logs did;
    /// a routine STARTED from the queue after GlobalReferences is destroyed faults in SetUpSettings -> GetFontByName.
    /// Nothing in the save is written: the images go to a scratch folder (emptied first while the generator is idle); the game's in-memory logo
    /// cache gets the same picture it already had. Nothing here changes mod behaviour.</summary>
    internal static class DevT8
    {
        internal static string LogoRegen(string arg)
        {
            try
            {
                int count = 120;
                string a = (arg ?? "").Trim();
                if (a.Length > 0 && (!int.TryParse(a, out count) || count < 1 || count > 1000))
                    return "ERR usage: logoregen [count 1-1000]";
                var gi = SaveGameManager.Current;
                if (gi == null) return "ERR logoregen: no game loaded";
                var gen = BusinessLogoGenerator.Instance;
                if (gen == null) return "ERR logoregen: no logo generator in this scene";
                var owned = new List<BuildingRegistration>();
                var any = new List<BuildingRegistration>();
                if (gi.BuildingRegistrations != null)
                    foreach (var r in gi.BuildingRegistrations)
                    {
                        if (r == null || r.logoSettings == null || string.IsNullOrEmpty(r.BusinessName)) continue;
                        any.Add(r);
                        if (r.RentedByPlayer) owned.Add(r);
                    }
                var regs = owned.Count > 0 ? owned : any;
                if (regs.Count == 0) return "ERR logoregen: no business with logo settings in this save";
                string root = Path.Combine(Application.temporaryCachePath, "bamp-t8-logos");
                // Emptied only while the generator is idle: an earlier call's renders still running would lose their
                // file write (and could leave the busy flag stuck).
                if (!gen._logoIsGenerating)
                {
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                    catch (Exception ex) { return $"ERR logoregen: the scratch folder could not be emptied: {ex.Message}"; }
                }
                for (int i = 0; i < count; i++)
                {
                    var r = regs[i % regs.Count];
                    string name = r.BusinessName;
                    BusinessLogoGenerator.Create(name, r.logoSettings, Path.Combine(root, i.ToString()), r.RentedByPlayer, delegate
                    {
                        LogoHelper.GetPlayerBusinessLogoPath(name);   // the sign callback's read of the save
                    });
                }
                bool busy = gen._logoIsGenerating;
                int queued = gen.LogoGenerationQueue.Count;
                Plugin.Logger.LogInfo($"[TestDrive] logoregen: {count} logo render(s) handed to the game's logo maker for {regs.Count} business(es) (owned={owned.Count > 0}); generating={busy} queued={queued}.");
                return $"OK logoregen n={count} businesses={regs.Count} owned={owned.Count > 0} generating={busy} queued={queued}";
            }
            catch (Exception ex) { return $"ERR logoregen: {ex.GetType().Name}: {ex.Message}"; }
        }
    }
}
#endif
