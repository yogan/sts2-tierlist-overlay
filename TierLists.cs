using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace TierListOverlay;

/// <param name="Author">Who rated the item, e.g. "Baalorlord".</param>
/// <param name="GoodUpgrade">Whether upgrading this card is worth it.</param>
/// <param name="MaxCopies">How many copies the deck wants at most, if limited.</param>
/// <param name="Kind">"cards", "relics" or "potions", as the list groups them.</param>
public sealed record TierEntry(string Tier, string Color, string Commentary, string Author, bool GoodUpgrade, int? MaxCopies, string Kind)
{
    /// <summary>The character whose list rated a card of theirs, when it is not the player's (e.g. "defect").</summary>
    public string? RatedFor { get; init; }
}

/// <summary>
/// Loads tier lists from the public Untapped.gg API, caching them next to the mod DLL so the
/// overlay also works offline.
/// </summary>
internal static class TierLists
{
    // Baalorlord's and Jorbs' lists, one per character plus colorless. Jorbs rates cards only.
    private static readonly (string Author, string Name, string Uuid)[] Lists =
    {
        (Settings.Baalorlord, "ironclad", "004de170-026a-4dd4-a280-3b904be0b5d6"),
        (Settings.Baalorlord, "silent", "6d61ea21-0552-4c49-8bb5-a5c15530fc00"),
        (Settings.Baalorlord, "regent", "0e6c1e23-bec6-4887-a9e0-dbf49ede974d"),
        (Settings.Baalorlord, "necrobinder", "43d0b41f-7d6d-4ce9-928e-c1310a413983"),
        (Settings.Baalorlord, "defect", "5a512e04-4583-4a16-9271-d46864c6cb4c"),
        (Settings.Baalorlord, "colorless", "0a626c22-dc49-433e-ac6e-76cc9abf5684"),
        (Settings.Jorbs, "ironclad", "6b3390b7-bcbc-4ebc-ae78-ae9a400c66dc"),
        (Settings.Jorbs, "silent", "2cd72d4b-eeb4-42ab-89ca-4ac682137672"),
        (Settings.Jorbs, "regent", "c47dbfda-4983-4107-8c9c-c33d5ae32b23"),
        (Settings.Jorbs, "necrobinder", "3ae661e6-6190-4a45-b00e-63e8400fff94"),
        (Settings.Jorbs, "defect", "5f8d3e04-1f50-4b56-b51a-d8650b000ce3"),
        (Settings.Jorbs, "colorless", "81684acf-26a7-4e44-a649-84f4c2824ec0"),
    };

    // game_version=v0.107.1 resolves to the newest list for that version or older, and
    // "public" to the one for the current public game release.
    private const string ApiUrl = "https://api.sts2.untapped.gg/api/v1/tier-lists/{0}/resolve/?game_version={1}";
    private const string PublicVersion = "public";

