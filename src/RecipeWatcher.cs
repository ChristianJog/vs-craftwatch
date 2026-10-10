using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace CraftWatch;

/// <summary>
/// Beobachtet den Rezeptabgleich einer Seite (Client oder Server) unabhaengig vom Crafting-Grid:
/// die gemeinsamen Zutaten-Stacks, die statischen Standard-Attribute, die Rezeptdaten selbst und
/// als Gegenprobe den Abgleich bekannter Rezepte gegen frisch erzeugte Zutaten.
/// </summary>
public class RecipeWatcher
{
    private const int RecipesPerTick = 250;
    private const int CanariesPerTick = 150;
    private const int MaxLinesPerMinute = 120;

    private readonly ICoreAPI api;
    private readonly string side;
    private readonly Action<string> alarm;
    private readonly Func<IPlayer?> anyPlayer;
    private IWorldAccessor World => api.World;

    private int tick;
    private bool baselineTaken;

    // Gemeinsame Zutaten-Stacks
    private string[] sharedIdentity = [];
    private bool[] sharedDirty = [];

    // Statische Standard-Attribute
    private int attrsInstance;
    private bool attrsDirty;

    // Rezeptdaten
    private string[] recipeBaseline = [];
    private int recipeCursor;

    // Gegenprobe
    private readonly List<int> canaryRecipes = [];
    private readonly Dictionary<int, bool> canaryState = [];
    private int canaryCursor;

    private int linesThisMinute;
    private long minuteStartedMs;

    public bool CanaryEnabled { get; set; } = true;

    public bool BaselineTaken => baselineTaken;

    public RecipeWatcher(ICoreAPI api, string side, Func<IPlayer?> anyPlayer, Action<string> alarm)
    {
        this.api = api;
        this.side = side;
        this.anyPlayer = anyPlayer;
        this.alarm = alarm;
    }

    /// <summary>Alle 100 ms aus dem Tick der jeweiligen Seite aufrufen.</summary>
    public void Tick()
    {
        try
        {
            if (!baselineTaken)
            {
                if (World.GridRecipes == null || World.GridRecipes.Count == 0) return;
                TakeBaseline();
                return;
            }

            tick++;
            CheckDefaultAttributes();
            // Rohzustand zuerst ansehen: die Gegenprobe ruft den schreibenden Getter auf.
            if (tick % 10 == 0) ScanSharedStacks();
            CheckRecipeSlice();
            if (CanaryEnabled) CheckCanarySlice();
        }
        catch (Exception e)
        {
            Log("ERROR", $"Tick: {e}");
        }
    }

    private void TakeBaseline()
    {
        var dict = World.FastSearchRecipesByIngredient;
        sharedIdentity = new string[dict.Count];
        sharedDirty = new bool[dict.Count];
        int withStack = 0;
        for (int i = 0; i < dict.Count; i++)
        {
            ItemStack? stack = dict.GetAt(i).Key.ResolvedItemStack;
            sharedIdentity[i] = Describe.Identity(stack);
            // Vor dem ersten Lesen ueber den Getter traegt der Stack noch Menge und Attribute aus
            // dem Laden. Das ist kein Befund; gemeldet wird erst ein Wechsel von sauber zu unsauber.
            sharedDirty[i] = IsDirty(stack);
            if (stack != null) withStack++;
        }

        attrsInstance = RuntimeHelpers.GetHashCode(CraftingRecipeIngredient.defaultEmptyAttributes);
        attrsDirty = CraftingRecipeIngredient.defaultEmptyAttributes.Count != 0;

        var recipes = World.GridRecipes;
        recipeBaseline = new string[recipes.Count];
        for (int i = 0; i < recipes.Count; i++)
        {
            recipeBaseline[i] = Describe.Recipe(recipes[i]);
            if (IsCanaryCandidate(recipes[i])) canaryRecipes.Add(i);
        }

        baselineTaken = true;
        Log("BASELINE", $"gridRecipes={recipes.Count} sharedIngredients={dict.Count} (mit Stack {withStack}) " +
                        $"canaryKandidaten={canaryRecipes.Count} defaultAttrs=#{attrsInstance:x}/{CraftingRecipeIngredient.defaultEmptyAttributes.Count}");
    }

    private static bool IsDirty(ItemStack? stack)
    {
        if (stack == null) return false;
        return stack.StackSize != 1 || (stack.Attributes?.Count ?? 0) != 0;
    }

    public void CheckDefaultAttributes()
    {
        ITreeAttribute attributes = CraftingRecipeIngredient.defaultEmptyAttributes;
        int instance = RuntimeHelpers.GetHashCode(attributes);
        if (instance != attrsInstance)
        {
            // Das Spiel ersetzt die Instanz nur, wenn es sie befuellt vorgefunden hat.
            Log("ATTRS", $"defaultEmptyAttributes ersetzt: #{attrsInstance:x} -> #{instance:x} (das Spiel hat einen befuellten Stand verworfen)");
            attrsInstance = instance;
        }

        bool dirty = attributes.Count != 0;
        if (dirty == attrsDirty) return;
        attrsDirty = dirty;
        Log("ATTRS", dirty
            ? $"defaultEmptyAttributes BEFUELLT #{instance:x}: {Describe.Attrs(attributes)}"
            : $"defaultEmptyAttributes wieder leer #{instance:x}");
    }

