using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace CraftWatch;

/// <summary>Meldung des Clients an den Server: Rezept gefunden, Ausgabefeld bleibt leer.</summary>
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

    private ICoreClientAPI? capi;
    private RecipeWatcher? clientRecipes;
    private GridWatcher? clientGrid;
    private GuiPoller? guiPoller;
    private long lastClientNotifyMs;

    public override void Start(ICoreAPI api)
    {
        CwLog.Open(GamePaths.DataPath);
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<BugReport>();
    }

    public override void Dispose()
    {
        CwLog.Write(sapi != null ? "S" : "C", "STOP", "Mod wird entladen");
        CwLog.Close();
    }

    #region Server

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        CwLog.Write("S", "START", $"CraftWatch {Mod.Info.Version}, Spiel {GameVersion.ShortGameVersion}, Log {CwLog.FilePath}");

        serverRecipes = new RecipeWatcher(api, "S", () => api.World.AllOnlinePlayers.FirstOrDefault(), NotifyServer);

        api.Event.RegisterGameTickListener(_ => ServerTick(), TickMs);
        api.Event.PlayerNowPlaying += player =>
        {
            CwLog.Write("S", "EVENT", $"Spieler spielt: {player.PlayerName}");
            serverGrids[player.PlayerUID] = new GridWatcher("S", serverRecipes, player, OnServerSuspect);
        };
        api.Event.PlayerDisconnect += player =>
        {
            CwLog.Write("S", "EVENT", $"Spieler weg: {player.PlayerName}");
            if (serverGrids.Remove(player.PlayerUID, out GridWatcher? grid)) grid.Detach();
        };
        api.Event.GameWorldSave += () => CwLog.Write("S", "EVENT", "Speichern der Welt");
        api.Event.ServerSuspend += () =>
        {
            CwLog.Write("S", "EVENT", "Server angehalten");
            return EnumSuspendState.Ready;
        };
        api.Event.ServerResume += () => CwLog.Write("S", "EVENT", "Server laeuft weiter");

        api.Network.GetChannel(ChannelName).SetMessageHandler<BugReport>(OnBugReport);

        api.ChatCommands.Create("cw")
            .WithDescription("CraftWatch: Markierung und vollstaendigen Zustand ins Log schreiben")
            .RequiresPrivilege(Privilege.chat)
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("text"))
            .HandleWith(args =>
            {
                string text = args[0] as string ?? "";
                CwLog.Write("S", "MARK", $"{args.Caller.Player?.PlayerName}: {text}");
                if (args.Caller.Player is IServerPlayer player && serverGrids.TryGetValue(player.PlayerUID, out GridWatcher? grid))
                {
                    DumpServerGrid(grid, "MARK");
                }
                return TextCommandResult.Success($"CraftWatch: Markierung geschrieben nach {CwLog.FilePath}");
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
        sapi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, $"[CraftWatch] Server: {message}", EnumChatType.Notification);
    }

    /// <summary>Der Server rechnet selbst ein Rezept aus, sein Ausgabefeld bleibt aber leer.</summary>
    private void OnServerSuspect(GridWatcher grid, List<GridRecipe> matches)
    {
        string message = $"{grid.Player.PlayerName}: eigene Rechnung findet {string.Join("; ", matches.Take(3).Select(GridWatcher.Key))}, Ausgabefeld bleibt leer";
        CwLog.Write("S", "ALARM", "MISMATCH " + message);
        DumpServerGrid(grid, "MISMATCH", matches);
        NotifyServer(message);
    }

    private void OnBugReport(IServerPlayer fromPlayer, BugReport report)
    {
        CwLog.Write("S", "ALARM", $"MELDUNG vom Client {fromPlayer.PlayerName}: Client findet [{string.Join("; ", report.RecipeKey)}], Client-Grid [{report.ClientGrid}]");
        if (sapi == null || serverRecipes == null) return;
        if (!serverGrids.TryGetValue(fromPlayer.PlayerUID, out GridWatcher? grid)) return;

        // Die Rezepte des Clients auf dem Server wiederfinden: erst ueber die Position, sonst ueber den Namen.
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
            else CwLog.Write("S", "DIAG", $"Rezept des Clients auf dem Server nicht gefunden: {report.RecipeKey[i]}");
        }

        DumpServerGrid(grid, "MELDUNG", named);
        NotifyServer($"Client meldet leeres Ausgabefeld bei {string.Join("; ", report.RecipeKey.Take(2))}");
    }

    private void DumpServerGrid(GridWatcher grid, string reason, List<GridRecipe>? recipesToDiagnose = null)
    {
        if (serverRecipes == null) return;
        ItemSlot[] slots = grid.InputSlots();

        // Zuerst die Zerlegung: sie haelt den Rohzustand fest, bevor weitere Aufrufe ihn ueberschreiben.
        foreach (GridRecipe recipe in (recipesToDiagnose ?? []).Take(3))
        {
            CwLog.Write("S", "DIAG", $"{reason} {serverRecipes.Diagnose(recipe, slots, grid.GridWidth, grid.Player)}");
        }

        List<GridRecipe> own = serverRecipes.FindMatches(grid.Player, slots, grid.GridWidth);
        CwLog.Write("S", "DIAG", $"{reason} Server-Grid [{Describe.Grid(slots)}] out={Describe.Stack(grid.Output)} " +
                                 $"eigeneRechnung=[{string.Join("; ", own.Take(4).Select(GridWatcher.Key))}]");
        CwLog.Write("S", "DIAG", $"{reason} Inventar-Felder: {grid.InventoryFields()}");
    }

    #endregion

    #region Client

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        CwLog.Write("C", "START", $"CraftWatch {Mod.Info.Version}, Spiel {GameVersion.ShortGameVersion}, Einzelspieler={api.IsSinglePlayer}, Log {CwLog.FilePath}");

        clientRecipes = new RecipeWatcher(api, "C", () => api.World.Player, NotifyClient);

        api.Event.RegisterGameTickListener(_ => ClientTick(), TickMs);
        api.Event.PauseResume += paused => CwLog.Write("C", "EVENT", paused ? "Pause an" : "Pause aus");
        api.Event.LeaveWorld += () => CwLog.Write("C", "EVENT", "Welt wird verlassen");

        // Dialoge und die statischen Attribute pro Bild pruefen: das laeuft auch bei pausiertem Spiel.
        guiPoller = new GuiPoller(api, clientRecipes);
        api.Event.RegisterRenderer(guiPoller, EnumRenderStage.Ortho, "craftwatch-guipoller");
    }

    private void ClientTick()
    {
        if (capi == null || clientRecipes == null) return;
        clientRecipes.Tick();

        if (clientGrid == null && capi.World.Player != null)
        {
            clientGrid = new GridWatcher("C", clientRecipes, capi.World.Player, OnClientSuspect);
        }
        clientGrid?.Tick();
    }

    private void NotifyClient(string message)
    {
        long now = Environment.TickCount64;
        if (capi == null || now - lastClientNotifyMs < NotifyEveryMs) return;
        lastClientNotifyMs = now;
        capi.ShowChatMessage($"[CraftWatch] Client: {message}");
    }

    /// <summary>Der Client rechnet ein Rezept aus, das Ausgabefeld bleibt aber leer: die Signatur des Fehlers.</summary>
    private void OnClientSuspect(GridWatcher grid, List<GridRecipe> matches)
    {
        if (capi == null || clientRecipes == null) return;

        ItemSlot[] slots = grid.InputSlots();
        string gridText = Describe.Grid(slots);
        string message = $"Rezept {string.Join("; ", matches.Take(3).Select(GridWatcher.Key))} passt, Ausgabefeld bleibt leer";
        CwLog.Write("C", "ALARM", $"SIGNATUR {message}; Grid [{gridText}]");
        foreach (GridRecipe recipe in matches.Take(3))
        {
            CwLog.Write("C", "DIAG", $"SIGNATUR {clientRecipes.Diagnose(recipe, slots, grid.GridWidth, grid.Player)}");
        }
        CwLog.Write("C", "DIAG", $"SIGNATUR Inventar-Felder: {grid.InventoryFields()}");
        CwLog.Write("C", "DIAG", $"SIGNATUR offene Dialoge: {guiPoller?.Current}");

        var recipes = capi.World.GridRecipes;
        var report = new BugReport
        {
            RecipeIndex = matches.Take(3).Select(recipe => recipes.IndexOf(recipe)).ToArray(),
            RecipeKey = matches.Take(3).Select(GridWatcher.Key).ToArray(),
            ClientGrid = gridText
        };
        IClientNetworkChannel channel = capi.Network.GetChannel(ChannelName);
        if (channel.Connected) channel.SendPacket(report);
        else CwLog.Write("C", "DIAG", "Server hat CraftWatch nicht geladen, keine Server-Diagnose");

        NotifyClient(message);
    }

    #endregion
}

