using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace TierListOverlay;

/// <param name="Author">Who rated the item, e.g. "Baalorlord".</param>
/// <param name="GoodUpgrade">Whether upgrading this card is worth it.</param>
/// <param name="MaxCopies">How many copies the deck wants at most, if limited.</param>
public sealed record TierEntry(string Tier, string Color, string Commentary, string Author, bool GoodUpgrade, int? MaxCopies);

/// <summary>
/// Loads tier lists from the public Untapped.gg API, caching them next to the mod DLL so the
/// overlay also works offline.
/// </summary>
internal static class TierLists
{
    // Baalorlord's lists, one per character plus colorless.
    private static readonly (string Name, string Uuid)[] Lists =
    {
        ("ironclad", "004de170-026a-4dd4-a280-3b904be0b5d6"),
        ("silent", "6d61ea21-0552-4c49-8bb5-a5c15530fc00"),
        ("regent", "0e6c1e23-bec6-4887-a9e0-dbf49ede974d"),
        ("necrobinder", "43d0b41f-7d6d-4ce9-928e-c1310a413983"),
        ("defect", "5a512e04-4583-4a16-9271-d46864c6cb4c"),
        ("colorless", "0a626c22-dc49-433e-ac6e-76cc9abf5684"),
    };

    private const string ApiUrl = "https://api.sts2.untapped.gg/api/v1/tier-lists/{0}/resolve/?game_version=public";

    private const string Colorless = "colorless";

    // List name -> item ID -> entry. Lists rate items per character (e.g. a colorless card can
    // be S for Ironclad but A in general), so they are kept apart instead of merged.
    private static volatile Dictionary<string, Dictionary<string, TierEntry>> _lists = new();

    /// <summary>
    /// Looks an item up: the current character's list wins over the colorless list.
    /// </summary>
    public static bool TryGet(string? characterId, string itemId, out TierEntry entry)
    {
        var lists = _lists;
        if (characterId != null && lists.TryGetValue(characterId.ToLowerInvariant(), out var own) && own.TryGetValue(itemId, out entry!))
            return true;
        if (lists.TryGetValue(Colorless, out var colorless) && colorless.TryGetValue(itemId, out entry!))
            return true;
        entry = null!;
        return false;
    }

    public static void LoadInBackground(string cacheDir)
    {
        Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var lists = new Dictionary<string, Dictionary<string, TierEntry>>();
                foreach (var (name, uuid) in Lists)
                {
                    // Not .json: the game treats every .json file below mods/ as a mod manifest.
                    var cachePath = Path.Combine(cacheDir, $"{name}.tierlist");
                    string? json = null;
                    try
                    {
                        json = await http.GetStringAsync(string.Format(ApiUrl, uuid));
                        await File.WriteAllTextAsync(cachePath, json);
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[TierListOverlay] Fetching {name} failed ({ex.Message}), using cache");
                        if (File.Exists(cachePath))
                            json = await File.ReadAllTextAsync(cachePath);
                    }
                    if (json != null)
                        lists[name] = Parse(json);
                }
                _lists = lists;
                GD.Print($"[TierListOverlay] Loaded {lists.Count} tier lists with {lists.Values.Sum(l => l.Count)} entries");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Loading tier lists failed: {ex}");
            }
        });
    }

    private static Dictionary<string, TierEntry> Parse(string json)
    {
        var into = new Dictionary<string, TierEntry>();
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("tier_list");
        var author = list.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("display_name", out var dn)
            ? dn.GetString() ?? "" : "";
        foreach (var tier in list.GetProperty("tiers").EnumerateArray())
        {
            var name = tier.GetProperty("name").GetString() ?? "?";
            var color = tier.TryGetProperty("color", out var c) ? c.GetString() ?? "#808080" : "#808080";
            foreach (var group in new[] { "cards", "relics", "potions" })
            {
                if (!tier.TryGetProperty(group, out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("item_id").GetString();
                    if (id == null)
                        continue;
                    var commentary = item.TryGetProperty("commentary", out var cm) ? cm.GetString() ?? "" : "";
                    var goodUpgrade = item.TryGetProperty("good_upgrade", out var gu) && gu.ValueKind == JsonValueKind.True;
                    int? maxCopies = item.TryGetProperty("max_copies", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : null;
                    into[id] = new TierEntry(name, color, commentary, author, goodUpgrade, maxCopies);
                }
            }
        }
        return into;
    }
}
