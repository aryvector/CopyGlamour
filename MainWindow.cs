using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CopyGlamour;

public sealed class MainWindow : Window
{
    // Green marks a piece that was applied, red one that wasn't, grey a hint that isn't a result yet (e.g. "Weapon for SAM").
    private static readonly Vector4 AppliedColor = new(0.3f, 0.9f, 0.3f, 1f);
    private static readonly Vector4 FailedColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 HintColor = new(0.7f, 0.7f, 0.7f, 1f);

    // Long enough for any Windows file path.
    private const int SavedPagePathMaxLength = 1024;

    // Plenty for "title by creator".
    private const int DesignNameMaxLength = 256;

    // How often the clipboard is checked for a copied glamour page: often enough to feel instant, rare enough to cost nothing.
    private const long ClipboardCheckIntervalMilliseconds = 1000;

    // Sources listed per piece; the rest move into a tooltip so an item sold by many NPCs doesn't stretch the table.
    private const int SourcesShownPerPiece = 3;

    // Only text that has both of these can be an Eorzea Collection glamour page (the footer and the gear section).
    private const string EorzeaCollectionPageSign = "Eorzea Collection";
    private const string EquipmentSectionSign = "Equipment";

    // Room for the six table columns at a readable width.
    private static readonly Vector2 MinimumWindowSize = new(760, 300);

    // Room kept right of the path box for its Load button.
    private const float LoadButtonWidth = 80f;

    // Wide enough to show a typical "title by creator" name in full.
    private const float DesignNameInputWidth = 300f;

    // Column widths: the tick box and slot are fixed, the rest share the window by these weights.
    private const float UseColumnWidth = 30f;
    private const float SlotColumnWidth = 80f;
    private const float ItemColumnWeight = 2f;
    private const float DyesColumnWeight = 1.5f;
    private const float WhereToGetColumnWeight = 4f;
    private const float ResultColumnWeight = 2f;

    private readonly Plugin plugin;
    private string savedPagePath = "";
    private string designName = "";
    private string statusMessage = "";
    private bool isBusy;
    private CopiedGlamour? currentGlamour;
    private List<PieceRow> currentPieceRows = [];
    private string lastSeenClipboardText = "";
    private long nextClipboardCheckTime;

