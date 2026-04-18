using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using DNA.Input;
using System.Reflection;
using DNA.CastleMinerZ.UI;
using DNA.CastleMinerZ;
using DNA.Drawing.UI;
using HarmonyLib;
using System.Linq;
using System;

using static ModLoader.LogSystem;

namespace Config
{
    internal static class GamePatches
    {
        private static Harmony _harmony;
        private static string _harmonyId;

        public static void ApplyAllPatches()
        {
            Log("[Harmony] Starting game patching.");

            _harmonyId = $"castleminerz.mods.{typeof(GamePatches).Namespace}.patches";
            _harmony = new Harmony(_harmonyId);

            Assembly asm = typeof(GamePatches).Assembly;

            int successCount = 0;
            int failCount = 0;

            foreach (var patchType in EnumeratePatchTypes(asm))
            {
                try
                {
                    var proc = _harmony.CreateClassProcessor(patchType);
                    var targets = proc?.Patch();
                    successCount++;

                    int targetCount = targets?.Count ?? 0;
                    Log($"[Harmony] Patched {patchType.FullName} ({targetCount} target(s)).");
                }
                catch (Exception ex)
                {
                    failCount++;
                    Log($"[Harmony] FAILED patching {patchType.FullName}: {ex.GetType().Name}: {ex.Message}.");
                }
            }

            var ours = _harmony.GetPatchedMethods()
                               .Where(m =>
                               {
                                   var info = Harmony.GetPatchInfo(m);
                                   return info != null && (info.Owners?.Contains(_harmonyId) ?? false);
                               })
                               .ToList();

            Log($"[Harmony] Patching complete. Success={successCount}, Failed={failCount}, MethodsPatchedByUs={ours.Count}.");
        }

        public static void DisableAll()
        {
            if (_harmony != null)
            {
                Log($"[Harmony] Unpatching all ({_harmonyId}).");
                _harmony.UnpatchAll(_harmonyId);
            }
        }

        private static IEnumerable<Type> EnumeratePatchTypes(Assembly asm)
        {
            foreach (var t in AccessTools.GetTypesFromAssembly(asm))
            {
                if (t == null || !t.IsClass) continue;

                bool hasPatchAttr = t.GetCustomAttributes(inherit: true)
                                     .Any(a => a != null &&
                                               (a.GetType().FullName == "HarmonyLib.HarmonyPatch" ||
                                                a.GetType().Name == "HarmonyPatch"));
                if (hasPatchAttr)
                    yield return t;
            }
        }

        internal static class ConfigMenuPatches
        {
            internal static class MenuItemRegistry
            {
                internal static readonly object Tag = new object();

                private static readonly Dictionary<MenuScreen, MenuItemElement> _items =
                    new Dictionary<MenuScreen, MenuItemElement>();

                public static void Remember(MenuScreen menu, MenuItemElement item)
                {
                    if (menu == null || item == null)
                        return;

                    _items[menu] = item;
                }

                public static MenuItemElement Get(MenuScreen menu)
                {
                    if (menu == null)
                        return null;

                    MenuItemElement item;
                    return _items.TryGetValue(menu, out item) ? item : null;
                }
            }

            private static class MenuSelectUtil
            {
                private static object TryGetAnyTag(object e)
                {
                    if (e == null) return null;

                    foreach (var name in new[] { "Item", "Tag", "Value", "SelectedItem", "Payload" })
                    {
                        var p = AccessTools.Property(e.GetType(), name);
                        if (p != null)
                        {
                            try { return p.GetValue(e, null); } catch { }
                        }

                        var f = AccessTools.Field(e.GetType(), name);
                        if (f != null)
                        {
                            try { return f.GetValue(e); } catch { }
                        }
                    }

                    foreach (var p in e.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (p.PropertyType != typeof(object)) continue;
                        try
                        {
                            var v = p.GetValue(e, null);
                            if (ReferenceEquals(v, MenuItemRegistry.Tag)) return v;
                        }
                        catch { }
                    }

