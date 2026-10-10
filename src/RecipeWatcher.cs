using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace CraftWatch;

/// <summary>
/// Watches recipe matching on one side (client or server) independently of the crafting grid:
/// the shared ingredient stacks, the static default attributes and the recipe data.
///
/// This class is strictly read-only. It calls neither GridRecipe.Matches nor the getter
/// CraftingRecipeIngredient.ResolvedItemStack: both reset the attributes of the stack they read
/// and would remove the very corruption this class is meant to observe.
/// </summary>
public class RecipeWatcher
{
    private const int RecipesPerTick = 250;
    private const int MaxExamples = 8;

    private readonly ICoreAPI api;
    private readonly string side;
    private readonly Action<string> alarm;
    private IWorldAccessor World => api.World;

    private int tick;

    // Shared ingredient stacks
    private string[] sharedIdentity = [];
    private bool[] fastPathOkAtStart = [];
    private readonly Dictionary<string, List<int>> sharedByCode = [];
    private string sharedSummary = "0/0/0";
    private bool sharedWasClean = true;

    // Static default attributes
    private int attrsInstance;
    private bool attrsDirty;

    // Recipe data
    private string[] recipeBaseline = [];
    private int recipeCursor;

    public bool BaselineTaken { get; private set; }

    public RecipeWatcher(ICoreAPI api, string side, Action<string> alarm)
    {
        this.api = api;
        this.side = side;
        this.alarm = alarm;
    }

    /// <summary>Call every 100 ms from the tick of the respective side.</summary>
    public void Tick()
    {
        try
        {
            if (!BaselineTaken)
            {
                if (World.GridRecipes == null || World.GridRecipes.Count == 0) return;
                TakeBaseline();
                return;
            }

            tick++;
            CheckDefaultAttributes();
            if (tick % 10 == 0) ScanSharedStacks();
            CheckRecipeSlice();
        }
        catch (Exception e)
        {
            CwLog.Write(side, "ERROR", $"Tick: {e}");
        }
    }

    private void TakeBaseline()
    {
        var dict = World.FastSearchRecipesByIngredient;
        sharedIdentity = new string[dict.Count];
        fastPathOkAtStart = new bool[dict.Count];
        int withStack = 0, okAtStart = 0;
        for (int i = 0; i < dict.Count; i++)
        {
            var key = dict.GetAt(i).Key;
            ItemStack? stack = key.ResolvedItemStack;
            sharedIdentity[i] = Describe.Identity(stack);
            fastPathOkAtStart[i] = FastPathAcceptsOwnItem(key);
            if (stack == null) continue;

            withStack++;
            if (fastPathOkAtStart[i]) okAtStart++;
            string? code = stack.Collectible?.Code?.ToString();
            if (code == null) continue;
            if (!sharedByCode.TryGetValue(code, out List<int>? indices)) sharedByCode[code] = indices = [];
            indices.Add(i);
        }

        attrsInstance = RuntimeHelpers.GetHashCode(CraftingRecipeIngredient.defaultEmptyAttributes);
        attrsDirty = CraftingRecipeIngredient.defaultEmptyAttributes.Count != 0;

        recipeBaseline = World.GridRecipes.Select(Describe.Recipe).ToArray();

        BaselineTaken = true;
        CwLog.Write(side, "BASELINE", $"gridRecipes={recipeBaseline.Length} sharedIngredients={dict.Count} (with stack {withStack}, " +
                                      $"fast path accepts own item for {okAtStart}, key type {(dict.Count > 0 ? dict.GetAt(0).Key.GetType().Name : "?")}) " +
                                      $"defaultAttrs=#{attrsInstance:x}/{CraftingRecipeIngredient.defaultEmptyAttributes.Count}");
    }