    public MainWindow(Plugin plugin)
        // The version in the title shows at a glance which build the game has loaded.
        : base($"Copy Glamour v{typeof(MainWindow).Assembly.GetName().Version}##CopyGlamourMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = MinimumWindowSize, MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    /// <summary>
    /// Reads a glamour page (copied text or a saved page's path) and fills the table, in the background so the game doesn't freeze.
    /// Nothing is applied yet: that's the "Apply selected" button.
    /// </summary>
    public void StartLoad(string glamourSource)
    {
        if (isBusy) return;
        // Show a path in the box; copied page text is many lines and stays out of it.
        if (!glamourSource.Contains('\n')) savedPagePath = glamourSource;
        RunBusy("Reading the glamour page…", async () =>
        {
            var glamour = await plugin.LoadGlamour(glamourSource);
            return ShowGlamour(glamour, await plugin.ResolveGlamour(glamour));
        });
    }

    /// <summary>
    /// Runs one background job (load, apply, revert, save) with the buttons disabled, and shows the message it returns.
    /// Any exception goes to the status line and the log, and the buttons always come back.
    /// </summary>
    private void RunBusy(string busyText, Func<Task<string>> work)
    {
        if (isBusy) return;
        isBusy = true;
        statusMessage = busyText;
        Task.Run(async () =>
        {
            try
            {
                statusMessage = await work();
            }
            catch (Exception exception)
            {
                Plugin.Log.Warning(exception, $"Failed: {busyText}");
                statusMessage = exception.Message;
            }
            finally
            {
                isBusy = false;
            }
        });
    }

    private string ShowGlamour(CopiedGlamour glamour, List<PieceRow> pieceRows)
    {
        currentGlamour = glamour;
        currentPieceRows = pieceRows;
        designName = DisplayName(glamour);
        return $"Loaded {pieceRows.Count} pieces. Untick any you don't want, then click Apply selected.";
    }

    /// <summary>"Title by Creator", with "Copied glamour" when the page had no title.</summary>
    private static string DisplayName(CopiedGlamour glamour)
    {
        var glamourTitle = glamour.Title.Length > 0 ? glamour.Title : "Copied glamour";
        return glamour.Creator.Length > 0 ? $"{glamourTitle} by {glamour.Creator}" : glamourTitle;
    }

    public override void Draw()
    {
        // Auto-load: when a different text lands on the clipboard and it's a glamour page, load it (not apply).
        if (plugin.Configuration.LoadFromClipboardAutomatically && !isBusy && Environment.TickCount64 >= nextClipboardCheckTime)
        {
            nextClipboardCheckTime = Environment.TickCount64 + ClipboardCheckIntervalMilliseconds;
            var clipboardText = ImGui.GetClipboardText() ?? "";
            if (clipboardText != lastSeenClipboardText)
            {
                lastSeenClipboardText = clipboardText;
                if (clipboardText.Contains(EorzeaCollectionPageSign) && clipboardText.Contains(EquipmentSectionSign)) StartLoad(clipboardText);
            }
        }

        // ---- Loading a glamour ----
        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Paste from clipboard"))
        {
            var clipboardText = ImGui.GetClipboardText() ?? "";
            lastSeenClipboardText = clipboardText;
            StartLoad(clipboardText);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("(Ctrl+A, Ctrl+C on the glamour page first)");
        ImGui.SameLine();
        // "###openGlamourer" keeps the button's ID the same while its label changes.
        var openGlamourerLabel = plugin.SavedDesignId != Guid.Empty ? "Open saved design in Glamourer###openGlamourer" : "Open Glamourer###openGlamourer";
        if (ImGui.Button(openGlamourerLabel) && !plugin.OpenGlamourer())
            statusMessage = "Couldn't open Glamourer: it isn't installed or isn't enabled.";

        var loadFromClipboardAutomatically = plugin.Configuration.LoadFromClipboardAutomatically;
        if (ImGui.Checkbox("Load automatically when I copy a glamour page (while this window is open)", ref loadFromClipboardAutomatically))
        {
            plugin.Configuration.LoadFromClipboardAutomatically = loadFromClipboardAutomatically;
            Plugin.PluginInterface.SavePluginConfig(plugin.Configuration);
        }

        ImGui.TextUnformatted("Or the path of a page saved with Ctrl+S:");
        ImGui.SetNextItemWidth(-LoadButtonWidth);
        var pressedEnter = ImGui.InputText("##savedPagePath", ref savedPagePath, SavedPagePathMaxLength, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.BeginDisabled(isBusy || savedPagePath.Trim().Length == 0);
        if (ImGui.Button("Load") || (pressedEnter && savedPagePath.Trim().Length > 0)) StartLoad(savedPagePath.Trim());
        ImGui.EndDisabled();

        if (statusMessage.Length > 0) ImGui.TextWrapped(statusMessage);

        // ---- The loaded glamour ----
        var glamour = currentGlamour;
        var pieceRows = currentPieceRows;
        if (glamour != null && pieceRows.Count > 0)
        {
            ImGui.Separator();
            var glamourHeading = DisplayName(glamour);
            if (glamour.IntendedFor.Length > 0) glamourHeading += $"  ·  Intended for: {glamour.IntendedFor}";
            ImGui.TextUnformatted(glamourHeading);

            // Buttons first, table below: the table can get long, and the buttons stay in reach without scrolling.
            ImGui.BeginDisabled(isBusy || !pieceRows.Any(pieceRow => pieceRow.Selected));
            if (ImGui.Button("Apply selected"))
                RunBusy("Applying through Glamourer…", async () =>
                {
                    var appliedCount = await plugin.ApplyGlamour(pieceRows);
                    return $"Applied {appliedCount} of {pieceRows.Count(pieceRow => pieceRow.Selected)} selected pieces.";
                });
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Select all"))
                foreach (var pieceRow in pieceRows.Where(pieceRow => pieceRow.CanApply)) pieceRow.Selected = true;
            ImGui.SameLine();
            if (ImGui.Button("Select none"))
                foreach (var pieceRow in pieceRows) pieceRow.Selected = false;

            ImGui.SameLine();
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button("Revert")) RunBusy("Reverting…", plugin.RevertGlamour);
            ImGui.EndDisabled();

            ImGui.TextUnformatted("Glamourer design name:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(DesignNameInputWidth);
            ImGui.InputText("##designName", ref designName, DesignNameMaxLength);
            ImGui.SameLine();
            ImGui.BeginDisabled(isBusy || designName.Trim().Length == 0);
            if (ImGui.Button("Save as Glamourer design"))
            {
                var nameToSave = designName.Trim();
                RunBusy("Saving the design…", () => plugin.SaveAsGlamourerDesign(nameToSave));
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Saves everything you're wearing right now (as Glamourer shows it), so apply the glamour first.");

            DrawPieceTable(pieceRows);
        }

        // ---- Recent glamours ----
        var recentGlamours = plugin.Configuration.RecentGlamours.ToArray();
        if (recentGlamours.Length > 0 && ImGui.CollapsingHeader($"Recent glamours ({recentGlamours.Length})###recentGlamours"))
        {
            for (var recentIndex = 0; recentIndex < recentGlamours.Length; recentIndex++)
            {
                var recentGlamour = recentGlamours[recentIndex];
                ImGui.PushID(recentIndex);
                ImGui.BeginDisabled(isBusy);
                // A recent glamour is already parsed: only the game data lookup runs again.
                if (ImGui.SmallButton("Load")) RunBusy("Loading…", async () => ShowGlamour(recentGlamour, await plugin.ResolveGlamour(recentGlamour)));
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextUnformatted(DisplayName(recentGlamour));
                ImGui.SameLine();
                ImGui.TextDisabled($"({recentGlamour.Pieces.Count} pieces)");
                ImGui.PopID();
            }
        }
    }

    private void DrawPieceTable(List<PieceRow> pieceRows)
    {
        if (!ImGui.BeginTable("##pieceRows", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, UseColumnWidth);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, SlotColumnWidth);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, ItemColumnWeight);
        ImGui.TableSetupColumn("Dyes", ImGuiTableColumnFlags.WidthStretch, DyesColumnWeight);
        ImGui.TableSetupColumn("Where to get", ImGuiTableColumnFlags.WidthStretch, WhereToGetColumnWeight);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, ResultColumnWeight);
        ImGui.TableHeadersRow();

        var itemSources = plugin.ItemSources;

        // One line of text per source, e.g. "Vendor: Engerrand (Apparel & Item Vendor), Ul'dah - Steps of Nald (10.5, 9.8), 1,200 gil".
        var describeSource = (ItemSource itemSource) =>
        {
            if (itemSource.Kind == ItemSourceKind.Crafting) return $"Crafted: {itemSource.Seller} {itemSource.Cost}";
            var sourceKindLabel = itemSource.Kind switch
            {
                ItemSourceKind.GilVendor => "Vendor",
                ItemSourceKind.CurrencyVendor => "Exchange",
                ItemSourceKind.GrandCompanyVendor => "Grand Company",
                _ => "Vendor",
            };
            var placeText = itemSource.Place == null
                ? "location unknown"
                : $"{itemSource.Place.PlaceName} ({itemSource.Place.MapX:0.0}, {itemSource.Place.MapY:0.0})";
            return $"{sourceKindLabel}: {itemSource.Seller}, {placeText}, {itemSource.Cost}";
        };

        for (var rowIndex = 0; rowIndex < pieceRows.Count; rowIndex++)
        {
            var pieceRow = pieceRows[rowIndex];
            ImGui.PushID(rowIndex);
            ImGui.TableNextRow();

            // Use
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!pieceRow.CanApply);
            var isSelected = pieceRow.Selected;
            if (ImGui.Checkbox("##selected", ref isSelected)) pieceRow.Selected = isSelected;
            ImGui.EndDisabled();

            // Slot, Item, Dyes
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(pieceRow.Piece.SlotLabel);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(pieceRow.Piece.ItemName.Length > 0 ? pieceRow.Piece.ItemName : "?");
            ImGui.TableNextColumn();
            ImGui.TextWrapped(pieceRow.Piece.DyeNames.Count > 0 ? string.Join(" / ", pieceRow.Piece.DyeNames) : "–");

            // Where to get: up to SourcesShownPerPiece lines, each with a Map or Recipe button, then links to item databases.
            ImGui.TableNextColumn();
            if (pieceRow.SourceItemId == 0)
            {
                ImGui.TextDisabled("–");
            }
            else if (itemSources == null)
            {
                ImGui.TextDisabled("Finding sources…");
            }
            else
            {
                var sourcesForItem = itemSources.SourcesFor(pieceRow.SourceItemId);
                if (sourcesForItem.Count == 0)
                    ImGui.TextWrapped("Not sold by any vendor or crafted: a drop, quest, achievement or Mog Station item. See the links below.");

                for (var sourceIndex = 0; sourceIndex < sourcesForItem.Count && sourceIndex < SourcesShownPerPiece; sourceIndex++)
                {
                    var itemSource = sourcesForItem[sourceIndex];
                    ImGui.PushID(sourceIndex);
                    if (itemSource.Place != null)
                    {
                        // Opens the map with a flag on the NPC.
                        if (ImGui.SmallButton("Map"))
                            Plugin.GameGui.OpenMapWithMapLink(new MapLinkPayload(itemSource.Place.TerritoryId, itemSource.Place.MapId, itemSource.Place.MapX, itemSource.Place.MapY));
                        ImGui.SameLine();
                    }
                    else if (itemSource.Kind == ItemSourceKind.Crafting && itemSource.RecipeId != 0)
                    {
                        // Opens the crafting log at this recipe.
                        // unsafe only here: the crafting log is reached through a game pointer, and C# doesn't allow
                        // await anywhere in an unsafe class, which the loading and applying code needs.
                        if (ImGui.SmallButton("Recipe"))
                        {
                            unsafe
                            {
                                var recipeNote = AgentRecipeNote.Instance();
                                if (recipeNote != null) recipeNote->OpenRecipeByRecipeId(itemSource.RecipeId);
                            }
                        }
                        ImGui.SameLine();
                    }
                    ImGui.TextWrapped(describeSource(itemSource));
                    ImGui.PopID();
                }

                if (sourcesForItem.Count > SourcesShownPerPiece)
                {
                    ImGui.TextDisabled($"+{sourcesForItem.Count - SourcesShownPerPiece} more (hover to see)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join("\n", sourcesForItem.Skip(SourcesShownPerPiece).Select(describeSource)));
                }

                // Item databases list every source, including duty drops, quests and the Mog Station.
                if (ImGui.SmallButton("Garland Tools")) Util.OpenLink($"https://www.garlandtools.org/db/#item/{pieceRow.SourceItemId}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Teamcraft")) Util.OpenLink($"https://ffxivteamcraft.com/db/en/item/{pieceRow.SourceItemId}");
            }

            // Result
            ImGui.TableNextColumn();
            if (pieceRow.Message.Length > 0)
            {
                var resultColor = pieceRow.Applied ? AppliedColor : pieceRow.HasProblem ? FailedColor : HintColor;
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(resultColor, pieceRow.Message);
                ImGui.PopTextWrapPos();
            }

            ImGui.PopID();
        }
        ImGui.EndTable();
    }
}