                    foreach (var f in e.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f.FieldType != typeof(object)) continue;
                        try
                        {
                            var v = f.GetValue(e);
                            if (ReferenceEquals(v, MenuItemRegistry.Tag)) return v;
                        }
                        catch { }
                    }

                    return null;
                }

                public static bool IsOurSelection(object sender, object e)
                {
                    var tag = TryGetAnyTag(e);
                    if (ReferenceEquals(tag, MenuItemRegistry.Tag))
                        return true;

                    var menu = sender as MenuScreen;
                    if (menu == null || e == null)
                        return false;

                    var ours = MenuItemRegistry.Get(menu);
                    if (ours == null)
                        return false;

                    foreach (var p in e.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!typeof(MenuItemElement).IsAssignableFrom(p.PropertyType))
                            continue;

                        try
                        {
                            if (ReferenceEquals(p.GetValue(e, null), ours))
                                return true;
                        }
                        catch { }
                    }

                    foreach (var f in e.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!typeof(MenuItemElement).IsAssignableFrom(f.FieldType))
                            continue;

                        try
                        {
                            if (ReferenceEquals(f.GetValue(e), ours))
                                return true;
                        }
                        catch { }
                    }

                    return false;
                }
            }

            private static class MenuOrderHelper
            {
                private static IList<MenuItemElement> GetItemList(MenuScreen screen)
                {
                    if (screen == null) return null;

                    var t = screen.GetType();
                    while (t != null)
                    {
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            if (typeof(IList<MenuItemElement>).IsAssignableFrom(f.FieldType))
                            {
                                try { return (IList<MenuItemElement>)f.GetValue(screen); } catch { }
                            }
                        }

                        t = t.BaseType;
                    }

                    return null;
                }

                private static string GetStringsProperty(string name)
                {
                    try
                    {
                        var stringsType = AccessTools.TypeByName("DNA.CastleMinerZ.Globalization.Strings");
                        var prop = AccessTools.Property(stringsType, name);
                        if (prop != null)
                            return prop.GetValue(null, null) as string;
                    }
                    catch { }

                    return null;
                }

                private static string GetItemText(MenuItemElement item)
                {
                    if (item == null) return null;

                    foreach (var name in new[] { "Text", "Caption", "Title", "Label", "Content" })
                    {
                        var p = AccessTools.Property(item.GetType(), name);
                        if (p != null && p.PropertyType == typeof(string))
                        {
                            try { return (string)p.GetValue(item, null); } catch { }
                        }

                        var f = AccessTools.Field(item.GetType(), name);
                        if (f != null && f.FieldType == typeof(string))
                        {
                            try { return (string)f.GetValue(item); } catch { }
                        }
                    }

                    return null;
                }

                public static void PlaceAbove(MenuScreen menu, string anchorTextKey)
                {
                    if (menu == null) return;

                    var ours = MenuItemRegistry.Get(menu);
                    var list = GetItemList(menu);
                    if (ours == null || list == null) return;

                    int ourIdx = list.IndexOf(ours);
                    if (ourIdx < 0) return;

                    int anchorIdx = FindAnchorIndex(list, anchorTextKey);
                    if (anchorIdx < 0)
                        return;

                    if (anchorIdx == ourIdx)
                        return;

                    list.RemoveAt(ourIdx);

                    // Re-resolve the anchor after removal because the indices can shift
                    // when our injected row was originally above the anchor item.
                    anchorIdx = FindAnchorIndex(list, anchorTextKey);
                    if (anchorIdx < 0)
                        anchorIdx = list.Count;

                    if (anchorIdx > list.Count)
                        anchorIdx = list.Count;

                    list.Insert(Math.Max(0, anchorIdx), ours);
                }

                private static int FindAnchorIndex(IList<MenuItemElement> list, string anchorTextKey)
                {
                    if (list == null || list.Count == 0)
                        return -1;

                    string anchorText = GetStringsProperty(anchorTextKey);
                    if (!string.IsNullOrEmpty(anchorText))
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var text = GetItemText(list[i]);
                            if (!string.IsNullOrEmpty(text) &&
                                string.Equals(text, anchorText, StringComparison.OrdinalIgnoreCase))
                            {
                                return i;
                            }
                        }
                    }

                    return list.Count - 1;
                }
            }

            private static bool LaunchFromFrontEnd()
            {
                try
                {
                    var game = CastleMinerZGame.Instance;
                    if (game?.FrontEnd == null)
                        return false;

                    game.FrontEnd.PushScreen(new ConfigScreen(game));
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Failed to launch Config screen: {ex.Message}.");
                    return false;
                }
            }

            private static bool LaunchFromInGameMenu()
            {
                try
                {
                    var game = CastleMinerZGame.Instance;
                    var uiGroup = game?.GameScreen?._uiGroup;
                    if (game == null || uiGroup == null)
                        return false;

                    uiGroup.PushScreen(new ConfigScreen(game));
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Failed to launch Config from in-game menu: {ex.Message}.");
                    return false;
                }
            }

            private static bool HasConfigInFrontEndStack()
            {
                if (ConfigScreen.IsOpen)
                    return true;

                var frontEnd = CastleMinerZGame.Instance?.FrontEnd;
                if (frontEnd == null)
                    return false;

                return HasScreenInStack(frontEnd, typeof(ConfigScreen));
            }

            private static bool HasConfigInAnyStack()
            {
                if (HasConfigInFrontEndStack())
                    return true;

                var uiGroup = CastleMinerZGame.Instance?.GameScreen?._uiGroup;
                return HasScreenInStack(uiGroup, typeof(ConfigScreen));
            }

            private static bool IsConfigCurrentScreen()
            {
                var game = CastleMinerZGame.Instance;
                if (game == null)
                    return false;

                var frontEndCurrent = game.FrontEnd?.CurrentScreen;
                if (frontEndCurrent != null && typeof(ConfigScreen).IsAssignableFrom(frontEndCurrent.GetType()))
                    return true;

                var uiCurrent = game.GameScreen?._uiGroup?.CurrentScreen;
                if (uiCurrent != null && typeof(ConfigScreen).IsAssignableFrom(uiCurrent.GetType()))
                    return true;

                return false;
            }

            private static bool HasScreenInStack(ScreenGroup group, Type screenType)
            {
                if (group == null || screenType == null)
                    return false;

                var current = group.CurrentScreen;
                if (current != null && screenType.IsAssignableFrom(current.GetType()))
                    return true;

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var screensField = group.GetType().GetField("_screens", flags);
                var stack = screensField?.GetValue(group) as System.Collections.IEnumerable;
                if (stack == null)
                    return false;

                foreach (var item in stack)
                {
                    if (item == null)
                        continue;

                    if (screenType.IsAssignableFrom(item.GetType()))
                        return true;

                    var childGroup = item as ScreenGroup;
                    if (childGroup != null && HasScreenInStack(childGroup, screenType))
                        return true;
                }

                return false;
            }

            [HarmonyPatch(typeof(FrontEndScreen))]
            private static class Patch_MainMenu_Selection_Intercept
            {
                static IEnumerable<MethodBase> TargetMethods()
                {
                    foreach (var m in AccessTools.GetDeclaredMethods(typeof(FrontEndScreen)))
                    {
                        if (m.IsStatic) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.Name.IndexOf("MenuItemSelected", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var ps = m.GetParameters();
                        if (ps.Length != 2) continue;
                        if (ps[0].ParameterType != typeof(object)) continue;

                        var p1 = ps[1].ParameterType;
                        var n = p1?.Name ?? "";
                        if (!(n.Equals("SelectedMenuItemArgs", StringComparison.OrdinalIgnoreCase) ||
                              n.EndsWith("SelectedMenuItemArgs", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        yield return m;
                    }
                }

                static bool Prefix(object __instance, object sender, object e)
                {
                    try
                    {
                        if (MenuSelectUtil.IsOurSelection(sender, e))
                        {
                            if (!LaunchFromFrontEnd())
                                Log("Failed to launch Config from main menu.");

                            return false;
                        }
                    }
                    catch { }

                    return true;
                }
            }

            [HarmonyPatch(typeof(MainMenu), MethodType.Constructor, new[] { typeof(CastleMinerZGame) })]
            private static class Patch_MainMenu_AddItem_Ctor
            {
                static void Postfix(MainMenu __instance)
                {
                    var item = __instance.AddMenuItem("Config", MenuItemRegistry.Tag);
                    MenuItemRegistry.Remember(__instance, item);
                    MenuOrderHelper.PlaceAbove(__instance, "Options");
                }
            }

            [HarmonyPatch(typeof(MainMenu), "OnUpdate")]
            private static class Patch_MainMenu_EnsureItem_OnUpdate
            {
                static void Postfix(MainMenu __instance)
                {
                    if (MenuItemRegistry.Get(__instance) == null)
                    {
                        var item = __instance.AddMenuItem("Config", MenuItemRegistry.Tag);
                        MenuItemRegistry.Remember(__instance, item);
                    }

                    MenuOrderHelper.PlaceAbove(__instance, "Options");
                }
            }

            [HarmonyPatch(typeof(InGameMenu), MethodType.Constructor, new[] { typeof(CastleMinerZGame) })]
            private static class Patch_InGameMenu_AddItem_Ctor
            {
                static void Postfix(InGameMenu __instance)
                {
                    var item = __instance.AddMenuItem("Config", MenuItemRegistry.Tag);
                    MenuItemRegistry.Remember(__instance, item);
                    MenuOrderHelper.PlaceAbove(__instance, "Options");
                }
            }

            [HarmonyPatch(typeof(InGameMenu), "OnUpdate")]
            private static class Patch_InGameMenu_EnsureItem_OnUpdate
            {
                static void Postfix(InGameMenu __instance)
                {
                    if (MenuItemRegistry.Get(__instance) == null)
                    {
                        var item = __instance.AddMenuItem("Config", MenuItemRegistry.Tag);
                        MenuItemRegistry.Remember(__instance, item);
                    }

                    MenuOrderHelper.PlaceAbove(__instance, "Options");
                }
            }

            [HarmonyPatch(typeof(GameScreen), "_inGameMenu_MenuItemSelected", new[] { typeof(object), typeof(SelectedMenuItemArgs) })]
            private static class Patch_InGameMenu_Selection_Intercept
            {
                static bool Prefix(object sender, SelectedMenuItemArgs e)
                {
                    try
                    {
                        if (MenuSelectUtil.IsOurSelection(sender, e))
                        {
                            if (!LaunchFromInGameMenu())
                                Log("Failed to launch Config from pause menu.");

                            return false;
                        }
                    }
                    catch { }

                    return true;
                }
            }

            [HarmonyPatch(typeof(Screen), "ProcessInput", new[] { typeof(InputManager), typeof(GameTime) })]
            private static class Patch_Config_ConsumeFrontEndInput
            {
                static bool Prefix(Screen __instance, ref bool __result)
                {
                    // Match the stable pattern used by LanternLandMap/TexturePacks/MoreAchievements:
                    // do not globally block input for other screens.
                    return true;
                }
            }

            [HarmonyPatch(typeof(ScreenGroup), "get_CaptureMouse")]
            private static class Patch_Config_ReleaseMouseCapture
            {
                static void Postfix(ref bool __result)
                {
                    // Intentionally no-op to avoid fighting vanilla/menu capture flow.
                }
            }

            [HarmonyPatch(typeof(ScreenGroup), "get_ShowMouseCursor")]
            private static class Patch_Config_ShowCursor
            {
                static void Postfix(ref bool __result)
                {
                    // Intentionally no-op to avoid fighting vanilla/menu cursor flow.
                }
            }
        }
    }
}