    private void ScanSharedStacks()
    {
        var dict = World.FastSearchRecipesByIngredient;
        if (dict.Count != sharedIdentity.Length)
        {
            Log("SHARED", $"Anzahl gemeinsamer Zutaten geaendert: {sharedIdentity.Length} -> {dict.Count}");
            int old = sharedIdentity.Length;
            Array.Resize(ref sharedIdentity, dict.Count);
            Array.Resize(ref sharedDirty, dict.Count);
            for (int i = old; i < dict.Count; i++)
            {
                ItemStack? added = dict.GetAt(i).Key.ResolvedItemStack;
                sharedIdentity[i] = Describe.Identity(added);
                sharedDirty[i] = IsDirty(added);
            }
        }

        for (int i = 0; i < dict.Count; i++)
        {
            var entry = dict.GetAt(i);
            ItemStack? stack = entry.Key.ResolvedItemStack;

            string identity = Describe.Identity(stack);
            if (identity != sharedIdentity[i])
            {
                string message = $"gemeinsame Zutat #{i} ({entry.Key.Type}:{entry.Key.Code}) ersetzt: {sharedIdentity[i]} -> {identity} " +
                                 $"{Describe.State(stack)}; betrifft {entry.Value.Count} Rezepte";
                Log("ALARM", message);
                alarm(message);
                sharedIdentity[i] = identity;
            }

            bool dirty = IsDirty(stack);
            if (dirty == sharedDirty[i]) continue;
            sharedDirty[i] = dirty;
            Log("SHARED", dirty
                ? $"gemeinsame Zutat #{i} ({entry.Key.Code}) veraendert: {Describe.State(stack)}; {entry.Value.Count} Rezepte"
                : $"gemeinsame Zutat #{i} ({entry.Key.Code}) wieder im Normalzustand");
        }
    }

    private void CheckRecipeSlice()
    {
        var recipes = World.GridRecipes;
        if (recipes.Count != recipeBaseline.Length)
        {
            Log("RECIPE", $"Anzahl Grid-Rezepte geaendert: {recipeBaseline.Length} -> {recipes.Count}; Grundstand neu");
            recipeBaseline = recipes.Select(Describe.Recipe).ToArray();
            recipeCursor = 0;
            return;
        }

        for (int n = 0; n < RecipesPerTick && recipes.Count > 0; n++)
        {
            int i = recipeCursor;
            recipeCursor = (recipeCursor + 1) % recipes.Count;

            string now = Describe.Recipe(recipes[i]);
            if (now == recipeBaseline[i]) continue;

            string message = $"Rezept #{i} geaendert. VORHER: {recipeBaseline[i]} NACHHER: {now}";
            Log("ALARM", message);
            alarm($"Rezeptdaten geaendert: {recipes[i].Name} -> {recipes[i].Output?.Code}");
            recipeBaseline[i] = now;
        }
    }