/// <summary>Protokolliert pro Bild, welche Dialoge offen sind. Laeuft im Rendern und damit auch in der Pause.</summary>
public class GuiPoller : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly RecipeWatcher recipes;
    private HashSet<string> open = [];

    public double RenderOrder => 1;
    public int RenderRange => 1;
    public string Current => string.Join(", ", open);

    public GuiPoller(ICoreClientAPI capi, RecipeWatcher recipes)
    {
        this.capi = capi;
        this.recipes = recipes;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        try
        {
            if (recipes.BaselineTaken) recipes.CheckDefaultAttributes();

            var now = new HashSet<string>();
            foreach (GuiDialog dialog in capi.Gui.OpenedGuis.OfType<GuiDialog>())
            {
                // Dauerhaft sichtbare HUD-Elemente sind kein Ereignis.
                if (dialog.DialogType == EnumDialogType.HUD) continue;
                now.Add(dialog.DebugName ?? dialog.GetType().Name);
            }

            foreach (string name in now.Where(name => !open.Contains(name)))
            {
                CwLog.Write("C", "GUI", $"auf: {name}{(capi.IsGamePaused ? " (Spiel pausiert)" : "")}");
            }
            foreach (string name in open.Where(name => !now.Contains(name)))
            {
                CwLog.Write("C", "GUI", $"zu:  {name}");
            }
            open = now;
        }
        catch (Exception e)
        {
            CwLog.Write("C", "ERROR", $"GuiPoller: {e.GetType().Name}: {e.Message}");
        }
    }

    public void Dispose() { }
}
