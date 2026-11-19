// Self-checks for GlamourPageParser and the recent-glamour settings round trip. Exit code 1 on the first failure.
// Run from the plugin folder: dotnet run --project Tests
// Real pages: drop saved pages (.html) or copied text (.txt) into Tests/Fixtures/; each must parse with every piece named.
using CopyGlamour;
using Newtonsoft.Json;

static void Check(bool condition, string what)
{
    if (condition) return;
    Console.Error.WriteLine($"FAIL: {what}");
    Environment.Exit(1);
}

static void CheckPieces(CopiedGlamour glamour, string format, params (string Slot, string Item, string[] Dyes)[] expected)
{
    Check(glamour.Pieces.Count == expected.Length, $"{format}: {glamour.Pieces.Count} pieces, expected {expected.Length}");
    for (var i = 0; i < expected.Length; i++)
    {
        var piece = glamour.Pieces[i];
        Check(piece.SlotLabel == expected[i].Slot, $"{format}: piece {i} slot \"{piece.SlotLabel}\", expected \"{expected[i].Slot}\"");
        Check(piece.ItemName == expected[i].Item, $"{format}: piece {i} item \"{piece.ItemName}\", expected \"{expected[i].Item}\"");
        Check(piece.DyeNames.SequenceEqual(expected[i].Dyes),
            $"{format}: piece {i} dyes [{string.Join(", ", piece.DyeNames)}], expected [{string.Join(", ", expected[i].Dyes)}]");
    }
}

// ---- Copied text (Ctrl+A, Ctrl+C). Covers: Markdown links, a group heading, both dyes on one line, button text after a name,
// and what real page 358808 showed: image captions under the like count, the creator's world, a job abbreviation line. ----
var copiedText = """
    Eorzea Collection
    Sunset Samurai
    42

    Cover image
    Second image
    Tags: Casual, Red
    Equipment
    — Weapon —
    MAIN HAND
    [Kotetsu](https://na.finalfantasyxiv.com/lodestone/playguide/db/item/x/)
    ⬤ Jet Black
    — Armor —
    HEAD
    Kasa
    ◯ Undyed
    BODY
    Brioso Gilet
    ⬤ Jet Black ⬤ Snow White
    Gearset
    Inspired by
    Someone else
    Creator
    [Ignes from «Gilgamesh»](https://ffxiv.eorzeacollection.com/author/1)
    Ignes
    ⧫ Gilgamesh
    Intended for
    SAM
    Samurai
    Level to Equip
    """.Replace("\r", "");
var fromText = GlamourPageParser.Parse(copiedText);
Check(fromText.Title == "Sunset Samurai", $"text: title \"{fromText.Title}\"");
Check(fromText.Creator == "Ignes", $"text: creator \"{fromText.Creator}\"");
Check(fromText.IntendedFor == "Samurai", $"text: intended for \"{fromText.IntendedFor}\"");
CheckPieces(fromText, "text",
    ("MAIN HAND", "Kotetsu", ["Jet Black"]),
    ("HEAD", "Kasa", ["Undyed"]),
    ("BODY", "Brioso Gilet", ["Jet Black", "Snow White"]));

// ---- Saved page HTML (Ctrl+S). Covers: linked name, HTML entity in a dye, name only in the icon's alt text. ----
var savedHtml = """
    <html><head><title>010. | Eorzea Collection</title></head><body>
    <h3 class="title is-5">Ignes</h3>
    <span>Intended for</span> <span class="tag ml-1 is-background-dark">Samurai</span>
    <div class="gear-icon-box-slot-name is-family-secondary">HEAD</div>
    <div class="list-item-title"><a href="https://x"><span class="name">Kasa</span></a></div>
    <div class="list-item-description">
      <div class="tag is-tiny is-dark"><span>&#11044; Jet Black</span></div>
      <div class="tag is-tiny"><span>◯ Undyed</span></div>
    </div>
    <div class="list-item-controls">Gearset</div>
    <div class="gear-icon-box-slot-name is-family-secondary">BODY</div>
    <img class="gear-icon-image image" src="x.png" alt="Brioso Gilet">
    </body></html>
    """;
