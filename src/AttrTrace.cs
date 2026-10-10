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
/// </summary>
public static class AttrTrace
{
    private const string HarmonyId = "craftwatch.attrtrace";
    private const int MaxDistinctTraces = 20;

    private static readonly object gate = new();
    private static readonly HashSet<int> seenTraces = [];
    private static Harmony? harmony;
    private static int users;
    private static int hits;

    public static int Hits => hits;

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

    private static void BeforeWrite(TreeAttribute __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!ReferenceEquals(__instance, CraftingRecipeIngredient.defaultEmptyAttributes)) return;

        int count = Interlocked.Increment(ref hits);
        string key = __args.Length > 0 ? __args[0] as string ?? "?" : "";
        string trace = Environment.StackTrace;

        lock (gate)
        {
            if (seenTraces.Count >= MaxDistinctTraces || !seenTraces.Add(trace.GetHashCode())) return;
        }

        CwLog.Write("-", "TRACE", $"write #{count} to defaultEmptyAttributes: {__originalMethod.Name}(\"{key}\") " +
                                  $"on thread '{Thread.CurrentThread.Name}' ({Environment.CurrentManagedThreadId}){Environment.NewLine}{trace}");
    }
}
