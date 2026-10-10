using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace CraftWatch;

/// <summary>
/// Records who writes to CraftingRecipeIngredient.defaultEmptyAttributes. That static field has
/// to stay empty; any write to it is the bug this mod is looking for.
///
/// The patch does not change behaviour: it runs before the writing methods of TreeAttribute,
/// compares the instance and, on a hit, writes the call stack to the log.
///
/// A second patch remembers which item is having its transition state updated, so that a write
/// coming from there can be attributed to that item.
/// </summary>
public static class AttrTrace
{
    private const string HarmonyId = "craftwatch.attrtrace";
    private const int MaxDistinctTraces = 20;

    private static readonly object gate = new();
    private static readonly HashSet<int> seenTraces = [];
    private static readonly HashSet<string> seenItems = [];
    private static Harmony? harmony;
    private static int users;
    private static int hits;

    public static int Hits => hits;

    /// <summary>The item whose transition state is being updated on this thread, if any.</summary>
    [ThreadStatic] private static string? currentItem;

    /// <summary>
    /// Raised once per distinct item on a write to the static field: item code (or null) and the
    /// attribute key. Called on the writing thread, from inside the patched method.
    /// </summary>
    public static event Action<string?, string>? StaticWrite;

    public static void Install()
    {
        lock (gate)
        {
            users++;
            if (harmony != null) return;

            harmony = new Harmony(HarmonyId);
            var prefix = new HarmonyMethod(typeof(AttrTrace).GetMethod(nameof(BeforeWrite), BindingFlags.Static | BindingFlags.NonPublic));
            var patched = new List<string>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in typeof(TreeAttribute).GetMethods(flags))
            {
                if (!IsWriter(method) || method.IsGenericMethodDefinition || method.IsAbstract) continue;
                try
                {
                    harmony.Patch(method, prefix: prefix);
                    patched.Add(method.Name);
                }
                catch (Exception e)
                {
                    CwLog.Write("-", "ERROR", $"AttrTrace: patching TreeAttribute.{method.Name} failed: {e.GetType().Name}: {e.Message}");
                }
            }
            CwLog.Write("-", "TRACE", $"watching writes to TreeAttribute: {string.Join(", ", patched)}");

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags own = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo? transition = typeof(CollectibleObject).GetMethod("UpdateAndGetTransitionStatesNative", any);
            try
            {
                if (transition == null) throw new MissingMethodException("CollectibleObject.UpdateAndGetTransitionStatesNative");
                harmony.Patch(transition,
                    prefix: new HarmonyMethod(typeof(AttrTrace).GetMethod(nameof(EnterTransition), own)),
                    postfix: new HarmonyMethod(typeof(AttrTrace).GetMethod(nameof(LeaveTransition), own)));
            }
            catch (Exception e)
            {
                CwLog.Write("-", "ERROR", $"AttrTrace: item attribution unavailable: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    public static void Uninstall()
    {
        lock (gate)
        {
            users--;
            if (users > 0 || harmony == null) return;
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }

    private static bool IsWriter(MethodInfo method)
    {
        string name = method.Name;
        return name == "set_Item" || name.StartsWith("Set", StringComparison.Ordinal)
               || name is "MergeTree" or "RemoveAttribute" or "GetOrAddTreeAttribute" or "Clear";
    }

    private static void EnterTransition(ItemSlot __1, out string? __state)
    {
        __state = currentItem;
        currentItem = __1?.Itemstack?.Collectible?.Code?.ToString();
    }

    private static void LeaveTransition(string? __state)
    {
        currentItem = __state;
    }

    private static void BeforeWrite(TreeAttribute __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!ReferenceEquals(__instance, CraftingRecipeIngredient.defaultEmptyAttributes)) return;

        int count = Interlocked.Increment(ref hits);
        string key = __args.Length > 0 ? __args[0] as string ?? "?" : "";
        string? item = currentItem;
        string trace = Environment.StackTrace;

        bool newTrace, newItem;
        lock (gate)
        {
            newTrace = seenTraces.Count < MaxDistinctTraces && seenTraces.Add(trace.GetHashCode());
            newItem = seenItems.Count < MaxDistinctTraces && seenItems.Add(item ?? "?");
        }

        if (newTrace)
        {
            CwLog.Write("-", "TRACE", $"write #{count} to defaultEmptyAttributes: {__originalMethod.Name}(\"{key}\") item={item ?? "?"} " +
                                      $"on thread '{Thread.CurrentThread.Name}' ({Environment.CurrentManagedThreadId}){Environment.NewLine}{trace}");
        }

        if (!newItem) return;
        try
        {
            StaticWrite?.Invoke(item, key);
        }
        catch (Exception e)
        {
            CwLog.Write("-", "ERROR", $"AttrTrace: StaticWrite handler failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