    /// <summary>
    /// The comparison the game's fast path uses: the shared stack in its raw state against a
    /// fresh stack of the same item. If this returns false, the game no longer finds any recipe
    /// that uses this ingredient.
    /// </summary>
    private static bool FastPathAcceptsOwnItem(IRecipeIngredientBase key)
    {
        ItemStack? stack = key.ResolvedItemStack;
        if (stack?.Collectible == null || key.MatchingType != EnumRecipeMatchType.Exact) return true;
        try
        {
            return key.SatisfiesAsIngredient(new ItemStack(stack.Collectible, 1));
        }
        catch
        {
            return false;
        }
    }

    public void CheckDefaultAttributes()
    {
        if (!BaselineTaken) return;

        ITreeAttribute attributes = CraftingRecipeIngredient.defaultEmptyAttributes;
        int instance = RuntimeHelpers.GetHashCode(attributes);
        if (instance != attrsInstance)
        {
            // The game only replaces the instance when it found it non-empty.
            CwLog.Write(side, "ATTRS", $"defaultEmptyAttributes replaced: #{attrsInstance:x} -> #{instance:x} (the game discarded a non-empty instance)");
            attrsInstance = instance;
        }

        bool dirty = attributes.Count != 0;
        if (dirty == attrsDirty) return;
        attrsDirty = dirty;
        CwLog.Write(side, "ATTRS", dirty
            ? $"defaultEmptyAttributes NOT EMPTY #{instance:x}: {Describe.Attrs(attributes)}"
            : $"defaultEmptyAttributes empty again #{instance:x}");
    }

    private void ScanSharedStacks()
    {
        var dict = World.FastSearchRecipesByIngredient;
        if (dict.Count != sharedIdentity.Length)
        {
            CwLog.Write(side, "SHARED", $"number of shared ingredients changed: {sharedIdentity.Length} -> {dict.Count}; taking a new baseline");
            sharedByCode.Clear();
            BaselineTaken = false;
            return;
        }

        ITreeAttribute current = CraftingRecipeIngredient.defaultEmptyAttributes;
        int withAttributes = 0, onOtherTree = 0, fastPathBroken = 0, affectedRecipes = 0;
        var examples = new List<string>();
        string attributeSample = "";

        for (int i = 0; i < dict.Count; i++)
        {
            var entry = dict.GetAt(i);
            ItemStack? stack = entry.Key.ResolvedItemStack;

            string identity = Describe.Identity(stack);
            if (identity != sharedIdentity[i])
            {
                string message = $"shared ingredient #{i} ({entry.Key.Type}:{entry.Key.Code}) replaced: {sharedIdentity[i]} -> {identity}; used by {entry.Value.Count} recipes";
                CwLog.Write(side, "ALARM", message);
                alarm(message);
                sharedIdentity[i] = identity;
            }

            if (stack == null) continue;

            // Attributes of its own with which the fast path still accepts the item are legitimate
            // (on the client e.g. paper of type papyrus) and not a finding.
            bool broken = fastPathOkAtStart[i] && !FastPathAcceptsOwnItem(entry.Key);
            bool onStatic = ReferenceEquals(stack.Attributes, current);
            bool hasAttributes = (stack.Attributes?.Count ?? 0) != 0 && (broken || onStatic);
            if (hasAttributes)
            {
                withAttributes++;
                if (!onStatic) onOtherTree++;
                if (attributeSample == "") attributeSample = Describe.Attrs(stack.Attributes);
            }

            if (broken)
            {
                fastPathBroken++;
                affectedRecipes += entry.Value.Count;
                if (examples.Count < MaxExamples) examples.Add(entry.Key.Code?.ToString() ?? "?");
            }
        }

        string summary = $"{withAttributes}/{onOtherTree}/{fastPathBroken}";
        if (summary == sharedSummary) return;
        sharedSummary = summary;

        bool clean = withAttributes == 0 && fastPathBroken == 0;
        if (clean)
        {
            CwLog.Write(side, "SHARED", "all shared ingredient stacks are without attributes again, fast path ok");
            sharedWasClean = true;
            return;
        }

        string text = $"shared ingredient stacks: {withAttributes} carry attributes ({onOtherTree} of them on an attribute tree that is not the current static one), " +
                      $"fast path no longer accepts the own item for {fastPathBroken} (recipe references: {affectedRecipes}). " +
                      $"Examples: {string.Join(", ", examples)}. Attributes: {attributeSample}";
        CwLog.Write(side, sharedWasClean ? "ALARM" : "SHARED", text);
        if (sharedWasClean) alarm($"fast path broken for {fastPathBroken} ingredients, {withAttributes} stacks carry attributes");
        sharedWasClean = false;
    }

