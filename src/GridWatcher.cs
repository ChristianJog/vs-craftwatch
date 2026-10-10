using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CraftWatch;

/// <summary>
/// Beobachtet das Crafting-Grid eines Spielers auf einer Seite. Nach jeder Aenderung rechnet es
/// selbst nach, welche Rezepte passen, und vergleicht das mit dem Ausgabefeld. Findet die eigene
/// Rechnung ein Rezept, waehrend das Ausgabefeld leer bleibt, ist das die Signatur des Fehlers.
/// </summary>
public class GridWatcher
{
    private const int SuspectAfterMs = 400;

    private readonly string side;
    private readonly RecipeWatcher recipes;
    private readonly Action<GridWatcher, List<GridRecipe>> onSuspect;

    private InventoryBase? inventory;
    private bool pending;
    private int lastSlot = -1;
    private string lastLine = "";
    private long suspectSinceMs;
    private string suspectGrid = "";
    private string reportedGrid = "";
    private List<GridRecipe> suspectMatches = [];

    public IPlayer Player { get; }

    public GridWatcher(string side, RecipeWatcher recipes, IPlayer player, Action<GridWatcher, List<GridRecipe>> onSuspect)
    {
        this.side = side;
        this.recipes = recipes;
        this.onSuspect = onSuspect;
        Player = player;
    }

    public void Detach()
    {
        if (inventory != null) inventory.SlotModified -= OnSlotModified;
        inventory = null;
    }

    private void OnSlotModified(int slotId)
    {
        lastSlot = slotId;
        pending = true;
    }

    public ItemSlot[] InputSlots()
    {
        if (inventory == null) return [];
        var slots = new ItemSlot[inventory.Count - 1];
        for (int i = 0; i < slots.Length; i++) slots[i] = inventory[i];
        return slots;
    }

    public ItemStack? Output => inventory == null ? null : inventory[inventory.Count - 1].Itemstack;

    public int GridWidth => inventory == null ? 3 : (int)Math.Round(Math.Sqrt(inventory.Count - 1));

    /// <summary>Alle 100 ms aus dem Tick der jeweiligen Seite aufrufen.</summary>
    public void Tick()
    {
        try
        {
            if (inventory == null)
            {
                inventory = Player.InventoryManager?.GetOwnInventory(GlobalConstants.craftingInvClassName) as InventoryBase;
                if (inventory == null) return;
                inventory.SlotModified += OnSlotModified;
                CwLog.Write(side, "GRID", $"{Player.PlayerName}: Crafting-Inventar {inventory.InventoryID} ({inventory.GetType().Name}, {inventory.Count} Slots) wird beobachtet");
                pending = true;
            }

            if (pending)
            {
                pending = false;
                Evaluate();
            }

            if (suspectSinceMs != 0 && suspectGrid != reportedGrid && Environment.TickCount64 - suspectSinceMs >= SuspectAfterMs)
            {
                reportedGrid = suspectGrid;
                onSuspect(this, suspectMatches);
            }
        }
        catch (Exception e)
        {
            CwLog.Write(side, "ERROR", $"GridWatcher {Player.PlayerName}: {e}");
        }
    }

    private void Evaluate()
    {
        ItemSlot[] slots = InputSlots();
        string grid = Describe.Grid(slots);
        ItemStack? output = Output;

        long started = Environment.TickCount64;
        List<GridRecipe> matches = grid == "leer" ? [] : recipes.FindMatches(Player, slots, GridWidth);
        long tookMs = Environment.TickCount64 - started;

        string line = $"{Player.PlayerName}: in=[{grid}] out={(output == null ? "leer" : $"{output.StackSize}x{output.Collectible?.Code}")} " +
                      $"eigeneRechnung=[{string.Join("; ", matches.Take(4).Select(Key))}{(matches.Count > 4 ? $"; +{matches.Count - 4}" : "")}]";
        if (line != lastLine)
        {
            lastLine = line;
            CwLog.Write(side, "GRID", $"{line} (slot {lastSlot}, {tookMs} ms)");
        }

        bool suspect = matches.Count > 0 && output == null;
        if (!suspect)
        {
            suspectSinceMs = 0;
            suspectGrid = "";
            // Ein gefuelltes Ausgabefeld gibt denselben Grid-Inhalt wieder fuer eine neue Meldung frei.
            if (output != null) reportedGrid = "";
            return;
        }

        if (suspectGrid != grid)
        {
            suspectGrid = grid;
            suspectSinceMs = Environment.TickCount64;
        }
        suspectMatches = matches;
    }

    public static string Key(GridRecipe recipe) => $"{recipe.Name}->{recipe.Output?.Code}";

    /// <summary>Einfache Felder des Inventar-Objekts, auch private (z. B. Zustandsmerker).</summary>
    public string InventoryFields()
    {
        if (inventory == null) return "kein Inventar";
        var text = new StringBuilder();
        for (Type? type = inventory.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                Type fieldType = field.FieldType;
                if (!fieldType.IsPrimitive && !fieldType.IsEnum && fieldType != typeof(string)) continue;
                object? value;
                try { value = field.GetValue(inventory); }
                catch (Exception e) { value = "!" + e.GetType().Name; }
                text.Append($"{type.Name}.{field.Name}={value}; ");
            }
        }
        return text.ToString();
    }
}
