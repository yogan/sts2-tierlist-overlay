using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

namespace TierListOverlay;

/// <summary>What the player is choosing. The stats and their wording differ per kind.</summary>
internal enum StatsKind
{
    Reward,
    Skip,
    Ancient,
    Shop,
    Upgrade,
    // Event options: the player's own counts only.
    Event,
}

/// <summary>
/// How players who were offered an item did, from Untapped.gg's global stats (Ascension 7+):
/// how often they took it (picked, bought or upgraded) and how the act's and the run's winrate
/// changed when they did, each with a tier letter. Values are null where the server has too
/// little data. `AllCharacters`: the server had too little for the character and counted all
/// characters' runs.
/// </summary>
internal sealed record StatEntry(
    double? ActionPct, string? ActionTier,
    double? ActDelta, string? ActTier,
    double? RunDelta, string? RunTier,
    int TimesOffered, bool AllCharacters = false)
{
    public bool HasData => TimesOffered > 0 && ActionPct != null;
}

/// <summary>
/// An item's stats as a badge and its tip show them: Untapped.gg's (`Global`, for `Act`, 1 to 3
/// or 4 after the last boss) and the player's own (`Mine`), either of them null where there
/// are none.
/// </summary>
internal sealed record StatsInfo(StatsKind Kind, StatEntry? Global, int Act, PersonalStats? Mine);

/// <summary>The answer for one screen, by item group ("cards", "relics", "potions", "skip", "remove").</summary>
internal sealed class StatsAnswer
{
    public readonly Dictionary<string, Dictionary<string, StatEntry>> Groups = new();

    public StatEntry? Get(string group, string id) =>
        Groups.TryGetValue(group, out var items) && items.TryGetValue(id, out var entry) ? entry : null;
}

/// <summary>A stats request for one screen. `Body` doubles as the cache key.</summary>
internal sealed record StatsRequest(string Path, string Body, int Act);

/// <summary>
/// Fetches global stats for what a screen offers, in the background, and keeps the answers
/// for half an hour. Asking again for the same offer returns the kept answer.
/// </summary>
internal static class Stats
{
    public enum Lookup
    {
        Ready,
        // Asked for, the answer is not in yet.
        Pending,
        // No stats: no login, offline, or the request failed.
        Unavailable,
    }

    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
    // A failed request is not repeated at once, so an outage does not mean a request a frame.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private sealed class Slot
    {
        public DateTime At = DateTime.UtcNow;
        public StatsAnswer? Answer;
        public bool Done;
    }

    private static readonly Dictionary<string, Slot> Cache = new();

    public static void Clear()
    {
        lock (Cache)
            Cache.Clear();
    }

    public static Lookup Get(StatsRequest request, out StatsAnswer? answer)
    {
        answer = null;
        if (!Account.Usable)
            return Lookup.Unavailable;
        Slot? slot;
        lock (Cache)
        {
            if (!Cache.TryGetValue(request.Body, out slot) || Stale(slot))
            {
                // Answers for offers long gone go before a new one comes in.
                foreach (var key in Cache.Where(c => Stale(c.Value)).Select(c => c.Key).ToList())
                    Cache.Remove(key);
                slot = Cache[request.Body] = new Slot();
                Fetch(request, slot);
            }
        }
        if (!slot.Done)
            return Lookup.Pending;
        answer = slot.Answer;
        return answer == null ? Lookup.Unavailable : Lookup.Ready;
    }

    private static bool Stale(Slot slot) =>
        slot.Done && DateTime.UtcNow - slot.At > (slot.Answer == null ? RetryAfter : KeepFor);

