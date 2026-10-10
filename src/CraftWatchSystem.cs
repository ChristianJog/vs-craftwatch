using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CraftWatch;

/// <summary>Report from the client to the server: a recipe matches, the output slot stays empty.</summary>
[ProtoContract]
public class BugReport
{
    [ProtoMember(1)] public int[] RecipeIndex = [];
    [ProtoMember(2)] public string[] RecipeKey = [];
    [ProtoMember(3)] public string ClientGrid = "";
}

public class CraftWatchSystem : ModSystem
{
    private const string ChannelName = "craftwatch";
    private const int TickMs = 100;
    private const int NotifyEveryMs = 15_000;

    private ICoreServerAPI? sapi;
    private RecipeWatcher? serverRecipes;
    private readonly Dictionary<string, GridWatcher> serverGrids = [];
    private long lastServerNotifyMs;
    private bool serverSuspended;

    // Context for attributing a write to the static field; shared by both sides of one process.
    private static readonly object causeGate = new();
    private static ICoreAPI? causeApi;
    private static GuiPoller? causePoller;
    private static int causeUsers;

    private ICoreClientAPI? capi;
    private RecipeWatcher? clientRecipes;
    private GridWatcher? clientGrid;
    private GuiPoller? guiPoller;
    private long lastClientNotifyMs;

    public override void Start(ICoreAPI api)
    {
        CwLog.Open(GamePaths.DataPath);
        AttrTrace.Install();
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<BugReport>();

        // One subscription for the whole process: in singleplayer this class exists once per side.
        lock (causeGate)
        {
            causeApi ??= api;
            if (causeUsers++ == 0) AttrTrace.StaticWrite += OnStaticWrite;
        }
    }

    public override void Dispose()
    {
        lock (causeGate)
        {
            if (--causeUsers == 0)
            {
                AttrTrace.StaticWrite -= OnStaticWrite;
                causeApi = null;
                causePoller = null;
            }
        }
        CwLog.Write(sapi != null ? "S" : "C", "STOP", $"mod unloading; writes to defaultEmptyAttributes in total: {AttrTrace.Hits}");
        AttrTrace.Uninstall();
        CwLog.Close();
    }

    #region Server

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        CwLog.Write("S", "START", $"CraftWatch {Mod.Info.Version}, game {GameVersion.ShortGameVersion}, log {CwLog.FilePath}");

        serverRecipes = new RecipeWatcher(api, "S", NotifyServer);

        api.Event.RegisterGameTickListener(_ => ServerTick(), TickMs);
        api.Event.PlayerNowPlaying += player =>
        {
            CwLog.Write("S", "EVENT", $"player now playing: {player.PlayerName}");
            serverGrids[player.PlayerUID] = new GridWatcher(api, "S", serverRecipes, player, OnServerSuspect);
        };
        api.Event.PlayerDisconnect += player =>
        {
            CwLog.Write("S", "EVENT", $"player left: {player.PlayerName}");
            if (serverGrids.Remove(player.PlayerUID, out GridWatcher? grid)) grid.Detach();
        };
        api.Event.GameWorldSave += () => CwLog.Write("S", "EVENT", "world save");
        api.Event.ServerSuspend += () =>
        {
            // The game asks repeatedly until everybody is ready.
            if (!serverSuspended) CwLog.Write("S", "EVENT", "server suspended");
            serverSuspended = true;
            return EnumSuspendState.Ready;
        };
        api.Event.ServerResume += () =>
        {
            serverSuspended = false;
            CwLog.Write("S", "EVENT", "server resumed");
        };

        api.Network.GetChannel(ChannelName).SetMessageHandler<BugReport>(OnBugReport);

