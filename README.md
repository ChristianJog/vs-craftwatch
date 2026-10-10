# CraftWatch

A diagnostic mod for Vintage Story 1.22. It watches recipe matching and the crafting grid and
logs deviations, in order to catch the "Uncraftable items" bug
([VintageStory-Issues#9175](https://github.com/anegostudios/VintageStory-Issues/issues/9175))
at the moment it starts.

It does not fix the bug. It records what happens, and on request it can undo the damage for
the running session.

**Just want to try it, without any programming?** Go to the
[quick start](#quick-start-no-programming-needed) at the end of this page.

## Background

Since 1.22 all grid recipes share one `ItemStack` per distinct ingredient
(`world.FastSearchRecipesByIngredient`). The attributes of such a stack normally are the static
field `CraftingRecipeIngredient.defaultEmptyAttributes`. If anything writes to that field, the
game's fast-search comparison (`resolvedItemStack.Satisfies(inputStack)`) fails for every
ingredient whose shared stack points to it, and the server no longer finds a recipe. The client
keeps matching, shows the result for a moment and gets it cleared by the server.

A known writer is the handbook: it renders the shared stack of a barrel recipe ingredient without
cloning it. Rendering a perishable item writes its `transitionstate` into the stack's attributes,
i.e. into the static field. In singleplayer the client and the integrated server share that field.

## What the mod does and does not do

The mod **only observes**. It reads the raw state of the shared stacks from the fast-search
dictionary and does not go through the getter `CraftingRecipeIngredient.ResolvedItemStack`, which
writes to the shared stack while reading it. The single exception is the command `/cw repair`
(see [Repair](#repair)): it changes game state, and only when you call it.

Two Harmony patches observe without changing behaviour. They are the only use of Harmony in the mod
and live in `src/Patches/`:

- a prefix in front of the writing methods of `TreeAttribute` compares the instance and, on a
  write to the static field, logs the call stack;
- a prefix and postfix around `CollectibleObject.UpdateAndGetTransitionStatesNative` remember which
  item is having its spoilage state updated, so that such a write can be attributed to an item.

What is recorded:

- **Raw state of the shared stacks**, once per second on each side.
- **Fast-path probe**: the same comparison the game uses, against a fresh stack of each item.
- **Static field**: checked every frame on the client and every 100 ms on the server.
- **Crafting grid**: content, output slot, an independent reference calculation of matching
  recipes, and per item whether the fast path still accepts it.
- **Signature**: a recipe matches by reference calculation while the output slot stays empty for
  0.4 s. The client reports this to the server, which logs its own view of the same moment.
- **Context**: dialogs opened and closed (also while paused), the handbook page shown, pause,
  world save.
- **Cause**: on a write to the static field, the item whose spoilage state was being updated, the
  handbook page open at that moment and the barrel recipes that use the item as an ingredient.

The reference calculation is an approximation: it ignores recipes that require a character trait
and item-specific rules (`MatchesForCrafting`).

## Build

Requires the .NET 10 SDK and the game installed at `%APPDATA%\Vintagestory`.

    dotnet build -c Release

The result is in `bin\craftwatch\`. Zip `CraftWatch.dll`, `modinfo.json` and `LICENSE` and put the
zip into `%APPDATA%\VintagestoryData\Mods`. For a different install location of the game pass
`-p:VSPath=...`.

## Log

`%APPDATA%\VintagestoryData\CraftWatch\craftwatch-<date>_<time>.log`, one file per game start.
The game neither overwrites nor archives it. `[S]` is the server side, `[C]` the client side,
`[-]` a patch (side-independent).

| Kind | Meaning |
|---|---|
| `START`, `STOP` | mod loaded or unloaded; `STOP` states the total number of writes to the static field |
| `BASELINE` | state at start: number of recipes and shared ingredients |
| `GRID` | crafting grid content, output slot, reference calculation, fast path per item |
| `GUI` | dialog opened or closed, handbook page shown (client only) |
| `EVENT` | pause, world save, player joins or leaves |
| `TRACE` | write to the static field, with item and call stack — the culprit |
| `CAUSE` | context of that write: item, handbook page, barrel recipes that use the item |
| `ATTRS` | the static field became non-empty, empty again, or was replaced by the game |
| `ALARM` | a finding: shared stacks carry attributes and the fast path no longer accepts items; an ingredient was replaced; recipe data changed; `SIGNATURE` = a recipe matches and the output slot stays empty; `REPORT` = the client reported a signature |
| `SHARED` | change of state after an alarm, or a changed number of shared ingredients |
| `RECIPE` | the number of grid recipes changed |
| `DIAG` | breakdown belonging to an alarm, raw state of the ingredients |
| `REPAIR` | `/cw repair` was run: what was reset on this side |
| `MARK` | a mark set by hand with `/cw <text>` |
| `ERROR` | the mod itself failed somewhere |

## Commands

| Command | Effect |
|---|---|
| `/cw <text>` | writes a mark and the raw state of your own grid to the log |
| `/cw full` | like `/cw`, and logs what the game's full matching (`GridRecipe.Matches`) finds for the grid. It finds the recipe even while crafting is broken, and it repairs nothing |
| `/cw repair` | undoes the damage, see below |

Chat commands do not arrive while the game is paused, e.g. while the handbook is open.

## Repair

`/cw repair` gives the static field a fresh, empty attribute tree and re-attaches to it every
shared ingredient stack that the fast path accepted at start and no longer accepts. It does this
on the server and asks every client to do the same. Stacks with legitimate attributes of their
own are left alone.

- Afterwards take an item out of the crafting grid and put it back in; the game only looks for a
  recipe when the grid changes.
- The cause is untouched. The next write to the static field breaks crafting again.
- The repair is logged (`REPAIR`), and the mod then records the next write in full again, even if
  it comes from the same place. This makes it possible to find several triggers in one session:
  provoke the bug, repair, try the next suspect.

## Report

A Python script turns the log files into one HTML page that shows when the bug occurred, which
recipe failed and which handbook recipe probably caused it. See
[tools/README.md](tools/README.md).

## Source layout

| File | Purpose |
|---|---|
| `src/CraftWatchSystem.cs` | entry point, events, client → server report, cause context, `/cw` command |
| `src/RecipeWatcher.cs` | shared ingredient stacks, static field, recipe data, fast-path probe |
| `src/GridWatcher.cs` | one player's crafting grid, detection of the signature |
| `src/ReferenceMatcher.cs` | independent calculation of matching recipes, without the shared stacks |
| `src/Patches/AttrTrace.cs` | the Harmony patches: call stack on a write to the static field, and the item being updated |
| `src/Describe.cs` | text representation without side effects |
| `src/CwLog.cs` | log file |
| `tools/craftwatch_report.py` | HTML report from the log files |

## Status

Tested against Vintage Story 1.22.7 in singleplayer on Windows. The mod reads private state by
reflection (`deduplicationIndex`, the handbook's `browseHistory`) and depends on engine internals
that may change with any game update.

## License

MIT, see [LICENSE](LICENSE).

## Quick start (no programming needed)

This is for players who just want to see whether the bug happens to them. You do not need to
build anything. The steps are written for Windows.

**1. Download the mod**

Open the [latest release](https://github.com/ChristianJog/vs-craftwatch/releases/latest) and
download the file `craftwatch_<version>.zip`. Do not unpack it.

**2. Put it into your Mods folder**

Press `Win` + `R`, paste the following line and press Enter:

    %APPDATA%\VintagestoryData\Mods

A folder opens. Move the downloaded zip file into it.

**3. Start the game and play**

Start Vintage Story. In the Mod Manager, CraftWatch should be listed and switched on. Then play
your singleplayer world as usual. The mod changes nothing in the game.

**4. When crafting breaks**

You notice the bug when the correct ingredients are in the crafting grid and the result does not
appear, or only flashes for a moment. CraftWatch then prints a line starting with `[CraftWatch]`
in the chat.

If you like, open the chat, type `/cw bug` and press Enter. That puts a mark into the log. Close
the handbook first: chat commands do not work while the game is paused.

To get crafting working again, type `/cw repair` in the chat, then take one item out of the
crafting grid and put it back in. Reloading the world helps as well.

**5. Find the log**

Press `Win` + `R`, paste the following line and press Enter:

    %APPDATA%\VintagestoryData\CraftWatch

Every game start creates one file named `craftwatch-<date>_<time>.log`. The newest one belongs to
your last session. It is a plain text file; you can open it with any editor or attach it to a bug
report.

**Want to trigger the bug on purpose?**

1. Craft a flint knife (knife blade and stick) to see that crafting works.
2. Open the handbook, go to the page for curdled milk, then close the handbook.
3. Put a flint spear head and a stick into the crafting grid.

The spear does not appear, or only flashes.

**Removing the mod**

Delete the zip file from the Mods folder. The log files in the `CraftWatch` folder can be deleted
as well.
