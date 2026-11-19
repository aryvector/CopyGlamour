using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Cabinet = Lumina.Excel.Sheets.Cabinet;
using MirageStoreSetItem = Lumina.Excel.Sheets.MirageStoreSetItem;

namespace CopyGlamour;

/// <summary>
/// Finds where the player keeps each piece: equipped, bags, armoury chest, saddlebag, glamour dresser, armoire, retainers.
/// Reads game memory, so call it only on the game's main thread (the window's Draw or Framework.Update).
/// </summary>
public sealed unsafe class OwnedItems
{
    // The dresser stores an HQ item as its item ID plus this.
    private const uint HighQualityItemIdOffset = 1_000_000;

    private static readonly (InventoryType[] Containers, string Place)[] PlayerContainers =
    [
        ([InventoryType.EquippedItems], "Equipped"),
        ([InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4], "Inventory"),
        ([InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
          InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
          InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings], "Armoury chest"),
        ([InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2], "Saddlebag"),
    ];

    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
        InventoryType.RetainerEquippedItems, InventoryType.RetainerMarket,
    ];

    private readonly Configuration configuration;
    private readonly Action saveConfiguration;

    // The armoire is checked by Cabinet row, not item ID.
    private readonly Dictionary<uint, uint> cabinetIdByItemId = [];

    // An outfit set in the dresser holds its pieces: set item ID → the piece in each of the 11 MirageStoreSetItem columns (0 = none).
    private readonly Dictionary<uint, uint[]> setPieceItemIdsBySetItemId = [];

    public OwnedItems(IDataManager dataManager, Configuration configuration, Action saveConfiguration)
    {
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;

        foreach (var cabinet in dataManager.GetExcelSheet<Cabinet>(ClientLanguage.English))
            if (cabinet.Item.RowId != 0) cabinetIdByItemId.TryAdd(cabinet.Item.RowId, cabinet.RowId);

        foreach (var outfitSet in dataManager.GetExcelSheet<MirageStoreSetItem>(ClientLanguage.English))
        {
            uint[] setPieceItemIds =
            [
                outfitSet.MainHand.RowId, outfitSet.OffHand.RowId, outfitSet.Head.RowId, outfitSet.Body.RowId, outfitSet.Hands.RowId,
                outfitSet.Legs.RowId, outfitSet.Feet.RowId, outfitSet.Earrings.RowId, outfitSet.Necklace.RowId, outfitSet.Bracelets.RowId,
                outfitSet.Ring.RowId,
            ];
            if (setPieceItemIds.Any(itemId => itemId != 0)) setPieceItemIdsBySetItemId[outfitSet.RowId] = setPieceItemIds;
        }
    }

    /// <summary>Where each row's piece is, and which storages the game hasn't loaded yet, so a piece there can't be seen.</summary>
    public sealed record Ownership(Dictionary<PieceRow, List<string>> PlacesByRow, List<string> NotChecked);

    public Ownership Find(IReadOnlyList<PieceRow> pieceRows)
    {
        var placesByRow = new Dictionary<PieceRow, List<string>>();
        var notChecked = new List<string>();
        var rowsByItemId = pieceRows.Where(pieceRow => pieceRow.SourceItemId != 0).ToLookup(pieceRow => pieceRow.SourceItemId);
        var addPlace = (PieceRow pieceRow, string place) =>
        {
            if (!placesByRow.TryGetValue(pieceRow, out var places)) placesByRow[pieceRow] = places = [];
            if (!places.Contains(place)) places.Add(place);
        };
        var addPlaceForItem = (uint itemId, string place) =>
        {
            foreach (var pieceRow in rowsByItemId[itemId]) addPlace(pieceRow, place);
        };

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null)
        {
            foreach (var (containerTypes, place) in PlayerContainers)
            {
                foreach (var containerType in containerTypes)
                {
                    var container = inventoryManager->GetInventoryContainer(containerType);
                    if (container == null || !container->IsLoaded) continue;
                    foreach (var itemId in ItemIdsIn(container)) addPlaceForItem(itemId, place);
                }
            }
            // The premium saddlebag is only there with the subscription, so only the normal one says it's unchecked.
            var saddlebag = inventoryManager->GetInventoryContainer(InventoryType.SaddleBag1);
            if (saddlebag == null || !saddlebag->IsLoaded) notChecked.Add("saddlebag");
        }

        var mirageManager = MirageManager.Instance();
        if (mirageManager == null || !mirageManager->PrismBoxLoaded)
        {
            notChecked.Add("glamour dresser");
        }
        else
        {
            var dresserItemIds = mirageManager->PrismBoxItemIds;
            for (var dresserIndex = 0; dresserIndex < dresserItemIds.Length; dresserIndex++)
            {
                var itemId = dresserItemIds[dresserIndex] % HighQualityItemIdOffset;
                if (itemId == 0) continue;
                addPlaceForItem(itemId, "Glamour dresser");
                if (!setPieceItemIdsBySetItemId.TryGetValue(itemId, out var setPieceItemIds)) continue;
                // A set can be stored with only some pieces; slot numbers follow the MirageStoreSetItem columns,
                // as ClientStructs documents for RestorePrismBoxSetItem.
                for (var setSlot = 0; setSlot < setPieceItemIds.Length; setSlot++)
                    if (setPieceItemIds[setSlot] != 0 && mirageManager->IsSetSlotUnlocked((uint)dresserIndex, setSlot))
                        addPlaceForItem(setPieceItemIds[setSlot], "Glamour dresser (outfit)");
            }
        }

        var uiState = UIState.Instance();
        if (uiState != null)
        {
            if (!uiState->Cabinet.IsCabinetLoaded())
            {
                notChecked.Add("armoire");
            }
            else
            {
                foreach (var itemRows in rowsByItemId)
                    if (cabinetIdByItemId.TryGetValue(itemRows.Key, out var cabinetId) && uiState->Cabinet.IsItemInCabinet(cabinetId))
                        addPlaceForItem(itemRows.Key, "Armoire");
            }

            // Facewear is owned once it's unlocked; the unlock item is used up then.
            foreach (var pieceRow in pieceRows)
                if (pieceRow.FacewearId != 0 && uiState->PlayerState.IsGlassesUnlocked((ushort)pieceRow.FacewearId))
                    addPlace(pieceRow, "Unlocked");

            var playerContentId = uiState->PlayerState.ContentId;
            var savedRetainers = configuration.Retainers.Where(savedRetainer => savedRetainer.OwnerContentId == playerContentId).ToList();
            foreach (var savedRetainer in savedRetainers)
                foreach (var itemId in savedRetainer.ItemIds)
                    addPlaceForItem(itemId, $"Retainer {savedRetainer.Name}");

            var retainerManager = RetainerManager.Instance();
            var retainersNotSeen = 0;
            if (retainerManager != null && retainerManager->IsReady)
                foreach (var retainer in retainerManager->Retainers)
                    if (retainer.RetainerId != 0 && savedRetainers.All(savedRetainer => savedRetainer.RetainerId != retainer.RetainerId))
                        retainersNotSeen++;
            if (savedRetainers.Count == 0) notChecked.Add("retainers");
            else if (retainersNotSeen > 0) notChecked.Add($"{retainersNotSeen} retainer(s)");
        }

        return new Ownership(placesByRow, notChecked);
    }

    /// <summary>
    /// The game only holds the items of the retainer you have open, so this saves them to the settings while one is open.
    /// Called about once a second, also while the window is closed.
    /// </summary>
    public void RememberOpenRetainer()
    {
        var inventoryManager = InventoryManager.Instance();
        var retainerManager = RetainerManager.Instance();
        var uiState = UIState.Instance();
        if (inventoryManager == null || retainerManager == null || uiState == null) return;
        var playerContentId = uiState->PlayerState.ContentId;
        if (playerContentId == 0) return;

        // A retainer you dismissed for good disappears from the game's list, so it goes from the settings too.
        if (retainerManager->IsReady)
        {
            var currentRetainerIds = new HashSet<ulong>();
            foreach (var retainer in retainerManager->Retainers)
                if (retainer.RetainerId != 0) currentRetainerIds.Add(retainer.RetainerId);
            if (configuration.Retainers.RemoveAll(savedRetainer => savedRetainer.OwnerContentId == playerContentId && !currentRetainerIds.Contains(savedRetainer.RetainerId)) > 0)
                saveConfiguration();
        }

        var openRetainer = retainerManager->GetActiveRetainer();
        var firstPage = inventoryManager->GetInventoryContainer(InventoryType.RetainerPage1);
        if (openRetainer == null || openRetainer->RetainerId == 0 || firstPage == null || !firstPage->IsLoaded) return;

        // shortcut: right after switching retainers the pages may still hold the previous one's items for a moment;
        // the next check a second later saves the right ones. Watch the game's retainer inventory packets if that ever sticks.
        var itemIds = new SortedSet<uint>();
        foreach (var containerType in RetainerContainers)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container != null && container->IsLoaded) itemIds.UnionWith(ItemIdsIn(container));
        }

        var retainerId = openRetainer->RetainerId;
        var retainerName = openRetainer->NameString;
        var savedRetainer = configuration.Retainers.Find(saved => saved.RetainerId == retainerId);
        if (savedRetainer != null && savedRetainer.Name == retainerName && savedRetainer.ItemIds.SequenceEqual(itemIds)) return;

        configuration.Retainers.RemoveAll(saved => saved.RetainerId == retainerId);
        configuration.Retainers.Add(new SavedRetainer { RetainerId = retainerId, OwnerContentId = playerContentId, Name = retainerName, ItemIds = [.. itemIds] });
        saveConfiguration();
    }

    private static List<uint> ItemIdsIn(InventoryContainer* container)
    {
        var itemIds = new List<uint>();
        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var item = container->GetInventorySlot(slotIndex);
            if (item != null && !item->IsEmpty()) itemIds.Add(item->GetBaseItemId());
        }
        return itemIds;
    }
}
