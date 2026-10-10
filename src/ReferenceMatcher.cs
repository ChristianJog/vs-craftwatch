using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace CraftWatch;

/// <summary>
/// An independent calculation of which recipes match a grid. It compares by code, quantity and
/// the attributes of the recipe definition and never touches the shared ingredient stacks — so it
/// stays correct when those stacks are corrupted, and it changes nothing.
///
/// It is a reference, not a re-implementation: recipes that require a character trait are left
/// out, and item-specific rules (MatchesForCrafting) are not checked.
/// </summary>
public static class ReferenceMatcher
{
    public static List<GridRecipe> Find(IWorldAccessor world, ItemSlot[] slots, int gridWidth)
    {
        var found = new List<GridRecipe>();
        if (slots.All(slot => slot.Itemstack == null)) return found;

        foreach (GridRecipe recipe in world.GridRecipes)
        {
            if (recipe.RequiresTrait != null) continue;
            if (Matches(world, recipe, slots, gridWidth)) found.Add(recipe);
        }
        return found;
    }

    private static bool Matches(IWorldAccessor world, GridRecipe recipe, ItemSlot[] slots, int gridWidth)
    {
        var ingredients = recipe.ResolvedIngredients;
        if (ingredients == null) return false;

        int gridHeight = slots.Length / gridWidth;
        if (gridWidth < recipe.Width || gridHeight < recipe.Height) return false;

        if (recipe.Shapeless) return MatchesShapeless(world, ingredients, slots);

        for (int col = 0; col <= gridWidth - recipe.Width; col++)
        {
            for (int row = 0; row <= gridHeight - recipe.Height; row++)
            {
                if (MatchesAt(world, recipe, ingredients, slots, gridWidth, gridHeight, col, row)) return true;
            }
        }
        return false;
    }

    private static bool MatchesAt(IWorldAccessor world, GridRecipe recipe, CraftingRecipeIngredient?[] ingredients,
        ItemSlot[] slots, int gridWidth, int gridHeight, int colStart, int rowStart)
    {
        for (int col = 0; col < gridWidth; col++)
        {
            for (int row = 0; row < gridHeight; row++)
            {
                int recipeCol = col - colStart, recipeRow = row - rowStart;
                bool inside = recipeCol >= 0 && recipeCol < recipe.Width && recipeRow >= 0 && recipeRow < recipe.Height;
                CraftingRecipeIngredient? ingredient = inside ? ingredients[recipeRow * recipe.Width + recipeCol] : null;
                ItemStack? input = slots[row * gridWidth + col].Itemstack;

                if (input == null && ingredient == null) continue;
                if (input == null || ingredient == null) return false;
                if (!Satisfies(world, ingredient, input, input.StackSize)) return false;
            }
        }
        return true;
    }

    private static bool Satisfies(IWorldAccessor world, CraftingRecipeIngredient ingredient, ItemStack input, int available)
    {
        if (input.Collectible?.Code == null) return false;

        if (ingredient.MatchingType != EnumRecipeMatchType.Exact)
        {
            // This branch compares by code, variants and tags and reads no shared stack.
            return ingredient.SatisfiesAsIngredient(input, checkStackSize: false) && available >= ingredient.Quantity;
        }

        if (ingredient.Type != input.Class || ingredient.Code == null || !ingredient.Code.Equals(input.Collectible.Code)) return false;
        if (available < ingredient.Quantity) return false;
        if (ingredient.ResolvedAttributes is TreeAttribute required && required.Count > 0)
        {
            return required.IsSubSetOf(world, input.Attributes);
        }
        return true;
    }

    private static bool MatchesShapeless(IWorldAccessor world, CraftingRecipeIngredient?[] ingredients, ItemSlot[] slots)
    {
        // Equal items in the grid add up, as they do in the game.
        var supplied = new List<(ItemStack stack, int quantity)>();
        foreach (ItemStack input in slots.Select(slot => slot.Itemstack).OfType<ItemStack>())
        {
            int index = supplied.FindIndex(entry => entry.stack.Collectible == input.Collectible && entry.stack.Class == input.Class);
            if (index >= 0) supplied[index] = (supplied[index].stack, supplied[index].quantity + input.StackSize);
            else supplied.Add((input, input.StackSize));
        }

        foreach (CraftingRecipeIngredient wildcard in ingredients.OfType<CraftingRecipeIngredient>()
                     .Where(ingredient => ingredient.MatchingType != EnumRecipeMatchType.Exact))
        {
            int index = supplied.FindIndex(entry => Satisfies(world, wildcard, entry.stack, entry.quantity));
            if (index < 0) return false;
            supplied.RemoveAt(index);
        }

        var needed = new List<(CraftingRecipeIngredient ingredient, int quantity)>();
        foreach (CraftingRecipeIngredient exact in ingredients.OfType<CraftingRecipeIngredient>()
                     .Where(ingredient => ingredient.MatchingType == EnumRecipeMatchType.Exact))
        {
            int index = needed.FindIndex(entry => entry.ingredient.Type == exact.Type && Equals(entry.ingredient.Code, exact.Code)
                                                  && entry.ingredient.ResolvedAttributes == null && exact.ResolvedAttributes == null);
            if (index >= 0) needed[index] = (needed[index].ingredient, needed[index].quantity + exact.Quantity);
            else needed.Add((exact, exact.Quantity));
        }

        if (needed.Count != supplied.Count) return false;

        foreach ((CraftingRecipeIngredient ingredient, int quantity) in needed)
        {
            int index = supplied.FindIndex(entry => entry.quantity >= quantity && Satisfies(world, ingredient, entry.stack, entry.quantity));
            if (index < 0) return false;
            supplied.RemoveAt(index);
        }
        return true;
    }
}
