using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace CopyGlamour;

// GearPiece and CopiedGlamour are stored by name in the settings file (the recent list): don't rename or move them.

/// <summary>One gear entry as Eorzea Collection lists it: its slot label, item name and dye names (as written on the page).</summary>
public sealed record GearPiece(string SlotLabel, string ItemName, IReadOnlyList<string> DyeNames);

/// <summary>One glamour as read from an Eorzea Collection page. Title, Creator and IntendedFor are "" when the page doesn't show them.</summary>
public sealed record CopiedGlamour(string Title, string Creator, string IntendedFor, IReadOnlyList<GearPiece> Pieces);

/// <summary>
/// Reads the gear from an Eorzea Collection glamour page. No game or Dalamud types here, so Tests/ can run it on its own.
/// </summary>
public static class GlamourPageParser
{
    // The text right before each slot label ("HEAD", "BODY", ...) in an Eorzea Collection glamour page's HTML.
    private const string EorzeaCollectionSlotMarker = "gear-icon-box-slot-name is-family-secondary\">";

    /// <summary>
    /// Parses either the page's copied text (Ctrl+A, Ctrl+C in the browser) or the HTML of a page saved with Ctrl+S.
    /// Throws with a plain-words message if the page has no gear on it.
    /// </summary>
    public static CopiedGlamour Parse(string pageContent)
    {
        var gearPieces = new List<GearPiece>();
        string glamourTitle, glamourCreator, glamourIntendedFor;
        if (pageContent.Contains(EorzeaCollectionSlotMarker))
        {
            // Page HTML (saved file). The page's <title> is "010. | Eorzea Collection"; the creator is the
            // first <h3 class="title is-5">; "Intended for" is a label followed by a tag with the job.
            var readMatch = (Match match) => match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : "";
            glamourTitle = readMatch(Regex.Match(pageContent, "<title>(.*?) \\| Eorzea Collection</title>", RegexOptions.Singleline));
            glamourCreator = readMatch(Regex.Match(pageContent, "<h3 class=\"title is-5\">([^<]*)</h3>"));
            glamourIntendedFor = readMatch(Regex.Match(pageContent,
                "Intended for</span>.*?<span class=\"tag ml-1 is-background-dark\">([^<]*)</span>", RegexOptions.Singleline));

            // Each slot starts with the slot marker, so splitting on it gives one block per slot.
            // In each block: the item name is the text inside list-item-title, and the dyes are the small tags
            // inside list-item-description (each tag is "⬤ Jet Black" or "◯ Undyed").
            foreach (var slotBlock in pageContent.Split(EorzeaCollectionSlotMarker).Skip(1))
            {
                var slotLabel = slotBlock[..Math.Max(slotBlock.IndexOf('<'), 0)].Trim();

                // \s* between tags: a full browser save packs the tags together, an "HTML only" save keeps the site's line breaks and indentation.
                // The <a> around the name is optional: items without a Lodestone database entry have no link.
                // If the title can't be read, the item icon's alt text holds the same name.
                // A piece whose name can't be read at all is still kept (empty name), so it shows up in the table instead of vanishing.
                var itemNameMatch = Regex.Match(slotBlock, "list-item-title\">\\s*(?:<a[^>]*>\\s*)?<span[^>]*>([^<]*)</span>");
                if (!itemNameMatch.Success) itemNameMatch = Regex.Match(slotBlock, "gear-icon-image image\"[^>]*alt=\"([^\"]*)\"");
                var itemName = readMatch(itemNameMatch);

                var dyeSectionStart = slotBlock.IndexOf("list-item-description", StringComparison.Ordinal);
                var dyeNames = new List<string>();
                if (dyeSectionStart >= 0)
                {
                    var dyeSectionEnd = slotBlock.IndexOf("list-item-controls", dyeSectionStart, StringComparison.Ordinal);
                    if (dyeSectionEnd < 0) dyeSectionEnd = slotBlock.Length;
                    var dyeSection = slotBlock[dyeSectionStart..dyeSectionEnd];
                    foreach (Match dyeTagMatch in Regex.Matches(dyeSection, "is-tiny[^\"]*\">\\s*<span>(.*?)</span>\\s*</div>", RegexOptions.Singleline))
                    {
                        var dyeText = Regex.Replace(dyeTagMatch.Groups[1].Value, "<[^>]+>", "");
                        dyeNames.Add(WebUtility.HtmlDecode(dyeText).Replace("⬤", "").Replace("◯", "").Trim());
                    }
                }

                gearPieces.Add(new GearPiece(slotLabel, itemName, dyeNames));
            }
        }
        else
        {
            // Copied page text. Headings are compared ignoring case, as some browsers copy them in the capitals the site shows on screen.
            var pageLines = pageContent.Split('\n').Select(pageLine => pageLine.Trim()).ToList();

            // Some apps paste links as Markdown, "[Brioso Gilet](https://…)": keep only the text.
            var withoutMarkdownLink = (string pageLine) =>
            {
                var markdownLinkMatch = Regex.Match(pageLine, "^\\[(.*)\\]\\(.*\\)$");
                return markdownLinkMatch.Success ? markdownLinkMatch.Groups[1].Value.Trim() : pageLine;
            };
            // The first two non-empty lines after a heading line such as "Creator" or "Intended for"; empty if the heading isn't there.
            var linesAfterHeading = (string heading) =>
            {
                var headingIndex = pageLines.FindIndex(pageLine => pageLine.Equals(heading, StringComparison.OrdinalIgnoreCase));
                if (headingIndex < 0) return new List<string>();
                return pageLines.Skip(headingIndex + 1).Where(pageLine => pageLine.Length > 0).Take(2).Select(withoutMarkdownLink).ToList();
            };
            var isLikeCount = (string pageLine) => pageLine.Length > 0 && pageLine.All(char.IsDigit);

            // The title is the line above the like count. The count sits above "Tags:" (or "Submitted on:" when there are
            // no tags), with the image captions ("Cover image", "Second image") between them on pages with several images.
            glamourTitle = "";
            var tagsLineIndex = pageLines.FindIndex(pageLine =>
                pageLine.StartsWith("Tags:", StringComparison.OrdinalIgnoreCase) || pageLine.Equals("Submitted on:", StringComparison.OrdinalIgnoreCase));
            var likeCountIndex = tagsLineIndex > 0 ? pageLines.FindLastIndex(tagsLineIndex - 1, pageLine => isLikeCount(pageLine)) : -1;
            for (var lineIndex = (likeCountIndex >= 0 ? likeCountIndex : tagsLineIndex) - 1; lineIndex >= 0; lineIndex--)
            {
                if (pageLines[lineIndex].Length == 0 || isLikeCount(pageLines[lineIndex])) continue;
                glamourTitle = withoutMarkdownLink(pageLines[lineIndex]);
                break;
            }

            // "Kay Lin from «Gilgamesh»": the saved page shows the name alone, so the world is dropped here too.
            glamourCreator = Regex.Replace(linesAfterHeading("Creator").FirstOrDefault() ?? "", @"\s+from\s+[«⧫].*$", "");

            // "AST" then "Astrologian": the saved page shows the full job name, the line after the capitals.
            var intendedForLines = linesAfterHeading("Intended for");
            glamourIntendedFor = intendedForLines.Count switch
            {
                0 => "",
                > 1 when Regex.IsMatch(intendedForLines[0], "^[A-Z]{2,3}$") => intendedForLines[1],
                _ => intendedForLines[0],
            };

            // The gear sits between the "Equipment" line and the next section ("Inspired by", "Creator", ...),
            // in this order per piece: a slot line in capitals ("HEAD"), the item name, then 0–2 dye lines ("⬤ Jet Black", "◯ Undyed").
            string[] sectionsAfterEquipment = ["Inspired by", "Creator", "About this glamour", "Featured in Album"];
            var equipmentLineIndex = pageLines.FindIndex(pageLine => pageLine.Equals("Equipment", StringComparison.OrdinalIgnoreCase));
            if (equipmentLineIndex < 0)
                throw new InvalidOperationException("No \"Equipment\" section in the copied text. Copy the whole glamour page with Ctrl+A, then Ctrl+C.");

            string? currentSlotLabel = null;
            var currentItemName = "";
            var currentDyeNames = new List<string>();
            foreach (var rawPageLine in pageLines.Skip(equipmentLineIndex + 1))
            {
                if (rawPageLine.Length == 0) continue;
                if (sectionsAfterEquipment.Any(sectionName => rawPageLine.Equals(sectionName, StringComparison.OrdinalIgnoreCase))) break;

                var pageLine = withoutMarkdownLink(rawPageLine);
                if (pageLine.Length == 0) continue;

                // Group headings like "— Weapon —" and "— Accessories —".
                if (pageLine.Contains('—')) continue;

                if (pageLine[0] is '⬤' or '◯')
                {
                    // Usually one dye per line, but both dyes can land on one line ("⬤ Jet Black ⬤ Snow White").
                    if (currentSlotLabel != null)
                        foreach (Match dyeMatch in Regex.Matches(pageLine, "[⬤◯]\\s*([^⬤◯]+)"))
                            currentDyeNames.Add(dyeMatch.Groups[1].Value.Trim());
                    continue;
                }

                if (Regex.IsMatch(pageLine, "^[A-Z][A-Z ]*$"))
                {
                    if (currentSlotLabel != null) gearPieces.Add(new GearPiece(currentSlotLabel, currentItemName, currentDyeNames));
                    currentSlotLabel = pageLine;
                    currentItemName = "";
                    currentDyeNames = new List<string>();
                    continue;
                }

                // The first other line after a slot is its item name; later ones (button text like "Gearset") are ignored.
                if (currentSlotLabel != null && currentItemName.Length == 0) currentItemName = pageLine;
            }

            if (currentSlotLabel != null) gearPieces.Add(new GearPiece(currentSlotLabel, currentItemName, currentDyeNames));
        }

        if (gearPieces.Count == 0)
            throw new InvalidOperationException(
                "No gear found on that page. If it opens fine in your browser, the site may have changed its layout.");

        return new CopiedGlamour(glamourTitle, glamourCreator, glamourIntendedFor, gearPieces);
    }
}
