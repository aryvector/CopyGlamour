using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace CopyGlamour;

/// <summary>
/// Where each item (wearable gear and facewear unlock items) can be got: gil vendors, currency vendors (tomestones, scrips, MGP, tokens …),
/// Grand Company vendors and crafting. Built once in the background from the game data alone.
/// The way shops are matched to the NPCs selling them, and NPCs to where they stand, follows
/// GlamourBrowser by ShyneeAK (github.com/ShyneeAK/GlamourBrowser, MIT licence).
/// Items that only drop in duties, come from quests, achievements or the Mog Station have no source here.
/// </summary>
public sealed class ItemSourceIndex
{
    private readonly Dictionary<uint, List<ItemSource>> sourcesByItemId = new();

    /// <summary>Vendors first (gil, currency, Grand Company), then crafting; empty if the game data shows no vendor or recipe.</summary>
    public IReadOnlyList<ItemSource> SourcesFor(uint itemId)
        => sourcesByItemId.TryGetValue(itemId, out var itemSources) ? itemSources : [];

    public ItemSourceIndex(IDataManager dataManager, IReadOnlySet<uint> itemIdsToFindSourcesFor, IPluginLog log)
    {
        // Each step runs on its own: if one fails (for example after a patch changes a sheet), the others still give sources.
        // System.Action spelled out: the game data also has a sheet called Action (the skills).
        var runStep = (string stepName, System.Action step) =>
        {
            try
            {
                step();
            }
            catch (Exception exception)
            {
                log.Warning(exception, $"Finding item sources: step '{stepName}' failed");
            }
        };

        var addSource = (uint itemId, ItemSource itemSource) =>
        {
            if (!sourcesByItemId.TryGetValue(itemId, out var itemSources)) sourcesByItemId[itemId] = itemSources = new List<ItemSource>();
            // The same NPC can sell an item from two of its shops; show it once.
            if (!itemSources.Contains(itemSource)) itemSources.Add(itemSource);
        };

        // ---------------------------------------------------------------------------------------------------------
        // 1. Which NPCs sell which shop.
        //    An NPC's ENpcData lists its event handlers. A handler is a shop's row id itself, or a step that leads to shops:
        //    TopicSelect (a menu like "Purchase gear / Purchase accessories"), PreHandler (a talk before the shop),
        //    CustomTalk (scripted vendors) or InclusionShop (a shop split into categories).
        //    The top 16 bits of a handler id say which kind it is.
        // ---------------------------------------------------------------------------------------------------------
        const uint GilShopHandlerType = 0x0004;
        const uint SpecialShopHandlerType = 0x001B;
        var npcIdsByShopId = new Dictionary<uint, List<uint>>();

        var linkShopToNpc = (uint shopId, uint npcId) =>
        {
            if (shopId == 0) return;
            if (!npcIdsByShopId.TryGetValue(shopId, out var npcIds)) npcIdsByShopId[shopId] = npcIds = new List<uint>();
            if (!npcIds.Contains(npcId)) npcIds.Add(npcId);
        };

        runStep("shop sellers", () =>
        {
            var inclusionShopSheet = dataManager.GetExcelSheet<InclusionShop>();
            var inclusionShopSeriesSheet = dataManager.GetSubrowExcelSheet<InclusionShopSeries>();
            var preHandlerSheet = dataManager.GetExcelSheet<PreHandler>();
            var topicSelectSheet = dataManager.GetExcelSheet<TopicSelect>();
            var customTalkSheet = dataManager.GetExcelSheet<CustomTalk>();
            var customTalkNestHandlersSheet = dataManager.GetSubrowExcelSheet<CustomTalkNestHandlers>();
            var specialShopSheet = dataManager.GetExcelSheet<SpecialShop>();
            var firstSpecialShopId = specialShopSheet.First().RowId;
            var lastSpecialShopId = specialShopSheet.Last().RowId;

            var linkInclusionShop = (uint handler, uint npcId) =>
            {
                if (!inclusionShopSheet.TryGetRow(handler, out var inclusionShop)) return;
                foreach (var category in inclusionShop.Category)
                {
                    if (category.RowId == 0 || !category.IsValid) continue;
                    if (!inclusionShopSeriesSheet.TryGetRow(category.Value.InclusionShopSeries.RowId, out var seriesEntries)) continue;
                    foreach (var seriesEntry in seriesEntries) linkShopToNpc(seriesEntry.SpecialShop.RowId, npcId);
                }
            };

            var linkPreHandler = (uint handler, uint npcId) =>
            {
                if (!preHandlerSheet.TryGetRow(handler, out var preHandler) || preHandler.Target.RowId == 0) return;
                if (preHandler.Target.RowId >> 16 == SpecialShopHandlerType) linkShopToNpc(preHandler.Target.RowId, npcId);
                else linkInclusionShop(preHandler.Target.RowId, npcId);
            };

            foreach (var npc in dataManager.GetExcelSheet<ENpcBase>())
            {
                foreach (var handlerRef in npc.ENpcData)
                {
                    var handler = handlerRef.RowId;
                    if (handler == 0) continue;

                    // A shop's row id directly (gil, special and Grand Company shops).
                    linkShopToNpc(handler, npc.RowId);

                    if (customTalkSheet.TryGetRow(handler, out var customTalk))
                    {
                        foreach (var scriptEntry in customTalk.Script)
                            if (scriptEntry.ScriptArg >= firstSpecialShopId && scriptEntry.ScriptArg <= lastSpecialShopId)
                                linkShopToNpc(scriptEntry.ScriptArg, npc.RowId);
                        if (customTalk.SpecialLinks.RowId != 0 && customTalkNestHandlersSheet.TryGetRow(customTalk.SpecialLinks.RowId, out var nestHandlers))
                            foreach (var nestHandler in nestHandlers) linkShopToNpc(nestHandler.NestHandler.RowId, npc.RowId);
                    }

                    if (topicSelectSheet.TryGetRow(handler, out var topicSelect))
                    {
                        foreach (var topicShopRef in topicSelect.Shop)
                        {
                            var topicShop = topicShopRef.RowId;
                            if (topicShop == 0) continue;
                            if (topicShop >> 16 is SpecialShopHandlerType or GilShopHandlerType) linkShopToNpc(topicShop, npc.RowId);
                            else linkPreHandler(topicShop, npc.RowId);
                        }
                    }

                    linkPreHandler(handler, npc.RowId);
                    linkInclusionShop(handler, npc.RowId);
                }
            }
        });

        // FATE shops (e.g. the gemstone traders) use the NPC's own id as their row id.
        runStep("FATE shop sellers", () =>
        {
            foreach (var fateShop in dataManager.GetExcelSheet<FateShop>())
                foreach (var specialShopRef in fateShop.SpecialShop)
                    linkShopToNpc(specialShopRef.RowId, fateShop.RowId);
        });

        // ---------------------------------------------------------------------------------------------------------
        // 2. Where NPCs stand. Level rows of type 8 place event NPCs in the world (world X/Z); converted to the map
        //    coordinates the game shows, with the same formula as Dalamud's MapUtil:
        //    0.02·offset + 2048/scale + 0.02·world + 1, where scale is Map.SizeFactor (100 when missing).
        // ---------------------------------------------------------------------------------------------------------
        const byte LevelTypeEventNpc = 8;
        const uint DefaultMapSizeFactor = 100;
        var npcPlaceById = new Dictionary<uint, NpcPlace>();
        var toMapCoordinate = (float worldCoordinate, uint mapSizeFactor, int mapOffset)
            => 0.02f * mapOffset + 2048f / (mapSizeFactor == 0 ? DefaultMapSizeFactor : mapSizeFactor) + 0.02f * worldCoordinate + 1f;

        runStep("NPC places", () =>
        {
            foreach (var level in dataManager.GetExcelSheet<Level>())
            {
                var npcId = level.Object.RowId;
                if (level.Type != LevelTypeEventNpc || npcId == 0 || npcPlaceById.ContainsKey(npcId) || !level.Map.IsValid || level.Territory.RowId == 0) continue;
                var map = level.Map.Value;
                npcPlaceById[npcId] = new NpcPlace(
                    level.Territory.RowId,
                    map.RowId,
                    toMapCoordinate(level.X, map.SizeFactor, map.OffsetX),
                    toMapCoordinate(level.Z, map.SizeFactor, map.OffsetY),
                    map.PlaceName.IsValid ? map.PlaceName.Value.Name.ExtractText() : "");
            }
        });

        // ---------------------------------------------------------------------------------------------------------
        // 3. Shops selling the items we want sources for. Kept as "pending" until all NPC places are known (step 4 may add more).
        // ---------------------------------------------------------------------------------------------------------
        var pendingVendorSources = new List<(uint ItemId, ItemSourceKind Kind, uint ShopId, string ShopName, string Cost)>();

        runStep("gil shops", () =>
        {
            var gilShopItemSheet = dataManager.GetSubrowExcelSheet<GilShopItem>();
            foreach (var gilShop in dataManager.GetExcelSheet<GilShop>())
            {
                if (!gilShopItemSheet.TryGetRow(gilShop.RowId, out var gilShopEntries)) continue;
                foreach (var gilShopEntry in gilShopEntries)
                {
                    var itemId = gilShopEntry.Item.RowId;
                    if (!itemIdsToFindSourcesFor.Contains(itemId) || !gilShopEntry.Item.IsValid) continue;
                    pendingVendorSources.Add((itemId, ItemSourceKind.GilVendor, gilShop.RowId, gilShop.Name.ExtractText(), $"{gilShopEntry.Item.Value.PriceMid:N0} gil"));
                }
            }
        });

        runStep("Grand Company shops", () =>
        {
            var gcScripShopItemSheet = dataManager.GetSubrowExcelSheet<GCScripShopItem>();
            var gcScripShopCategories = dataManager.GetExcelSheet<GCScripShopCategory>().ToList();
            foreach (var gcShop in dataManager.GetExcelSheet<GCShop>())
            {
                var grandCompanyName = gcShop.GrandCompany.IsValid ? gcShop.GrandCompany.Value.Name.ExtractText() : "Grand Company";
                foreach (var gcCategory in gcScripShopCategories.Where(category => category.GrandCompany.RowId == gcShop.GrandCompany.RowId))
                {
                    if (!gcScripShopItemSheet.TryGetRow(gcCategory.RowId, out var gcShopEntries)) continue;
                    foreach (var gcShopEntry in gcShopEntries)
                    {
                        if (!itemIdsToFindSourcesFor.Contains(gcShopEntry.Item.RowId)) continue;
                        pendingVendorSources.Add((gcShopEntry.Item.RowId, ItemSourceKind.GrandCompanyVendor, gcShop.RowId, grandCompanyName,
                            $"{gcShopEntry.CostGCSeals:N0} company seals ({grandCompanyName})"));
                    }
                }
            }
        });

        runStep("currency shops", () =>
        {
            // In a special shop, cost ids 1–7 aren't items but codes whose meaning depends on the shop's UseCurrencyType:
            // tomestones (code n = the n-th tomestone in TomestonesItem order), Poetics, any crafter/gatherer scrip, or gil.
            const uint GilItemId = 1;
            const uint PoeticsItemId = 28;
            const byte CurrencyTypeTomestonesA = 2;
            const byte CurrencyTypeTomestonesB = 4;
            const byte CurrencyTypeGil = 8;
            const byte CurrencyTypeScrips = 16;
            var tomestoneItemIdsInOrder = dataManager.GetExcelSheet<TomestonesItem>()
                .Where(tomestone => tomestone.Tomestones.RowId > 0)
                .OrderBy(tomestone => tomestone.Tomestones.RowId)
                .Select(tomestone => tomestone.Item.RowId)
                .ToList();
            var itemSheet = dataManager.GetExcelSheet<Item>();

            foreach (var specialShop in dataManager.GetExcelSheet<SpecialShop>())
            {
                foreach (var shopEntry in specialShop.Item)
                {
                    // The first real cost; an explicit loop because a default cost struct throws when read.
                    uint costItemId = 0, costAmount = 0;
                    foreach (var cost in shopEntry.ItemCosts)
                    {
                        if (cost.ItemCost.RowId == 0) continue;
                        costItemId = cost.ItemCost.RowId;
                        costAmount = cost.CurrencyCost;
                        break;
                    }

                    var isAnyScrip = false;
                    if (costItemId is >= 1 and <= 7)
                    {
                        if (specialShop.UseCurrencyType == CurrencyTypeScrips)
                        {
                            if (costItemId == 1) costItemId = PoeticsItemId;
                            else if (costItemId is 2 or 4 or 6 or 7) isAnyScrip = true;
                        }
                        else if (specialShop.UseCurrencyType == CurrencyTypeGil)
                        {
                            costItemId = GilItemId;
                        }
                        else if (specialShop.UseCurrencyType is CurrencyTypeTomestonesA or CurrencyTypeTomestonesB && costItemId <= tomestoneItemIdsInOrder.Count)
                        {
                            costItemId = tomestoneItemIdsInOrder[(int)costItemId - 1];
                        }
                    }

                    string costText;
                    if (costItemId == 0) costText = "free";
                    else if (isAnyScrip) costText = $"{costAmount:N0} scrips";
                    else if (costItemId == GilItemId) costText = $"{costAmount:N0} gil";
                    else if (costItemId <= 7) costText = $"{costAmount:N0} (special currency)";
                    else costText = $"{costAmount:N0} {(itemSheet.TryGetRow(costItemId, out var costItem) ? costItem.Name.ExtractText() : "currency")}";

                    foreach (var receivedItem in shopEntry.ReceiveItems)
                    {
                        if (!itemIdsToFindSourcesFor.Contains(receivedItem.Item.RowId)) continue;
                        pendingVendorSources.Add((receivedItem.Item.RowId, ItemSourceKind.CurrencyVendor, specialShop.RowId, specialShop.Name.ExtractText(), costText));
                    }
                }
            }
        });

        // ---------------------------------------------------------------------------------------------------------
        // 4. Sellers no Level row places: look them up in the zones' event layout files (planevent.lgb), one per zone
        //    background. Covers vendors like the hunt traders in Kugane.
        // ---------------------------------------------------------------------------------------------------------
        runStep("NPC places from zone layouts", () =>
        {
            var sellerNpcIdsWithoutPlace = new HashSet<uint>();
            foreach (var pendingVendorSource in pendingVendorSources)
                if (npcIdsByShopId.TryGetValue(pendingVendorSource.ShopId, out var sellerNpcIds))
                    foreach (var sellerNpcId in sellerNpcIds)
                        if (!npcPlaceById.ContainsKey(sellerNpcId)) sellerNpcIdsWithoutPlace.Add(sellerNpcId);

            var layoutBackgroundsRead = new HashSet<string>();
            foreach (var territory in dataManager.GetExcelSheet<TerritoryType>())
            {
                if (sellerNpcIdsWithoutPlace.Count == 0) break;
                var background = territory.Bg.ExtractText();
                var levelFolderIndex = background.IndexOf("/level/", StringComparison.Ordinal);
                if (levelFolderIndex < 0 || !territory.Map.IsValid || !layoutBackgroundsRead.Add(background)) continue;
                var eventLayout = dataManager.GameData.GetFile<LgbFile>($"bg/{background[..(levelFolderIndex + "/level/".Length)]}planevent.lgb");
                if (eventLayout == null) continue;

                var map = territory.Map.Value;
                foreach (var layer in eventLayout.Layers)
                {
                    foreach (var instanceObject in layer.InstanceObjects)
                    {
                        if (instanceObject.AssetType != LayerEntryType.EventNPC) continue;
                        var npcId = ((LayerCommon.ENPCInstanceObject)instanceObject.Object).ParentData.ParentData.BaseId;
                        if (!sellerNpcIdsWithoutPlace.Remove(npcId)) continue;
                        npcPlaceById[npcId] = new NpcPlace(
                            territory.RowId,
                            map.RowId,
                            toMapCoordinate(instanceObject.Transform.Translation.X, map.SizeFactor, map.OffsetX),
                            toMapCoordinate(instanceObject.Transform.Translation.Z, map.SizeFactor, map.OffsetY),
                            map.PlaceName.IsValid ? map.PlaceName.Value.Name.ExtractText() : "");
                    }
                }
            }
        });

        // ---------------------------------------------------------------------------------------------------------
        // 5. Turn pending shop entries into sources: one per NPC selling the shop, with its name, title and place.
        //    A shop with no known seller still shows, under its own name.
        // ---------------------------------------------------------------------------------------------------------
        runStep("vendor sources", () =>
        {
            var npcResidentSheet = dataManager.GetExcelSheet<ENpcResident>();
            // Some names carry grammar markers such as "[a]" (mostly in German and French data).
            var grammarMarker = new Regex(@"\[[a-z]\]");
            foreach (var pendingVendorSource in pendingVendorSources)
            {
                if (!npcIdsByShopId.TryGetValue(pendingVendorSource.ShopId, out var sellerNpcIds) || sellerNpcIds.Count == 0)
                {
                    var shopName = pendingVendorSource.ShopName.Length > 0 ? pendingVendorSource.ShopName : "Vendor";
                    addSource(pendingVendorSource.ItemId, new ItemSource(pendingVendorSource.Kind, shopName, pendingVendorSource.Cost, null, 0));
                    continue;
                }

                foreach (var sellerNpcId in sellerNpcIds)
                {
                    var sellerName = "";
                    if (npcResidentSheet.TryGetRow(sellerNpcId, out var npcResident))
                    {
                        var npcName = grammarMarker.Replace(npcResident.Singular.ExtractText(), "");
                        if (npcName.Length > 0) npcName = char.ToUpperInvariant(npcName[0]) + npcName[1..];
                        var npcTitle = npcResident.Title.ExtractText();
                        sellerName = npcTitle.Length > 0 ? $"{npcName} ({npcTitle})" : npcName;
                    }
                    if (sellerName.Length == 0) sellerName = pendingVendorSource.ShopName.Length > 0 ? pendingVendorSource.ShopName : "Vendor";
                    addSource(pendingVendorSource.ItemId,
                        new ItemSource(pendingVendorSource.Kind, sellerName, pendingVendorSource.Cost, npcPlaceById.GetValueOrDefault(sellerNpcId), 0));
                }
            }
        });

        // ---------------------------------------------------------------------------------------------------------
        // 6. Crafting. CraftType 0 (Carpenter) is ClassJob 8; the eight crafters follow in the same order.
        // ---------------------------------------------------------------------------------------------------------
        runStep("recipes", () =>
        {
            const uint FirstCrafterClassJobId = 8;
            var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
            foreach (var recipe in dataManager.GetExcelSheet<Recipe>())
            {
                var itemId = recipe.ItemResult.RowId;
                if (!itemIdsToFindSourcesFor.Contains(itemId)) continue;
                var crafterName = classJobSheet.TryGetRow(FirstCrafterClassJobId + recipe.CraftType.RowId, out var classJob) ? classJob.Name.ExtractText() : "";
                // Job names are lower case in the English data ("weaver").
                crafterName = crafterName.Length > 0 ? char.ToUpperInvariant(crafterName[0]) + crafterName[1..] : "Crafting";
                var recipeLevel = recipe.RecipeLevelTable.IsValid ? recipe.RecipeLevelTable.Value.ClassJobLevel : 0;
                addSource(itemId, new ItemSource(ItemSourceKind.Crafting, crafterName, $"Lv. {recipeLevel}", null, recipe.RowId));
            }
        });

        // Vendors first (in ItemSourceKind order: gil, currency, Grand Company), crafting last; vendors with a known place before unknown ones.
        foreach (var itemSources in sourcesByItemId.Values)
            itemSources.Sort((first, second) => first.Kind != second.Kind
                ? first.Kind.CompareTo(second.Kind)
                : (second.Place != null).CompareTo(first.Place != null));

        log.Information($"Item sources found for {sourcesByItemId.Count} items");
    }
}
