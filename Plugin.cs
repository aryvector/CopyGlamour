using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CopyGlamour;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/copyglamour";

    // The local player is always object index 0 in the game's object table.
    private const int LocalPlayerObjectIndex = 0;

    // Glamourer's "Once" flag: apply as a temporary change, without writing it into Glamourer's automation.
    private const ulong GlamourerApplyOnce = 0x01;

    // Glamourer's default revert flags: equipment (0x02) + customization (0x04).
    private const ulong GlamourerRevertEquipmentAndCustomization = 0x02 | 0x04;

    // Glamourer's OpenMainWindow tab number: -1 (MainTabType.None) opens on whichever tab Glamourer showed last.
    private const int GlamourerTabLastUsed = -1;

    // Glamourer's facewear call takes a bonus slot; 1 is the glasses slot (Glamourer.Api's ApiBonusSlot.Glasses).
    private const byte GlamourerBonusSlotGlasses = 1;

    // Facewear is unlocked by an item named "The Faces We Wear - <style name>".
    private const string FacewearUnlockItemPrefix = "The Faces We Wear - ";

    // Facewear colours in the order the game lists a style's variants (Default, Silver, Gold, ...).
    // Only a fallback for when a variant's own name doesn't say its colour.
    private static readonly string[] FacewearColoursInGameOrder =
        ["Default", "Silver", "Gold", "Black", "White", "Grey", "Red", "Blue", "Green", "Brass", "Purple", "Brown"];

    // Enough to find the glamours you looked at recently, while keeping the settings file small.
    private const int RecentGlamoursToKeep = 20;

    public readonly WindowSystem WindowSystem = new("CopyGlamour");
    private readonly MainWindow mainWindow;

    public Configuration Configuration { get; }

    /// <summary>Where to get each item. Null until it's built in the background, a few seconds after the plugin loads.</summary>
    public ItemSourceIndex? ItemSources { get; private set; }

    // Waited for in Dispose, so a reload in the first seconds doesn't leave it reading game data after unload.
    private readonly Task itemSourcesBuild;

    // Glamourer's IPC functions, by the labels Glamourer.Api uses (Glamourer.SetItem.V3 etc.).
    private readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> glamourerSetItem;
    private readonly ICallGateSubscriber<int, byte, ulong, uint, ulong, int> glamourerSetBonusItem;
    private readonly ICallGateSubscriber<int, uint, ulong, int> glamourerRevertState;
    private readonly ICallGateSubscriber<(int, int)> glamourerApiVersion;
    private readonly ICallGateSubscriber<int, uint, (int, string?)> glamourerGetStateBase64;
    private readonly ICallGateSubscriber<string, string, (int, Guid)> glamourerAddDesign;
    private readonly ICallGateSubscriber<bool?, int, object> glamourerOpenMainWindow;
    private readonly ICallGateSubscriber<Guid, object> glamourerOpenDesign;

    /// <summary>The Glamourer design saved for the glamour shown now; Guid.Empty until "Save as Glamourer design" succeeds.</summary>
    public Guid SavedDesignId { get; private set; } = Guid.Empty;

    // English names → game data. Eorzea Collection writes names in English, so the lookups use the English
    // game data whatever language the game client runs in.
    private readonly Dictionary<string, Item> equippableItemByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte> stainIdByName = new(StringComparer.OrdinalIgnoreCase);

    // Facewear: Eorzea Collection names the style ("Simple Oval Spectacles") and its colour ("Brown").
    // The game keeps each style as a GlassesStyle row listing up to 12 colour variants (Glasses rows).
    private readonly Dictionary<string, GlassesStyle> facewearStyleByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Item> facewearUnlockItemByStyleName = new(StringComparer.OrdinalIgnoreCase);

    public Plugin()
    {
        // A settings file that can't be read (e.g. edited by hand) starts fresh instead of stopping the plugin.
        Configuration configuration;
        try
        {
            configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Couldn't read the settings, starting with defaults");
            configuration = new Configuration();
        }
        Configuration = configuration;

        glamourerSetItem = PluginInterface.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
        glamourerSetBonusItem = PluginInterface.GetIpcSubscriber<int, byte, ulong, uint, ulong, int>("Glamourer.SetBonusItem");
        glamourerRevertState = PluginInterface.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
        glamourerApiVersion = PluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        glamourerGetStateBase64 = PluginInterface.GetIpcSubscriber<int, uint, (int, string?)>("Glamourer.GetStateBase64");
        glamourerAddDesign = PluginInterface.GetIpcSubscriber<string, string, (int, Guid)>("Glamourer.AddDesign");
        glamourerOpenMainWindow = PluginInterface.GetIpcSubscriber<bool?, int, object>("Glamourer.OpenMainWindow");
        glamourerOpenDesign = PluginInterface.GetIpcSubscriber<Guid, object>("Glamourer.OpenDesign");

        // Only items with an equip slot can be worn. If two items share a name, the first one wins for the name lookup:
        // same-named gear shares its look, so either is fine for a glamour.
        // Sources are found for every wearable item and every facewear unlock item.
        var itemIdsToFindSourcesFor = new HashSet<uint>();
        foreach (var item in DataManager.GetExcelSheet<Item>(ClientLanguage.English))
        {
            var itemName = item.Name.ExtractText();
            if (itemName.Length == 0) continue;
            if (itemName.StartsWith(FacewearUnlockItemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                itemIdsToFindSourcesFor.Add(item.RowId);
                facewearUnlockItemByStyleName.TryAdd(itemName[FacewearUnlockItemPrefix.Length..], item);
                continue;
            }
            if (item.EquipSlotCategory.RowId == 0) continue;
            itemIdsToFindSourcesFor.Add(item.RowId);
            equippableItemByName.TryAdd(itemName, item);
        }

        foreach (var facewearStyle in DataManager.GetExcelSheet<GlassesStyle>(ClientLanguage.English))
        {
            var facewearStyleName = facewearStyle.Name.ExtractText();
            if (facewearStyleName.Length > 0) facewearStyleByName.TryAdd(facewearStyleName, facewearStyle);
        }

        foreach (var stain in DataManager.GetExcelSheet<Stain>(ClientLanguage.English))
        {
            var stainName = stain.Name.ExtractText();
            if (stain.RowId == 0 || stain.RowId > byte.MaxValue || stainName.Length == 0) continue;
            stainIdByName.TryAdd(stainName, (byte)stain.RowId);
        }

        // Reading every shop, NPC and recipe takes a few seconds, so it runs in the background; the table says
        // "Finding sources…" until it's done.
        itemSourcesBuild = Task.Run(() =>
        {
            try
            {
                var buildTime = Stopwatch.StartNew();
                ItemSources = new ItemSourceIndex(DataManager, itemIdsToFindSourcesFor, Log);
                Log.Information($"Item sources ready in {buildTime.ElapsedMilliseconds} ms");
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Finding item sources failed");
            }
        });

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Copy Glamour. Add a saved page's path to load it straight away: /copyglamour <path>",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        // The only setting lives in the main window, so the settings button opens that too.
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        WindowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        // The build catches its own errors, so this only waits; 5 s caps an unload if it ever hangs.
        itemSourcesBuild.Wait(TimeSpan.FromSeconds(5));
    }

    private void OnCommand(string command, string args)
    {
        mainWindow.IsOpen = true;
        if (args.Trim().Length > 0) mainWindow.StartLoad(args.Trim());
    }

    /// <summary>
    /// Reads a glamour from an Eorzea Collection page (see GlamourPageParser), given either
    /// the page's copied text (Ctrl+A, Ctrl+C in the browser) or the path of a page saved with Ctrl+S.
    /// Links aren't supported: Eorzea Collection refuses downloads that don't come from a browser (403).
    /// Throws with a plain-words message if the page can't be read or has no gear on it.
    /// </summary>
    public async Task<CopiedGlamour> LoadGlamour(string glamourSource)
    {
        // Copied page text always spans many lines; a file path is one line.
        string pageContent;
        if (glamourSource.Contains('\n'))
        {
            pageContent = glamourSource;
        }
        else if (glamourSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Links can't be opened by the plugin (Eorzea Collection blocks it). " +
                "Open the link in your browser, press Ctrl+A then Ctrl+C, and click Paste from clipboard.");
        }
        else if (File.Exists(glamourSource.Trim('"')))
        {
            pageContent = await File.ReadAllTextAsync(glamourSource.Trim('"'));
        }
        else
        {
            throw new InvalidOperationException("No file at that path. Paste the full path of a page saved with Ctrl+S.");
        }

        var glamour = GlamourPageParser.Parse(pageContent);
        Log.Information($"Read \"{glamour.Title}\" by {glamour.Creator}: {glamour.Pieces.Count} gear pieces");
        return glamour;
    }

    /// <summary>
    /// Looks every piece up in the game data (item, slot, dyes, who can wear it) and remembers the glamour in the recent list.
    /// Runs on the game's main thread, where the window reads the recent list.
    /// </summary>
    public Task<List<PieceRow>> ResolveGlamour(CopiedGlamour glamour)
        => Framework.RunOnFrameworkThread(() =>
        {
            // A saved design belongs to the glamour it was saved from.
            SavedDesignId = Guid.Empty;

            var pieceRows = new List<PieceRow>();
            var ringsSeen = 0;
            foreach (var gearPiece in glamour.Pieces)
            {
                if (gearPiece.ItemName.Length == 0)
                {
                    pieceRows.Add(new PieceRow { Piece = gearPiece, Selected = false, HasProblem = true, Message = "Couldn't read this piece's name from the page" });
                    continue;
                }

                var lookupName = gearPiece.ItemName.Replace('’', '\'');
                if (!equippableItemByName.TryGetValue(lookupName, out var item))
                {
                    if (!facewearStyleByName.TryGetValue(lookupName, out var facewearStyle))
                    {
                        pieceRows.Add(new PieceRow { Piece = gearPiece, Selected = false, HasProblem = true, Message = "No wearable item or facewear with this name in the game data" });
                        continue;
                    }

                    // Facewear: the page's first "dye" is the colour. Pick the variant whose name has that colour as a word;
                    // if no variant name says it, use the colour's place in the game's colour order; else the first variant.
                    var facewearColourName = gearPiece.DyeNames.Count > 0 ? gearPiece.DyeNames[0] : "";
                    var variantGlassesIds = facewearStyle.Glasses.Select(glassesRef => glassesRef.RowId).ToList();
                    uint chosenGlassesId = 0;
                    if (facewearColourName.Length > 0)
                    {
                        var colourWord = new Regex($@"\b{Regex.Escape(facewearColourName)}\b", RegexOptions.IgnoreCase);
                        foreach (var glassesRef in facewearStyle.Glasses)
                        {
                            if (glassesRef.RowId == 0 || !glassesRef.IsValid || !colourWord.IsMatch(glassesRef.Value.Name.ExtractText())) continue;
                            chosenGlassesId = glassesRef.RowId;
                            break;
                        }
                        var colourIndexInGameOrder = Array.FindIndex(FacewearColoursInGameOrder,
                            colour => colour.Equals(facewearColourName, StringComparison.OrdinalIgnoreCase));
                        if (chosenGlassesId == 0 && colourIndexInGameOrder >= 0 && colourIndexInGameOrder < variantGlassesIds.Count)
                            chosenGlassesId = variantGlassesIds[colourIndexInGameOrder];
                    }
                    var colourFound = chosenGlassesId != 0 || facewearColourName.Length == 0;
                    if (chosenGlassesId == 0) chosenGlassesId = variantGlassesIds.FirstOrDefault(glassesId => glassesId != 0);

                    pieceRows.Add(new PieceRow
                    {
                        Piece = gearPiece,
                        FacewearId = chosenGlassesId,
                        SourceItemId = facewearUnlockItemByStyleName.TryGetValue(lookupName, out var facewearUnlockItem) ? facewearUnlockItem.RowId : 0,
                        Selected = chosenGlassesId != 0,
                        HasProblem = chosenGlassesId == 0 || !colourFound,
                        Message = chosenGlassesId == 0 ? "This facewear has no variants in the game data"
                            : colourFound ? "Facewear"
                            : $"Colour \"{facewearColourName}\" not found, using the first colour",
                    });
                    continue;
                }

                // The game data says which slot an item goes in: 1 = the slot it's worn in (-1 only means it hides that slot,
                // e.g. a robe over the legs). A two-handed weapon is MainHand 1 + OffHand -1, so it lands on MainHand.
                // Rings fit either hand: the first ring goes on the right hand, the second on the left.
                var slotCategory = item.EquipSlotCategory.Value;
                GlamourerSlot? slot =
                    slotCategory.MainHand == 1 ? GlamourerSlot.MainHand :
                    slotCategory.OffHand == 1 ? GlamourerSlot.OffHand :
                    slotCategory.Head == 1 ? GlamourerSlot.Head :
                    slotCategory.Body == 1 ? GlamourerSlot.Body :
                    slotCategory.Gloves == 1 ? GlamourerSlot.Hands :
                    slotCategory.Legs == 1 ? GlamourerSlot.Legs :
                    slotCategory.Feet == 1 ? GlamourerSlot.Feet :
                    slotCategory.Ears == 1 ? GlamourerSlot.Ears :
                    slotCategory.Neck == 1 ? GlamourerSlot.Neck :
                    slotCategory.Wrists == 1 ? GlamourerSlot.Wrists :
                    slotCategory.FingerL == 1 || slotCategory.FingerR == 1 ? (ringsSeen++ == 0 ? GlamourerSlot.RightRing : GlamourerSlot.LeftRing) :
                    (GlamourerSlot?)null;

                // Two dye channels; a missing tag or "Undyed" means no dye (0) in that channel.
                // A List, not a byte[]: Dalamud's IPC converts arguments through JSON, where a List<byte> becomes
                // a number array that turns back into Glamourer's IReadOnlyList<byte>, but a byte[] becomes a text string that can't.
                var stainIds = new List<byte> { 0, 0 };
                var unknownDyeNames = new List<string>();
                for (var dyeChannel = 0; dyeChannel < stainIds.Count && dyeChannel < gearPiece.DyeNames.Count; dyeChannel++)
                {
                    var dyeName = gearPiece.DyeNames[dyeChannel];
                    if (dyeName.Equals("Undyed", StringComparison.OrdinalIgnoreCase)) continue;
                    if (stainIdByName.TryGetValue(dyeName, out var stainId)) stainIds[dyeChannel] = stainId;
                    else unknownDyeNames.Add(dyeName);
                }

                var jobCategoryName = item.ClassJobCategory.IsValid ? item.ClassJobCategory.Value.Name.ExtractText() : "";
                var isWeapon = slot is GlamourerSlot.MainHand or GlamourerSlot.OffHand;
                var readMessage =
                    slot == null ? "Item has no slot Glamourer can set" :
                    unknownDyeNames.Count > 0 ? $"Unknown dye: {string.Join(", ", unknownDyeNames)}" :
                    isWeapon && jobCategoryName.Length > 0 ? $"Weapon for {jobCategoryName}" :
                    "";

                pieceRows.Add(new PieceRow
                {
                    Piece = gearPiece,
                    ItemId = item.RowId,
                    SourceItemId = item.RowId,
                    Slot = slot,
                    StainIds = stainIds,
                    UnknownDyeNames = unknownDyeNames,
                    JobCategoryName = jobCategoryName,
                    Selected = slot != null,
                    HasProblem = slot == null || unknownDyeNames.Count > 0,
                    Message = readMessage,
                });
            }

            // Most recent first; the same glamour (same title, creator and items) moves to the top instead of being listed twice.
            Configuration.RecentGlamours.RemoveAll(recentGlamour =>
                recentGlamour.Title == glamour.Title
                && recentGlamour.Creator == glamour.Creator
                && recentGlamour.Pieces.Select(recentPiece => recentPiece.ItemName).SequenceEqual(glamour.Pieces.Select(gearPiece => gearPiece.ItemName)));
            Configuration.RecentGlamours.Insert(0, glamour);
            if (Configuration.RecentGlamours.Count > RecentGlamoursToKeep)
                Configuration.RecentGlamours.RemoveRange(RecentGlamoursToKeep, Configuration.RecentGlamours.Count - RecentGlamoursToKeep);
            PluginInterface.SavePluginConfig(Configuration);

            return pieceRows;
        });

    /// <summary>
    /// Puts every ticked piece on the local player through Glamourer and writes the outcome into each row.
    /// Runs on the game's main thread, as IPC calls into other plugins should. Returns how many pieces were applied.
    /// </summary>
    public Task<int> ApplyGlamour(IReadOnlyList<PieceRow> pieceRows)
        => Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                glamourerApiVersion.InvokeFunc();
            }
            catch (IpcNotReadyError)
            {
                throw new InvalidOperationException("Glamourer isn't installed or isn't enabled.");
            }

            var appliedCount = 0;
            foreach (var pieceRow in pieceRows)
            {
                pieceRow.Applied = false;
                if (!pieceRow.Selected)
                {
                    if (pieceRow.CanApply)
                    {
                        pieceRow.Message = "Not selected";
                        pieceRow.HasProblem = false;
                    }
                    continue;
                }
                if (!pieceRow.CanApply) continue;

                // Facewear goes through Glamourer's own facewear (bonus item) call, gear through the normal one.
                var glamourerResult = pieceRow.FacewearId != 0
                    ? (GlamourerResult)glamourerSetBonusItem.InvokeFunc(
                        LocalPlayerObjectIndex, GlamourerBonusSlotGlasses, pieceRow.FacewearId, 0, GlamourerApplyOnce)
                    : (GlamourerResult)glamourerSetItem.InvokeFunc(
                        LocalPlayerObjectIndex, (byte)pieceRow.Slot!.Value, pieceRow.ItemId, pieceRow.StainIds, 0, GlamourerApplyOnce);

                var isWeapon = pieceRow.Slot is GlamourerSlot.MainHand or GlamourerSlot.OffHand;
                pieceRow.Applied = glamourerResult == GlamourerResult.Success;
                pieceRow.HasProblem = !pieceRow.Applied || pieceRow.UnknownDyeNames.Count > 0;
                pieceRow.Message = glamourerResult switch
                {
                    GlamourerResult.Success when pieceRow.UnknownDyeNames.Count > 0
                        => $"Applied, but unknown dye left undyed: {string.Join(", ", pieceRow.UnknownDyeNames)}",
                    GlamourerResult.Success => "Applied",
                    GlamourerResult.ItemInvalid when isWeapon && pieceRow.JobCategoryName.Length > 0
                        => $"Glamourer refused: this weapon is for {pieceRow.JobCategoryName}. Switch to that job and apply again.",
                    GlamourerResult.ItemInvalid when isWeapon => "Glamourer refused: the weapon doesn't match your current job's weapon type",
                    GlamourerResult.ItemInvalid => "Glamourer refused this item",
                    GlamourerResult.ActorNotFound => "Glamourer couldn't find your character",
                    GlamourerResult.InvalidKey => "Your Glamourer state is locked by another plugin",
                    _ => $"Glamourer answered {glamourerResult}",
                };
                if (pieceRow.Applied) appliedCount++;
                Log.Information($"{pieceRow.Piece.SlotLabel} {pieceRow.Piece.ItemName} → item {pieceRow.ItemId}, facewear {pieceRow.FacewearId}, slot {pieceRow.Slot}, " +
                                $"stains [{pieceRow.StainIds[0]}, {pieceRow.StainIds[1]}]: {glamourerResult}");
            }

            return appliedCount;
        });

    /// <summary>
    /// Saves the character's current look in Glamourer as a new design, so the glamour stays after a revert or a restart.
    /// It's a snapshot of everything Glamourer shows on you right now, so apply the glamour first.
    /// </summary>
    public Task<string> SaveAsGlamourerDesign(string designName)
        => Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var (getStateResult, currentLookBase64) = glamourerGetStateBase64.InvokeFunc(LocalPlayerObjectIndex, 0);
                if ((GlamourerResult)getStateResult != GlamourerResult.Success || string.IsNullOrEmpty(currentLookBase64))
                    return $"Glamourer couldn't read your current look ({(GlamourerResult)getStateResult}).";

                var (addDesignResult, newDesignId) = glamourerAddDesign.InvokeFunc(currentLookBase64, designName);
                if ((GlamourerResult)addDesignResult != GlamourerResult.Success)
                    return $"Glamourer couldn't create the design ({(GlamourerResult)addDesignResult}).";
                SavedDesignId = newDesignId;
                return $"Saved as Glamourer design \"{designName}\". \"Open saved design\" shows it in Glamourer.";
            }
            catch (IpcNotReadyError)
            {
                return "Glamourer isn't installed or isn't enabled.";
            }
        });

    /// <summary>
    /// Opens Glamourer's window: on the saved design (Designs tab, design selected) once one was saved for this glamour,
    /// otherwise on whichever tab Glamourer showed last. Called from the window's Draw, which already runs on the game's main thread.
    /// Older Glamourer versions without these calls get its /glamourer command instead.
    /// Returns false if nothing works (Glamourer not installed or not enabled).
    /// </summary>
    public bool OpenGlamourer()
    {
        try
        {
            if (SavedDesignId != Guid.Empty) glamourerOpenDesign.InvokeAction(SavedDesignId);
            else glamourerOpenMainWindow.InvokeAction(true, GlamourerTabLastUsed);
            return true;
        }
        catch (IpcError)
        {
            return CommandManager.ProcessCommand("/glamourer");
        }
    }

    /// <summary>Undoes all Glamourer changes on the local player (back to what the game shows).</summary>
    public Task<string> RevertGlamour()
        => Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var glamourerResult = (GlamourerResult)glamourerRevertState.InvokeFunc(LocalPlayerObjectIndex, 0, GlamourerRevertEquipmentAndCustomization);
                return glamourerResult == GlamourerResult.Success ? "Reverted." : $"Glamourer answered {glamourerResult}.";
            }
            catch (IpcNotReadyError)
            {
                return "Glamourer isn't installed or isn't enabled.";
            }
        });
}