        api.ChatCommands.Create("cw")
            .WithDescription("CraftWatch: write a mark and the current state to the log. '/cw full' additionally runs the game's own matching (which repairs corrupted ingredients).")
            .RequiresPrivilege(Privilege.chat)
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("text"))
            .HandleWith(args =>
            {
                string text = (args[0] as string ?? "").Trim();
                CwLog.Write("S", "MARK", $"{args.Caller.Player?.PlayerName}: {text}");
                if (args.Caller.Player is IServerPlayer player && serverGrids.TryGetValue(player.PlayerUID, out GridWatcher? grid))
                {
                    DumpServerGrid(grid, "MARK", grid.ReferenceMatches());
                    if (text == "full") DumpWithGameMatching(grid);
                }
                return TextCommandResult.Success($"CraftWatch: mark written to {CwLog.FilePath}");
            });
    }

    private void ServerTick()
    {
        serverRecipes?.Tick();
        foreach (GridWatcher grid in serverGrids.Values) grid.Tick();
    }

    private void NotifyServer(string message)
    {
        long now = Environment.TickCount64;
        if (sapi == null || now - lastServerNotifyMs < NotifyEveryMs) return;
        lastServerNotifyMs = now;
        sapi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, $"[CraftWatch] server: {message}", EnumChatType.Notification);
    }

    /// <summary>The reference calculation finds a recipe on the server, but its output slot stays empty.</summary>
    private void OnServerSuspect(GridWatcher grid, List<GridRecipe> matches)
    {
        string message = $"{grid.Player.PlayerName}: recipe {string.Join("; ", matches.Take(3).Select(GridWatcher.Key))} matches, the server's output slot stays empty";
        CwLog.Write("S", "ALARM", "SIGNATURE " + message);
        DumpServerGrid(grid, "SIGNATURE", matches);
        NotifyServer(message);
    }

    private void OnBugReport(IServerPlayer fromPlayer, BugReport report)
    {
        CwLog.Write("S", "ALARM", $"REPORT from client {fromPlayer.PlayerName}: client reference finds [{string.Join("; ", report.RecipeKey)}], client grid [{report.ClientGrid}]");
        if (sapi == null || !serverGrids.TryGetValue(fromPlayer.PlayerUID, out GridWatcher? grid)) return;

        // Find the client's recipes on the server: by position first, by name otherwise.
        var recipes = sapi.World.GridRecipes;
        var named = new List<GridRecipe>();
        for (int i = 0; i < report.RecipeKey.Length; i++)
        {
            int index = i < report.RecipeIndex.Length ? report.RecipeIndex[i] : -1;
            if (index >= 0 && index < recipes.Count && GridWatcher.Key(recipes[index]) == report.RecipeKey[i])
            {
                named.Add(recipes[index]);
                continue;
            }
            GridRecipe? byKey = recipes.FirstOrDefault(recipe => GridWatcher.Key(recipe) == report.RecipeKey[i]);
            if (byKey != null) named.Add(byKey);
            else CwLog.Write("S", "DIAG", $"recipe reported by the client not found on the server: {report.RecipeKey[i]}");
        }

        DumpServerGrid(grid, "REPORT", named);
    }

    private void DumpServerGrid(GridWatcher grid, string reason, List<GridRecipe> recipesToDiagnose)
    {
        if (serverRecipes == null) return;
        ItemSlot[] slots = grid.InputSlots();

        CwLog.Write("S", "DIAG", $"{reason} server grid [{Describe.Grid(slots)}] out={Describe.Stack(grid.Output)}; {serverRecipes.Summary}");
        foreach (GridRecipe recipe in recipesToDiagnose.Take(3))
        {
            CwLog.Write("S", "DIAG", $"{reason} {serverRecipes.DiagnoseRaw(recipe, slots)}");
        }
        CwLog.Write("S", "DIAG", $"{reason} inventory fields: {grid.InventoryFields()}");
    }

    /// <summary>
    /// Only on explicit command: asks the game's full matching. That reads the ingredients through
    /// the getter and resets their attributes while doing so, i.e. it removes the finding.
    /// </summary>
    private void DumpWithGameMatching(GridWatcher grid)
    {
        if (sapi == null) return;
        ItemSlot[] slots = grid.InputSlots();
        var found = new List<string>();
        foreach (GridRecipe recipe in sapi.World.GridRecipes)
        {
            try
            {
                if (recipe.Matches(grid.Player, sapi.World, slots, grid.GridWidth)) found.Add(GridWatcher.Key(recipe));
            }
            catch (Exception e)
            {
                CwLog.Write("S", "ERROR", $"Matches throws for {recipe.Name}: {e.GetType().Name}: {e.Message}");
            }
        }
        CwLog.Write("S", "DIAG", $"FULL GridRecipe.Matches finds [{string.Join("; ", found.Take(6))}] — NOTE: this call has repaired corrupted ingredients");
    }

    #endregion

    #region Client

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        CwLog.Write("C", "START", $"CraftWatch {Mod.Info.Version}, game {GameVersion.ShortGameVersion}, singleplayer={api.IsSinglePlayer}, log {CwLog.FilePath}");

        clientRecipes = new RecipeWatcher(api, "C", NotifyClient);

        api.Event.RegisterGameTickListener(_ => ClientTick(), TickMs);
        api.Event.PauseResume += paused => CwLog.Write("C", "EVENT", paused ? "pause on" : "pause off");
        api.Event.LeaveWorld += () => CwLog.Write("C", "EVENT", "leaving world");

        // Check dialogs and the static attributes every frame: that also runs while the game is paused.
        guiPoller = new GuiPoller(api, clientRecipes);
        api.Event.RegisterRenderer(guiPoller, EnumRenderStage.Ortho, "craftwatch-guipoller");
        causePoller = guiPoller;
    }

    /// <summary>
    /// Something wrote to the static field. Record the context that names the cause: the item, the
    /// handbook page that was open, and the barrel recipes that use the item as an ingredient —
    /// the handbook renders those ingredient stacks without cloning them.
    /// </summary>
    private static void OnStaticWrite(string? item, string key)
    {
        ICoreAPI? api = causeApi;
        if (api == null) return;

        string page = causePoller?.HandbookPage ?? "-";
        CwLog.Write("-", "CAUSE", $"item={item ?? "?"} key={key} handbookPage={page} dialogs=[{causePoller?.Current}]");
        if (item == null) return;

        var itemCode = new AssetLocation(item);
        foreach (BarrelRecipe recipe in api.GetBarrelRecipes())
        {
            if (recipe.Ingredients is not { } ingredients) continue;
            bool uses = ingredients.Any(ingredient => ingredient.Code != null &&
                                                      (ingredient.Code.Equals(itemCode) || WildcardUtil.Match(ingredient.Code, itemCode)));
            if (!uses) continue;

            string output = recipe.Output?.Code?.ToString() ?? "?";

            // Which page shows a recipe is decided by the handbook; a page code that names the output
            // or an ingredient is the best available hint.
            bool pageMentions = ingredients.Any(ingredient => ingredient.Code != null && page.Contains(ingredient.Code.Path))
                                || page.Contains(new AssetLocation(output).Path);
            CwLog.Write("-", "CAUSE", $"recipe kind=barrel code={recipe.Code} sealHours={recipe.SealHours} in=[{string.Join(" + ", ingredients.Select(ingredient => ingredient.Code))}] " +
                                      $"out={output} pageMentions={pageMentions}");
        }
    }

    private void ClientTick()
    {
        if (capi == null || clientRecipes == null) return;
        clientRecipes.Tick();

        if (clientGrid == null && capi.World.Player != null)
        {
            clientGrid = new GridWatcher(capi, "C", clientRecipes, capi.World.Player, OnClientSuspect);
        }
        clientGrid?.Tick();
    }

    private void NotifyClient(string message)
    {
        long now = Environment.TickCount64;
        if (capi == null || now - lastClientNotifyMs < NotifyEveryMs) return;
        lastClientNotifyMs = now;
        capi.ShowChatMessage($"[CraftWatch] client: {message}");
    }

    /// <summary>The reference calculation finds a recipe on the client, but the output slot stays empty.</summary>
    private void OnClientSuspect(GridWatcher grid, List<GridRecipe> matches)
    {
        if (capi == null || clientRecipes == null) return;

        ItemSlot[] slots = grid.InputSlots();
        string gridText = Describe.Grid(slots);
        string message = $"recipe {string.Join("; ", matches.Take(3).Select(GridWatcher.Key))} matches, output slot stays empty";
        CwLog.Write("C", "ALARM", $"SIGNATURE {message}; grid [{gridText}]; {clientRecipes.Summary}");
        foreach (GridRecipe recipe in matches.Take(3))
        {
            CwLog.Write("C", "DIAG", $"SIGNATURE {clientRecipes.DiagnoseRaw(recipe, slots)}");
        }
        CwLog.Write("C", "DIAG", $"SIGNATURE inventory fields: {grid.InventoryFields()}");
        CwLog.Write("C", "DIAG", $"SIGNATURE open dialogs: {guiPoller?.Current}");

        var recipes = capi.World.GridRecipes;
        var report = new BugReport
        {
            RecipeIndex = matches.Take(3).Select(recipe => recipes.IndexOf(recipe)).ToArray(),
            RecipeKey = matches.Take(3).Select(GridWatcher.Key).ToArray(),
            ClientGrid = gridText
        };
        IClientNetworkChannel channel = capi.Network.GetChannel(ChannelName);
        if (channel.Connected) channel.SendPacket(report);
        else CwLog.Write("C", "DIAG", "the server has not loaded CraftWatch, no server-side diagnosis");

        NotifyClient(message);
    }

    #endregion
}