    private static void Fetch(StatsRequest request, Slot slot)
    {
        Task.Run(async () =>
        {
            StatsAnswer? answer = null;
            try
            {
                var json = await Account.Post(request.Path, request.Body);
                if (json != null)
                {
                    answer = Parse(json);
                    GD.Print($"[TierListOverlay] Stats {request.Path} for {request.Body}: {string.Join(", ", answer.Groups.Select(g => $"{g.Value.Count} {g.Key}"))}");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Stats request {request.Path} failed: {ex}");
            }
            lock (Cache)
            {
                slot.Answer = answer;
                slot.At = DateTime.UtcNow;
                slot.Done = true;
            }
        });
    }

    // The answer's top-level fields per item group: a map of item ID to stats, or (skip and
    // card removal) one item's stats.
    private static readonly (string Field, string Group, bool Single)[] Fields =
    {
        ("card_reward_stats", "cards", false),
        ("skip_button_stats", "skip", true),
        ("ancient_choice_stats", "relics", false),
        ("offered_cards_stats", "cards", false),
        ("offered_relics_stats", "relics", false),
        ("offered_potions_stats", "potions", false),
        ("remove_button_stats", "remove", true),
        ("card_stats", "cards", false),
    };

    public const string SingleId = "";

    private static StatsAnswer Parse(string json)
    {
        var answer = new StatsAnswer();
        using var doc = JsonDocument.Parse(json);
        foreach (var (field, group, single) in Fields)
        {
            if (!doc.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Object)
                continue;
            var items = answer.Groups.TryGetValue(group, out var existing) ? existing : answer.Groups[group] = new();
            if (single)
                items[SingleId] = Entry(value);
            else
            {
                foreach (var item in value.EnumerateObject())
                    items[item.Name] = Entry(item.Value);
            }
        }
        return answer;
    }

    private static StatEntry Entry(JsonElement e)
    {
        // The share that took the item is named after what the screen offers it for.
        var action = new[] { "pick", "buy", "upgrade" }.FirstOrDefault(a => e.TryGetProperty(a + "_pct", out _)) ?? "pick";
        return new StatEntry(
            Number(e, action + "_pct"), Tier(e, action + "_tier"),
            Number(e, "win_ca_pct_delta"), Tier(e, "win_ca_tier"),
            Number(e, "win_pct_delta"), Tier(e, "win_tier"),
            e.TryGetProperty("times_offered", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
            e.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
                && warnings.EnumerateArray().Any(w => w.TryGetProperty("warning_enum", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == WidenedScope));
    }

    // The server's warning that it counted all characters ("Widened Scope Warning").
    private const int WidenedScope = 5;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // "NA" when the server has no tier for the value.
    private static string? Tier(JsonElement e, string name) => Text(e, name) is { } t && t != "NA" ? t : null;

    // --- Requests, one per screen. Items are sorted so the same offer makes the same key. ---

    /// <param name="Enchantment">The enchantment's ID ("SHARP"), if the card has one.</param>
    public sealed record Card(string Id, int UpgradeLevel, string? Enchantment, int Price = 0);

    public sealed record Priced(string Id, int Price);

    private static JsonObject CardJson(Card c, bool upgrade = true, bool price = false)
    {
        var o = new JsonObject { ["id"] = c.Id };
        if (upgrade)
            o["upgrade_level"] = c.UpgradeLevel;
        // The API refuses a null enchantment, so plain cards leave it out.
        if (c.Enchantment != null)
            o["enchantment"] = c.Enchantment;
        if (price)
            o["price"] = c.Price;
        return o;
    }

    private static StatsRequest Request(string endpoint, JsonObject body, int act, string character, int partySize)
    {
        body["act"] = act;
        body["character"] = character;
        body["party_size"] = partySize;
        return new StatsRequest($"/api/v1/global-stats/{endpoint}/", body.ToJsonString(), act);
    }

    private static JsonArray Array(IEnumerable<JsonNode> items) => new(items.ToArray());

    /// <param name="roomType">The game's RoomType: 1 monster, 2 elite, 3 boss.</param>
    public static StatsRequest CardReward(IEnumerable<Card> cards, int act, string character, int partySize, int roomType) =>
        Request("card-reward-stats", new JsonObject
        {
            ["offered_cards"] = Array(cards.OrderBy(c => c.Id).ThenBy(c => c.UpgradeLevel).Select(c => (JsonNode)CardJson(c))),
            ["room_type"] = roomType,
        }, act, character, partySize);

    public static StatsRequest Ancient(IEnumerable<string> relicIds, int act, string character, int partySize) =>
        Request("ancient-choice-stats", new JsonObject
        {
            ["ancient_choices"] = Array(relicIds.OrderBy(id => id).Select(id => (JsonNode)JsonValue.Create(id))),
        }, act, character, partySize);

    public static StatsRequest Shop(IEnumerable<Card> cards, IEnumerable<Priced> relics, IEnumerable<Priced> potions, int removePrice, int act, string character, int partySize) =>
        Request("shop-stats", new JsonObject
        {
            ["offered_cards"] = Array(cards.OrderBy(c => c.Id).ThenBy(c => c.Price).Select(c => (JsonNode)CardJson(c, price: true))),
            ["offered_relics"] = Array(relics.OrderBy(r => r.Id).Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["price"] = r.Price })),
            ["offered_potions"] = Array(potions.OrderBy(p => p.Id).Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["price"] = p.Price })),
            ["remove_price"] = removePrice,
        }, act, character, partySize);

    public static StatsRequest Upgrade(IEnumerable<Card> cards, int act, string character, int partySize) =>
        Request("card-upgrade-stats", new JsonObject
        {
            ["upgradeable_cards"] = Array(cards.DistinctBy(c => (c.Id, c.Enchantment)).OrderBy(c => c.Id).Select(c => (JsonNode)CardJson(c, upgrade: false))),
            ["screen"] = "upgrade",
        }, act, character, partySize);
}