    private void CheckRecipeSlice()
    {
        var recipes = World.GridRecipes;
        if (recipes.Count != recipeBaseline.Length)
        {
            CwLog.Write(side, "RECIPE", $"number of grid recipes changed: {recipeBaseline.Length} -> {recipes.Count}; taking a new baseline");
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

            CwLog.Write(side, "ALARM", $"recipe #{i} changed. BEFORE: {recipeBaseline[i]} AFTER: {now}");
            alarm($"recipe data changed: {recipes[i].Name} -> {recipes[i].Output?.Code}");
            recipeBaseline[i] = now;
        }
    }

    /// <summary>
    /// Checks a stack from the grid against the shared ingredients with the same code, the way
    /// the fast path does. Null if there is no exact ingredient for this item.
    /// </summary>
    public bool? FastPathAccepts(ItemStack input)
    {
        string? code = input.Collectible?.Code?.ToString();
        if (code == null || !sharedByCode.TryGetValue(code, out List<int>? indices)) return null;

        var dict = World.FastSearchRecipesByIngredient;
        bool any = false;
        foreach (int i in indices)
        {
            if (i >= dict.Count) continue;
            var key = dict.GetAt(i).Key;
            if (key.MatchingType != EnumRecipeMatchType.Exact) continue;
            any = true;
            try
            {
                if (!key.SatisfiesAsIngredient(input)) return false;
            }
            catch
            {
                return false;
            }
        }
        return any ? true : null;
    }

    /// <summary>Raw state of a recipe's ingredients and their behaviour in the fast path, without side effects.</summary>
    public string DiagnoseRaw(GridRecipe recipe, ItemSlot[] slots)
    {
        var text = new StringBuilder();
        text.Append($"RECIPE {Describe.Recipe(recipe)} || GRID {Describe.Grid(slots)}");

        var ingredients = recipe.ResolvedIngredients;
        if (ingredients == null) return text.ToString();

        var dict = World.FastSearchRecipesByIngredient;
        for (int i = 0; i < ingredients.Length; i++)
        {
            if (ingredients[i] is not { } ingredient) continue;
            int index = Describe.DedupIndex(ingredient);
            ItemStack? shared = Describe.SharedStack(World, ingredient);
            text.Append($" || ingredient{i} {ingredient.Code}: RAW {Describe.Stack(shared)}");
            if (index < 0 || index >= dict.Count) continue;

            var key = dict.GetAt(index).Key;
            text.Append($" acceptsOwnItem={FastPathAcceptsOwnItem(key)}");
            foreach (ItemSlot slot in slots)
            {
                ItemStack? input = slot.Itemstack;
                if (input?.Collectible?.Code == null || !input.Collectible.Code.Equals(ingredient.Code)) continue;
                string accepts;
                try { accepts = key.SatisfiesAsIngredient(input).ToString(); }
                catch (Exception e) { accepts = "!" + e.GetType().Name; }
                text.Append($" fastPathVsGrid={accepts}");
                break;
            }
        }
        return text.ToString();
    }

    public string Summary => $"stacks withAttributes/onOtherTree/fastPathBroken={sharedSummary}, " +
                             $"defaultAttrs=#{attrsInstance:x}/{CraftingRecipeIngredient.defaultEmptyAttributes.Count}";
}
