# CraftWatch

A diagnostic mod for Vintage Story 1.22. It watches recipe matching and the crafting grid and
logs deviations, in order to catch the "Uncraftable items" bug
([VintageStory-Issues#9175](https://github.com/anegostudios/VintageStory-Issues/issues/9175))
at the moment it starts.

It does not fix the bug. It records what happens.

## Background

Since 1.22 all grid recipes share one `ItemStack` per distinct ingredient
(`world.FastSearchRecipesByIngredient`). The attributes of such a stack normally are the static
field `CraftingRecipeIngredient.defaultEmptyAttributes`. If anything writes to that field, the
game's fast-search comparison (`resolvedItemStack.Satisfies(inputStack)`) fails for every
ingredient whose shared stack points to it, and the server no longer finds a recipe.

One writer found with this mod: the handbook renders the shared stack of a barrel recipe
ingredient without cloning it; rendering a perishable item writes its `transitionstate` into the
stack's attributes, i.e. into the static field. In singleplayer client and integrated server
share that field.

## What the mod does and does not do

The mod is **read-only**. In normal operation it calls neither `GridRecipe.Matches` nor the
getter `CraftingRecipeIngredient.ResolvedItemStack`, because both reset the attributes of the
stack they read and would remove the finding (version 0.1.0 did exactly that). The only exception
is the explicit command `/cw full`.

A Harmony prefix sits in front of the writing methods of `TreeAttribute`. It only compares the
instance and, on a write to the static field, logs the call stack. It does not change behaviour;
it costs one comparison per write.

- **Raw state of the shared stacks**, once per second on each side.
- **Fast-path probe**: the same comparison the game uses, against a fresh stack of each item.
- **Static field**: checked every frame on the client and every 100 ms on the server.
- **Crafting grid**: content, output slot, an independent reference calculation of matching
  recipes, and per item whether the fast path still accepts it.
- **Signature**: a recipe matches by reference calculation while the output slot stays empty for
  0.4 s. The client reports this to the server, which logs its own view of the same moment.
- **Context**: dialogs opened and closed (also while paused), pause, world save.

The reference calculation is an approximation: it ignores recipes that require a character trait
and item-specific rules (`MatchesForCrafting`).

## Build

Requires the .NET 10 SDK and the game installed at `%APPDATA%\Vintagestory`.

    dotnet build -c Release

The result is in `bin\craftwatch\` (`CraftWatch.dll`, `modinfo.json`). Zip both files and put the
zip into `%APPDATA%\VintagestoryData\Mods`. For a different install location pass
`-p:VSPath=...`.

## Log

`%APPDATA%\VintagestoryData\CraftWatch\craftwatch-<date>_<time>.log`, one file per game start.
The game neither overwrites nor archives it. `[S]` is the server side, `[C]` the client side,
`[-]` the patch (side-independent).

| Kind | Meaning |
|---|---|
| `BASELINE` | state at start: number of recipes and shared ingredients |
| `GRID` | crafting grid content, output slot, reference calculation, fast path per item |
| `GUI` | dialog opened or closed (client only) |
| `EVENT` | pause, world save, player joins or leaves |
| `TRACE` | write to the static field, with call stack — the culprit |
| `ATTRS` | the static field became non-empty, empty again, or was replaced by the game |
| `ALARM` | a finding: shared stacks carry attributes and the fast path no longer accepts items; an ingredient was replaced; recipe data changed; `SIGNATURE` = a recipe matches and the output slot stays empty |
| `SHARED` | change of state after an alarm |
| `DIAG` | breakdown belonging to an alarm, raw state of the ingredients |
| `MARK` | a mark set by hand with `/cw <text>` |

`/cw <text>` writes a mark and the raw state of your own grid. `/cw full` additionally runs the
game's full matching — that repairs corrupted ingredients. Chat commands do not arrive while the
game is paused.

## Source layout

| File | Purpose |
|---|---|
| `src/CraftWatchSystem.cs` | entry point, events, client → server report, `/cw` command |
| `src/RecipeWatcher.cs` | shared ingredient stacks, static field, recipe data, fast-path probe |
| `src/GridWatcher.cs` | one player's crafting grid, detection of the signature |
| `src/ReferenceMatcher.cs` | independent calculation of matching recipes, without the shared stacks |
| `src/AttrTrace.cs` | Harmony patch: call stack on a write to the static field |
| `src/Describe.cs` | text representation without side effects |
| `src/CwLog.cs` | log file |

## Status

Tested against Vintage Story 1.22.7 in singleplayer. The mod reads private state by reflection
(`deduplicationIndex`) and depends on engine internals that may change with any game update.

## License

MIT, see [LICENSE](LICENSE).