    // Lists change rarely, so refresh at most this often, or when the game version changes.
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);

    private const string Colorless = "colorless";

    // "author/list" -> item ID -> entry. Lists rate items per character (e.g. a colorless card
    // can be S for Ironclad but A in general), so they are kept apart instead of merged.
    private static volatile Dictionary<string, Dictionary<string, TierEntry>> _lists = new();

    /// <summary>Counts up whenever new lists are in place, so the mod can check them once.</summary>
    public static int Generation => _generation;

    private static volatile int _generation;

    /// <summary>All loaded entries as ("author/list", item ID, entry), for checking IDs.</summary>
    public static IEnumerable<(string List, string ItemId, TierEntry Entry)> All() =>
        _lists.SelectMany(l => l.Value.Select(e => (l.Key, e.Key, e.Value)));

    /// <summary>
    /// Looks an item up in the chosen author's lists: the current character's list wins over
    /// the colorless list. Another character's card (Kaleidoscope, Prismatic Gem, Splash, …)
    /// comes from that character's list, marked with whose it is. Relics and potions fall back
    /// to Baalorlord's lists, since Jorbs rates neither.
    /// </summary>
    public static bool TryGet(string? characterId, string itemId, out TierEntry entry, bool isCard = true)
    {
        var author = Settings.Author;
        if (TryGet(author, characterId, itemId, out entry))
            return true;
        if (isCard && OwnerOf(itemId) is { } owner && owner != characterId?.ToLowerInvariant()
            && TryGet(author, owner, itemId, out entry))
        {
            entry = entry with { RatedFor = owner };
            return true;
        }
        return !isCard && author != Settings.Baalorlord && TryGet(Settings.Baalorlord, characterId, itemId, out entry);
    }

    // Card ID -> the character whose pool has it, by list name ("defect"). Colorless cards
    // are in no character's pool.
    private static Dictionary<string, string>? _owners;

    private static string? OwnerOf(string cardId)
    {
        _owners ??= ModelDb.AllCharacters
            .SelectMany(c => c.CardPool.AllCardIds.Select(id => (Card: id.Entry, Owner: c.CardPool.Title)))
            .GroupBy(p => p.Card)
            .ToDictionary(g => g.Key, g => g.First().Owner);
        return _owners.TryGetValue(cardId, out var owner) ? owner : null;
    }

    private static bool TryGet(string author, string? characterId, string itemId, out TierEntry entry)
    {
        var lists = _lists;
        if (characterId != null && lists.TryGetValue(Key(author, characterId.ToLowerInvariant()), out var own) && own.TryGetValue(itemId, out entry!))
            return true;
        if (lists.TryGetValue(Key(author, Colorless), out var colorless) && colorless.TryGetValue(itemId, out entry!))
            return true;
        entry = null!;
        return false;
    }

    private static string Key(string author, string name) => $"{author.ToLowerInvariant()}/{name}";

    // Not .json: the game treats every .json file below mods/ as a mod manifest.
    private static string CachePath(string cacheDir, string author, string name) =>
        Path.Combine(cacheDir, $"{author.ToLowerInvariant()}-{name}.tierlist");

    /// <summary>
    /// Loads the cached lists at once (so badges also work offline), then fetches fresh ones
    /// for `gameVersion` (e.g. "v0.107.1") if the cache is old or from another game version.
    /// </summary>
    public static void LoadInBackground(string cacheDir, string? gameVersion)
    {
        Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                var cached = new Dictionary<string, Dictionary<string, TierEntry>>();
                foreach (var (author, name, _) in Lists)
                {
                    var path = CachePath(cacheDir, author, name);
                    if (File.Exists(path))
                        cached[Key(author, name)] = Parse(await File.ReadAllTextAsync(path));
                }
                Publish(cached, "from cache");

                var stamp = new ConfigFile();
                var stampPath = Path.Combine(cacheDir, "fetched.cfg");
                stamp.Load(stampPath);
                var fetchedFor = stamp.GetValue("fetched", "game_version", "").AsString();
                var fetchedAt = DateTime.FromBinary(stamp.GetValue("fetched", "time", 0L).AsInt64());
                var version = gameVersion ?? PublicVersion;
                if (cached.Count == Lists.Length && fetchedFor == version && DateTime.UtcNow - fetchedAt < RefreshAfter)
                    return;

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var fetched = await Task.WhenAll(Lists.Select(l => Fetch(http, cacheDir, l.Author, l.Name, l.Uuid, version)));
                var fresh = new Dictionary<string, Dictionary<string, TierEntry>>(cached);
                foreach (var (key, entries) in fetched)
                {
                    if (entries != null)
                        fresh[key] = entries;
                }
                Publish(fresh, $"for game {version}");
                if (fetched.All(f => f.Entries != null))
                {
                    stamp.SetValue("fetched", "game_version", version);
                    stamp.SetValue("fetched", "time", DateTime.UtcNow.ToBinary());
                    stamp.Save(stampPath);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Loading tier lists failed: {ex}");
            }
        });
    }

    private static void Publish(Dictionary<string, Dictionary<string, TierEntry>> lists, string how)
    {
        if (lists.Count == 0)
            return;
        _lists = lists;
        _generation++;
        GD.Print($"[TierListOverlay] Loaded {lists.Count} tier lists with {lists.Values.Sum(l => l.Count)} entries {how}");
    }

    // Fetches one list for the game version, or for the public release when the API has none
    // for that version (404: the version is older than any list). Null when offline.
    private static async Task<(string Key, Dictionary<string, TierEntry>? Entries)> Fetch(
        System.Net.Http.HttpClient http, string cacheDir, string author, string name, string uuid, string version)
    {
        var key = Key(author, name);
        try
        {
            using var response = await http.GetAsync(string.Format(ApiUrl, uuid, version));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && version != PublicVersion)
            {
                GD.Print($"[TierListOverlay] No {author} {name} list for game {version}, using the public one");
                return await Fetch(http, cacheDir, author, name, uuid, PublicVersion);
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var entries = Parse(json);
            await File.WriteAllTextAsync(CachePath(cacheDir, author, name), json);
            return (key, entries);
        }
        catch (Exception ex)
        {
            GD.Print($"[TierListOverlay] Fetching {author}'s {name} list failed ({ex.Message}), keeping the cached one");
            return (key, null);
        }
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
                    into[id] = new TierEntry(name, color, commentary, author, goodUpgrade, maxCopies, group);
                }
            }
        }
        return into;
    }
}