var fromHtml = GlamourPageParser.Parse(savedHtml);
Check(fromHtml.Title == "010.", $"html: title \"{fromHtml.Title}\"");
Check(fromHtml.Creator == "Ignes", $"html: creator \"{fromHtml.Creator}\"");
Check(fromHtml.IntendedFor == "Samurai", $"html: intended for \"{fromHtml.IntendedFor}\"");
CheckPieces(fromHtml, "html",
    ("HEAD", "Kasa", ["Jet Black", "Undyed"]),
    ("BODY", "Brioso Gilet", []));

// ---- Pages without gear say so instead of returning an empty glamour. ----
var threw = false;
try { GlamourPageParser.Parse("Eorzea Collection\nno gear here"); } catch (InvalidOperationException) { threw = true; }
Check(threw, "text without an Equipment section must throw");

// ---- The recent list survives a save and load with the settings Dalamud uses (PluginConfigurations.SerializeConfig). ----
var dalamudSettings = new JsonSerializerSettings
{
    TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
    TypeNameHandling = TypeNameHandling.Objects,
};
var savedJson = JsonConvert.SerializeObject(new List<CopiedGlamour> { fromText, fromHtml }, Formatting.Indented, dalamudSettings);
var reloaded = JsonConvert.DeserializeObject<List<CopiedGlamour>>(savedJson, dalamudSettings)!;
Check(reloaded.Count == 2 && reloaded[0].Title == fromText.Title && reloaded[0].Creator == fromText.Creator, "settings: title and creator survive");
CheckPieces(reloaded[0], "settings",
    ("MAIN HAND", "Kotetsu", ["Jet Black"]),
    ("HEAD", "Kasa", ["Undyed"]),
    ("BODY", "Brioso Gilet", ["Jet Black", "Snow White"]));

// ---- Real pages, if any were dropped in. ----
// The same page saved as <name>.html and copied as <name>.txt must give the same glamour from both readers.
var fixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var realPages = Directory.Exists(fixtureFolder) ? Directory.GetFiles(fixtureFolder) : [];
var glamourByPage = new Dictionary<string, CopiedGlamour>();
foreach (var realPage in realPages)
{
    var pageName = Path.GetFileName(realPage);
    var glamour = GlamourPageParser.Parse(File.ReadAllText(realPage).Replace("\r", ""));
    Check(glamour.Pieces.All(piece => piece.ItemName.Length > 0), $"{pageName}: a piece has no name");
    Console.WriteLine($"{pageName}: \"{glamour.Title}\" by {glamour.Creator}, for {glamour.IntendedFor}: " +
                      string.Join("; ", glamour.Pieces.Select(piece => $"{piece.SlotLabel} {piece.ItemName} [{string.Join(", ", piece.DyeNames)}]")));

    var stem = Path.GetFileNameWithoutExtension(realPage);
    if (!glamourByPage.TryGetValue(stem, out var otherCopy)) { glamourByPage[stem] = glamour; continue; }
    Check(glamour.Title == otherCopy.Title, $"{stem}: title \"{glamour.Title}\" vs \"{otherCopy.Title}\"");
    Check(glamour.Creator == otherCopy.Creator, $"{stem}: creator \"{glamour.Creator}\" vs \"{otherCopy.Creator}\"");
    Check(glamour.IntendedFor == otherCopy.IntendedFor, $"{stem}: intended for \"{glamour.IntendedFor}\" vs \"{otherCopy.IntendedFor}\"");
    CheckPieces(glamour, $"{stem} (text vs html)",
        otherCopy.Pieces.Select(piece => (piece.SlotLabel, piece.ItemName, piece.DyeNames.ToArray())).ToArray());
}

Console.WriteLine($"All checks passed ({realPages.Length} real pages).");
