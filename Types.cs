using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace CopyGlamour;

/// <summary>
/// Glamourer's equipment slots, same numbers as Glamourer.Api's ApiEquipSlot.
/// Copied here so the plugin talks to Glamourer over IPC without referencing its API package.
/// </summary>
public enum GlamourerSlot : byte
{
    MainHand = 1,
    OffHand = 2,
    Head = 3,
    Body = 4,
    Hands = 5,
    Legs = 7,
    Feet = 8,
    Ears = 9,
    Neck = 10,
    Wrists = 11,
    RightRing = 12,
    LeftRing = 14,
}

/// <summary>Glamourer's result codes, same numbers as Glamourer.Api's GlamourerApiEc.</summary>
public enum GlamourerResult
{
    Success = 0,
    NothingDone = 1,
    ActorNotFound = 2,
    ActorNotHuman = 3,
    DesignNotFound = 4,
    ItemInvalid = 5,
    InvalidKey = 6,
    InvalidState = 7,
    CouldNotParse = 8,
}

/// <summary>One row of the glamour table: a gear piece, what the game data says about it, and what happened when applied.</summary>
public sealed class PieceRow
{
    public required GearPiece Piece { get; init; }

    /// <summary>The game's item ID found for the piece's name; 0 if no wearable item has that name (or it's facewear).</summary>
    public uint ItemId { get; init; }

    /// <summary>
    /// For facewear (glasses): the Glasses row of the chosen colour, applied with Glamourer's facewear call. 0 for normal gear.
    /// </summary>
    public uint FacewearId { get; init; }

    /// <summary>
    /// The item whose vendors, recipes and database links the table shows: the gear item itself, or for facewear the
    /// "The Faces We Wear - …" item that unlocks it. 0 if there is none.
    /// </summary>
    public uint SourceItemId { get; init; }

    public GlamourerSlot? Slot { get; init; }

    /// <summary>The game's stain (dye) IDs for the piece's two dye channels; 0 = undyed.</summary>
    public IReadOnlyList<byte> StainIds { get; init; } = [0, 0];

    /// <summary>Dye names from the page that match no dye in the game data; their channel stays undyed.</summary>
    public IReadOnlyList<string> UnknownDyeNames { get; init; } = [];

    /// <summary>Who can wear the item according to the game data, e.g. "SAM" or "All Classes".</summary>
    public string JobCategoryName { get; init; } = "";

    /// <summary>Found in the game data with somewhere Glamourer can put it.</summary>
    public bool CanApply => (ItemId != 0 && Slot != null) || FacewearId != 0;

    /// <summary>Ticked in the table: applied by "Apply selected".</summary>
    public bool Selected { get; set; } = true;

    public bool Applied { get; set; }

    /// <summary>Something went wrong with this piece (not found, no slot, unknown dye, Glamourer refused): its message shows in red.</summary>
    public bool HasProblem { get; set; }

    /// <summary>A problem found while reading the piece, or the result of applying it, in plain words.</summary>
    public string Message { get; set; } = "";
}

public enum ItemSourceKind
{
    GilVendor,
    CurrencyVendor,
    GrandCompanyVendor,
    Crafting,
}

/// <summary>Where an NPC stands: the zone (territory + map) and the map coordinates the game shows, e.g. (10.5, 9.8).</summary>
public sealed record NpcPlace(uint TerritoryId, uint MapId, float MapX, float MapY, string PlaceName);

/// <summary>
/// One way to get an item.
/// Vendors: Seller is the NPC ("Name (Title)") or the shop's name when no NPC is known, Cost what it costs, Place where the NPC stands (null if unknown).
/// Crafting: Seller is the crafting job, Cost the recipe level, RecipeId the recipe to open.
/// </summary>
public sealed record ItemSource(ItemSourceKind Kind, string Seller, string Cost, NpcPlace? Place, uint RecipeId);

// The settings file stores this class's name, and those of the classes below and of CopiedGlamour and GearPiece.
// Don't rename or move them: players' files would no longer load, and the plugin would start over with empty settings.
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Load a glamour page as soon as it's copied, while the window is open.</summary>
    public bool LoadFromClipboardAutomatically { get; set; } = true;

    /// <summary>Most recent first.</summary>
    public List<CopiedGlamour> RecentGlamours { get; set; } = [];

    /// <summary>What each retainer held when last opened; the game only knows the retainer you have open.</summary>
    public List<SavedRetainer> Retainers { get; set; } = [];

    /// <summary>What each character's glamour dresser and armoire held when last opened; the game forgets them once closed.</summary>
    public List<SavedCharacterStorage> CharacterStorages { get; set; } = [];
}

// Stored by name in the settings file: don't rename or move (see Configuration).
[Serializable]
public sealed class SavedCharacterStorage
{
    public ulong OwnerContentId { get; set; }

    /// <summary>Null until the dresser is opened on this character. Outfit set pieces are included.</summary>
    public List<uint>? DresserItemIds { get; set; }

    /// <summary>Null until the armoire is opened on this character.</summary>
    public List<uint>? ArmoireItemIds { get; set; }
}

// Stored by name in the settings file: don't rename or move (see Configuration).
[Serializable]
public sealed class SavedRetainer
{
    public ulong RetainerId { get; set; }

    /// <summary>The character the retainer belongs to, so another character's retainers aren't listed.</summary>
    public ulong OwnerContentId { get; set; }

    public string Name { get; set; } = "";

    public List<uint> ItemIds { get; set; } = [];
}
