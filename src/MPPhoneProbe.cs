using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// One-shot diagnostic for the BizPhone chat-app feature (press F10 in-game,
    /// ideally WITH THE PHONE OPEN, for live data).  Dumps the smartphone UI:
    /// the home grid's layout model (LayoutGroup vs fixed anchors — decides
    /// whether a 9th icon can flow in or needs a slot made for it), every app
    /// button's geometry, the full-menu view, and the SmartphoneApps appList.
    /// Logging only; zero side effects.  Prior session established: AppName
    /// enum is FULL (8/8) and the home grid LOOKS full — this probe provides
    /// the ground truth to pick between grid-injection / paging / full-menu.
    /// </summary>
    public static class MPPhoneProbe
    {
        public static void Run()
        {
            Plugin.Logger.LogInfo("[PhoneProbe] ===== BEGIN (open the phone first for live data) =====");
            try
            {
                DumpComponentInstances("SmartphoneUI",   tree: true);
                DumpComponentInstances("SmartphoneApps", tree: false);
                DumpButtons("SmartphoneAppButton");
                DumpButtons("FullMenuAppButton");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneProbe] {ex}"); }
            Plugin.Logger.LogInfo("[PhoneProbe] ===== END =====");
        }

        private static void DumpComponentInstances(string typeName, bool tree)
        {
            var t = VehicleManager.FindGameType(typeName);
            if (t == null) { Plugin.Logger.LogInfo($"[PhoneProbe] type '{typeName}' NOT FOUND"); return; }
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t), true);
            Plugin.Logger.LogInfo($"[PhoneProbe] {typeName}: {(arr == null ? 0 : arr.Length)} instance(s)");
            if (arr == null) return;

            for (int i = 0; i < arr.Length && i < 2; i++)
            {
                var comp = arr[i].TryCast<Component>();
                if (comp == null) continue;
                Plugin.Logger.LogInfo($"[PhoneProbe] --- {typeName}[{i}] at '{PathOf(comp.transform)}' activeInHierarchy={comp.gameObject.activeInHierarchy}");
                DumpProperties(comp, t);
                if (tree) DumpTree(comp.transform, 0, 3);
            }
        }

        /// <summary>Every button of the given type: geometry + the PARENT
        /// container's layout components — the decision-critical data.</summary>
        private static void DumpButtons(string typeName)
        {
            var t = VehicleManager.FindGameType(typeName);
            if (t == null) { Plugin.Logger.LogInfo($"[PhoneProbe] type '{typeName}' NOT FOUND"); return; }
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t), true);
            Plugin.Logger.LogInfo($"[PhoneProbe] {typeName}: {(arr == null ? 0 : arr.Length)} instance(s)");
            if (arr == null) return;

            Transform? lastParent = null;
            for (int i = 0; i < arr.Length && i < 24; i++)
            {
                var comp = arr[i].TryCast<Component>();
                if (comp == null) continue;
                var rt = comp.transform.TryCast<RectTransform>();
                string geo = rt != null
                    ? $"size={rt.rect.width:F0}x{rt.rect.height:F0} anchored=({rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0}) scale={rt.localScale.x:F2}"
                    : "(no RectTransform)";
                Plugin.Logger.LogInfo($"[PhoneProbe]   [{i}] '{comp.gameObject.name}' active={comp.gameObject.activeInHierarchy} {geo} parent='{comp.transform.parent?.name}'");

                // Describe each distinct parent container once.
                var parent = comp.transform.parent;
                if (parent != null && parent != lastParent)
                {
                    lastParent = parent;
                    DescribeContainer(parent);
                }
            }
        }

        private static void DescribeContainer(Transform parent)
        {
            try
            {
                var prt = parent.TryCast<RectTransform>();
                string size = prt != null ? $"{prt.rect.width:F0}x{prt.rect.height:F0}" : "?";
                var comps = parent.GetComponents(Il2CppType.Of<Component>());
                var names = new System.Text.StringBuilder();
                string layout = "NONE (fixed anchors — a 9th icon needs explicit placement)";
                for (int c = 0; c < comps.Length; c++)
                {
                    var cc = comps[c];
                    if (cc == null) continue;
                    string cn = cc.GetIl2CppType().Name;
                    names.Append(cn).Append(' ');
                    if (cn == "GridLayoutGroup")
                    {
                        var gl = cc.TryCast<GridLayoutGroup>();
                        if (gl != null)
                            layout = $"GridLayoutGroup cell={gl.cellSize.x:F0}x{gl.cellSize.y:F0} spacing={gl.spacing.x:F0},{gl.spacing.y:F0} constraint={gl.constraint}/{gl.constraintCount} — a 9th child REFLOWS AUTOMATICALLY";
                    }
                    else if (cn == "VerticalLayoutGroup" || cn == "HorizontalLayoutGroup")
                    {
                        layout = $"{cn} — children flow automatically";
                    }
                    else if (cn == "ScrollRect")
                    {
                        layout += " + ScrollRect (CAN SCROLL — overflow is fine)";
                    }
                }
                Plugin.Logger.LogInfo($"[PhoneProbe]   CONTAINER '{PathOf(parent)}' size={size} children={parent.childCount} comps=[{names.ToString().Trim()}] layout: {layout}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneProbe] DescribeContainer: {ex.Message}"); }
        }

        /// <summary>Readable property values (IL2CPP exposes serialized fields as
        /// properties — GetField returns nothing).  Primitives/strings/enums only.</summary>
        private static void DumpProperties(Component comp, Type t)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (n >= 40) break;
                    var pt = p.PropertyType;
                    try
                    {
                        if (pt.IsPrimitive || pt.IsEnum || pt == typeof(string))
                        {
                            sb.Append($"{p.Name}={p.GetValue(comp)}  ");
                            n++;
                        }
                        else if (pt.Name.StartsWith("List") || pt.Name.Contains("[]"))
                        {
                            var v = p.GetValue(comp);
                            var cnt = v?.GetType().GetProperty("Count")?.GetValue(v);
                            sb.Append($"{p.Name}=<{pt.Name} Count={cnt ?? "?"}>  ");
                            n++;
                        }
                    }
                    catch { }
                }
                if (sb.Length > 0) Plugin.Logger.LogInfo($"[PhoneProbe]   props: {sb}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneProbe] DumpProperties: {ex.Message}"); }
        }

        private static void DumpTree(Transform tr, int depth, int maxDepth)
        {
            if (tr == null || depth > maxDepth) return;
            try
            {
                var rt = tr.TryCast<RectTransform>();
                string size = rt != null ? $" [{rt.rect.width:F0}x{rt.rect.height:F0}]" : "";
                Plugin.Logger.LogInfo($"[PhoneProbe]   {new string(' ', depth * 2)}{tr.name}{size} ({(tr.gameObject.activeSelf ? "on" : "OFF")}) ch={tr.childCount}");
                for (int i = 0; i < tr.childCount && i < 16; i++)
                    DumpTree(tr.GetChild(i), depth + 1, maxDepth);
            }
            catch { }
        }

        private static string PathOf(Transform tr)
        {
            try
            {
                var sb = new System.Text.StringBuilder(tr.name);
                var p = tr.parent;
                int guard = 0;
                while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
                return sb.ToString();
            }
            catch { return tr.name; }
        }
    }
}