/// <summary>Logs every frame which dialogs are open. Runs during rendering and therefore also while paused.</summary>
public class GuiPoller : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly RecipeWatcher recipes;
    private HashSet<string> open = [];
    private readonly Dictionary<Type, FieldInfo?> historyFields = [];

    public double RenderOrder => 1;
    public int RenderRange => 1;
    public string Current => string.Join(", ", open);

    /// <summary>Code of the handbook page currently shown, null while the handbook is closed.</summary>
    public string? HandbookPage { get; private set; }

    public GuiPoller(ICoreClientAPI capi, RecipeWatcher recipes)
    {
        this.capi = capi;
        this.recipes = recipes;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        try
        {
            recipes.CheckDefaultAttributes();

            var now = new HashSet<string>();
            string? page = null;
            foreach (GuiDialog dialog in capi.Gui.OpenedGuis.OfType<GuiDialog>())
            {
                // Permanently visible HUD elements are not an event.
                if (dialog.DialogType == EnumDialogType.HUD) continue;
                now.Add(dialog.DebugName ?? dialog.GetType().Name);
                if (dialog.GetType().Name.Contains("Handbook")) page = ReadHandbookPage(dialog);
            }

            if (page != HandbookPage)
            {
                HandbookPage = page;
                if (page != null) CwLog.Write("C", "GUI", $"handbook page: {page}");
            }

            foreach (string name in now.Where(name => !open.Contains(name)))
            {
                CwLog.Write("C", "GUI", $"open:  {name}{(capi.IsGamePaused ? " (game paused)" : "")}");
            }
            foreach (string name in open.Where(name => !now.Contains(name)))
            {
                CwLog.Write("C", "GUI", $"close: {name}");
            }
            open = now;
        }
        catch (Exception e)
        {
            CwLog.Write("C", "ERROR", $"GuiPoller: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The handbook keeps its pages in a protected browse history; the page on top is the one shown.
    /// Read by reflection so that the mod does not depend on the handbook's class layout at compile time.
    /// </summary>
    private string ReadHandbookPage(GuiDialog dialog)
    {
        Type type = dialog.GetType();
        if (!historyFields.TryGetValue(type, out FieldInfo? field))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type? t = type; t != null && field == null; t = t.BaseType) field = t.GetField("browseHistory", flags);
            historyFields[type] = field;
        }

        if (field?.GetValue(dialog) is not IEnumerable history) return "(unknown)";
        foreach (object? element in history)
        {
            // A Stack enumerates from the top.
            object? shown = element?.GetType().GetField("Page")?.GetValue(element);
            return shown?.GetType().GetProperty("PageCode")?.GetValue(shown) as string ?? "(search results)";
        }
        return "(overview)";
    }

    public void Dispose() { }
}
