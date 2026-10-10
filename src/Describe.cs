using System;
using System.Reflection;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace CraftWatch;

/// <summary>
/// Textdarstellungen fuer das Log. Alles hier liest nur. Wichtig: der Getter
/// CraftingRecipeIngredient.ResolvedItemStack schreibt beim Lesen in den gemeinsamen Stack
/// (StackSize = 1, Attributes = defaultEmptyAttributes). Wer den Rohzustand sehen will, muss
/// deshalb ueber <see cref="SharedStack"/> gehen und nicht ueber den Getter.
/// </summary>
public static class Describe
{
    private static readonly FieldInfo? dedupIndexField =
        typeof(CraftingRecipeIngredient).GetField("deduplicationIndex", BindingFlags.Instance | BindingFlags.NonPublic);

    public static int DedupIndex(CraftingRecipeIngredient ingredient)
    {
        return dedupIndexField?.GetValue(ingredient) is int index ? index : -2;
    }

    /// <summary>Der gemeinsame Stack der Zutat, ohne den schreibenden Getter zu benutzen.</summary>
    public static ItemStack? SharedStack(IWorldAccessor world, CraftingRecipeIngredient ingredient)
    {
        int index = DedupIndex(ingredient);
        var dict = world.FastSearchRecipesByIngredient;
        if (index < 0 || index >= dict.Count) return null;
        return dict.GetAt(index).Key.ResolvedItemStack;
    }

    /// <summary>Was den Stack ausmacht: Klasse, Id, Code.</summary>
    public static string Identity(ItemStack? stack)
    {
        if (stack == null) return "null";
        string code;
        try { code = stack.Collectible?.Code?.ToString() ?? "?"; }
        catch (Exception e) { code = "!" + e.GetType().Name; }
        return $"{stack.Class}:{stack.Id}:{code}";
    }

    /// <summary>Zustand, den der Abgleich braucht: Menge und Attribute.</summary>
    public static string State(ItemStack? stack)
    {
        if (stack == null) return "null";
        ITreeAttribute? attributes = stack.Attributes;
        bool isDefault = ReferenceEquals(attributes, CraftingRecipeIngredient.defaultEmptyAttributes);
        int count = attributes?.Count ?? -1;
        string text = $"size={stack.StackSize} attrs={(isDefault ? "default" : "own")}/{count}";
        if (count > 0) text += " " + Attrs(attributes);
        return text;
    }

    public static string Stack(ItemStack? stack) => stack == null ? "empty" : $"{Identity(stack)} {State(stack)}";

    public static string Attrs(IAttribute? attributes)
    {
        if (attributes == null) return "null";
        try
        {
            string json = attributes.ToJsonToken();
            return json.Length > 600 ? json.Substring(0, 600) + "…" : json;
        }
        catch (Exception e) { return "!" + e.GetType().Name; }
    }

    public static string Ingredient(CraftingRecipeIngredient? ingredient)
    {
        if (ingredient == null) return "-";
        return $"{ingredient.Type}:{ingredient.Code} q={ingredient.Quantity} match={ingredient.MatchingType} " +
               $"consume={ingredient.Consume} dur={ingredient.DurabilityChange} id={ingredient.Id} " +
               $"allowed={Join(ingredient.AllowedVariants)} skip={Join(ingredient.SkipVariants)} " +
               $"attrs={Attrs(ingredient.ResolvedAttributes)} dedup={DedupIndex(ingredient)}";
    }

    /// <summary>Alles an einem Rezept, was sich nach dem Laden nicht mehr aendern darf.</summary>
    public static string Recipe(GridRecipe recipe)
    {
        var text = new StringBuilder();
        text.Append($"{recipe.Name} {recipe.Width}x{recipe.Height} shapeless={recipe.Shapeless} out=");
        var output = recipe.Output;
        text.Append(output == null ? "null" : $"{output.Type}:{output.Code} q={output.Quantity} attrs={Attrs(output.ResolvedAttributes)}");

        var ingredients = recipe.ResolvedIngredients;
        if (ingredients == null) return text.Append(" ingredients=null").ToString();

        for (int i = 0; i < ingredients.Length; i++)
        {
            text.Append($" | [{i}] {Ingredient(ingredients[i])}");
        }
        return text.ToString();
    }

    public static string Grid(ItemSlot[] slots)
    {
        var text = new StringBuilder();
        for (int i = 0; i < slots.Length; i++)
        {
            ItemStack? stack = slots[i].Itemstack;
            if (stack == null) continue;
            if (text.Length > 0) text.Append(", ");
            text.Append($"{i}:{stack.StackSize}x{stack.Collectible?.Code}");
            int count = stack.Attributes?.Count ?? 0;
            if (count > 0) text.Append(Attrs(stack.Attributes));
        }
        return text.Length == 0 ? "leer" : text.ToString();
    }

    private static string Join(string[]? values) => values == null ? "null" : string.Join(",", values);
}