    /// <summary>Nur Rezepte, deren Zutaten sich ohne Raten nachbauen lassen.</summary>
    private static bool IsCanaryCandidate(GridRecipe recipe)
    {
        var ingredients = recipe.ResolvedIngredients;
        if (ingredients == null || recipe.Width > 3 || recipe.Height > 3) return false;
        bool any = false;
        foreach (var ingredient in ingredients)
        {
            if (ingredient == null) continue;
            if (ingredient.MatchingType != EnumRecipeMatchType.Exact || ingredient.Code == null) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Baut die Zutaten eines Rezepts neu aus dem Code auf — bewusst nicht aus dem gemeinsamen
    /// Stack, sonst wuerde ein beschaedigter Stack gegen sich selbst geprueft.
    /// </summary>
    public ItemSlot[]? BuildInputs(GridRecipe recipe)
    {
        var ingredients = recipe.ResolvedIngredients;
        if (ingredients == null) return null;

        var slots = new ItemSlot[9];
        for (int i = 0; i < slots.Length; i++) slots[i] = new DummySlot();

        for (int row = 0; row < recipe.Height; row++)
        {
            for (int col = 0; col < recipe.Width; col++)
            {
                var ingredient = ingredients[row * recipe.Width + col];
                if (ingredient == null) continue;

                CollectibleObject? collectible = ingredient.Type == EnumItemClass.Block
                    ? World.GetBlock(ingredient.Code)
                    : World.GetItem(ingredient.Code);
                if (collectible == null) return null;

                var stack = new ItemStack(collectible, Math.Max(1, ingredient.Quantity));
                if (ingredient.ResolvedAttributes != null) stack.Attributes = ingredient.ResolvedAttributes.Clone();
                slots[row * 3 + col].Itemstack = stack;
            }
        }
        return slots;
    }

    private void CheckCanarySlice()
    {
        if (canaryRecipes.Count == 0) return;
        IPlayer? player = anyPlayer();
        if (player?.Entity == null) return;

        var recipes = World.GridRecipes;
        for (int n = 0; n < CanariesPerTick; n++)
        {
            int index = canaryRecipes[canaryCursor];
            canaryCursor = (canaryCursor + 1) % canaryRecipes.Count;
            if (index >= recipes.Count) continue;

            GridRecipe recipe = recipes[index];
            ItemSlot[]? inputs = BuildInputs(recipe);
            if (inputs == null) continue;

            // Rohzustand festhalten, bevor Matches() ihn ueber den Getter ueberschreibt.
            string rawBefore = canaryState.TryGetValue(index, out bool known) && known ? RawShared(recipe) : "";

            bool matches = recipe.Matches(player, World, inputs, 3);
            if (!canaryState.TryGetValue(index, out bool before))
            {
                canaryState[index] = matches;
                continue;
            }
            if (matches == before) continue;
            canaryState[index] = matches;

            if (matches)
            {
                Log("CANARY", $"Rezept #{index} {recipe.Name} -> {recipe.Output?.Code} passt wieder");
                continue;
            }

            string message = $"Rezept #{index} {recipe.Name} -> {recipe.Output?.Code} passt NICHT MEHR auf frisch erzeugte Zutaten";
            Log("ALARM", $"CANARY {message}. Rohzustand vorher: {rawBefore}");
            Log("DIAG", Diagnose(recipe, inputs, 3, player));
            alarm(message);
        }
    }

    private string RawShared(GridRecipe recipe)
    {
        var ingredients = recipe.ResolvedIngredients;
        if (ingredients == null) return "null";
        var text = new StringBuilder();
        for (int i = 0; i < ingredients.Length; i++)
        {
            if (ingredients[i] is not { } ingredient) continue;
            text.Append($"[{i}] {Describe.Stack(Describe.SharedStack(World, ingredient))}; ");
        }
        return text.ToString();
    }

    /// <summary>Zerlegt den Abgleich eines Rezepts gegen ein Grid in seine Einzelpruefungen.</summary>
    public string Diagnose(GridRecipe recipe, ItemSlot[] slots, int gridWidth, IPlayer player)
    {
        var text = new StringBuilder();
        // Reihenfolge ist wichtig: erst Rohzustand, dann die Aufrufe, die ihn veraendern.
        text.Append($"REZEPT {Describe.Recipe(recipe)} || ROH {RawShared(recipe)}");
        text.Append($"|| GRID {Describe.Grid(slots)}");

        var ingredients = recipe.ResolvedIngredients;
        if (ingredients != null)
        {
            for (int s = 0; s < slots.Length; s++)
            {
                ItemStack? input = slots[s].Itemstack;
                if (input == null) continue;
                for (int i = 0; i < ingredients.Length; i++)
                {
                    if (ingredients[i] is not { } ingredient) continue;
                    string satisfies, forCrafting;
                    try { satisfies = ingredient.SatisfiesAsIngredient(input).ToString(); }
                    catch (Exception e) { satisfies = "!" + e.GetType().Name; }
                    try { forCrafting = input.Collectible.MatchesForCrafting(input, recipe, ingredient).ToString(); }
                    catch (Exception e) { forCrafting = "!" + e.GetType().Name; }
                    text.Append($" || slot{s}~zutat{i}: satisfies={satisfies} matchesForCrafting={forCrafting}");
                }
            }
        }

        string matches;
        try { matches = recipe.Matches(player, World, slots, gridWidth).ToString(); }
        catch (Exception e) { matches = "!" + e; }
        text.Append($" || Matches={matches}");
        return text.ToString();
    }

    /// <summary>Alle Rezepte, die nach eigener Rechnung auf das Grid passen.</summary>
    public List<GridRecipe> FindMatches(IPlayer player, ItemSlot[] slots, int gridWidth)
    {
        var found = new List<GridRecipe>();
        foreach (GridRecipe recipe in World.GridRecipes)
        {
            try
            {
                if (recipe.Matches(player, World, slots, gridWidth)) found.Add(recipe);
            }
            catch (Exception e)
            {
                Log("ERROR", $"Matches wirft bei {recipe.Name}: {e.GetType().Name}: {e.Message}");
            }
        }
        return found;
    }

    public void Log(string kind, string message)
    {
        long now = Environment.TickCount64;
        if (now - minuteStartedMs > 60_000)
        {
            if (linesThisMinute > MaxLinesPerMinute)
            {
                CwLog.Write(side, "LIMIT", $"{linesThisMinute - MaxLinesPerMinute} Zeilen in der letzten Minute unterdrueckt");
            }
            minuteStartedMs = now;
            linesThisMinute = 0;
        }

        linesThisMinute++;
        if (linesThisMinute > MaxLinesPerMinute && kind != "ALARM" && kind != "DIAG") return;
        CwLog.Write(side, kind, message);
    }
}
