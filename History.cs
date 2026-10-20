using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace TierListOverlay;

/// <summary>
/// How often the player took an item when offered it in their own past runs: `Taken` of
/// `Offered`, and in how many of the character's `Wins` the item was in the final deck or
/// relics (null where that means nothing: Skip, card removal, potions). `Runs`: the runs counted.
/// `LowerRuns`: in how many runs below Ascension 7, which the rest leaves out, they took it.
/// `Item`: false for Skip and card removal, which are choices, not items. `OthersTaken` of
/// `OthersOffered`: the same with the player's other characters.
/// </summary>
internal sealed record PersonalStats(int Taken, int Offered, int? InWins, int Wins, int Runs, int LowerRuns, bool Item,
    int OthersTaken, int OthersOffered)
{
    /// <summary>An item never taken in a counted run, however often (or rarely) offered.</summary>
    public bool Unpicked => Item && Taken == 0;

    /// <summary>Counts or Unpicked. Not a Skip or card removal never offered.</summary>
    public bool Shown => Unpicked || Offered > 0;
}

/// <summary>
/// Reads the game's run history (one JSON file per finished run in the profile's
/// saves/history folder) and counts, per character, what the player was offered and took on
/// the screens with stats. Runs count at Ascension 7+, in standard mode and alone, from all
/// acts and game versions. Lower runs only count for what was taken there. The modded and the
/// unmodded folder of the current profile number are combined (the game itself reads only the
/// current mode's). Only reads: never writes, moves or renames anything there.
///
/// The folders are listed every few seconds in the background, and only new files are read,
/// so a finished run counts from the next screen on. A `test-history` folder next to the mod
/// DLL takes the place of the profile folders, for tests (tools/check.py).
/// </summary>
internal static class History
{
    private const int MinAscension = 7;
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(3);

    private static string _testDir = "";
    private static DateTime _lastScan = DateTime.MinValue;
    private static Task? _scan;

    // Runs read so far, by file path. Null for files that don't count (or can't be read).
    private static readonly Dictionary<string, Run?> Runs = new();
    private static IReadOnlyList<string> _folders = System.Array.Empty<string>();

    // The counts per character ("DEFECT"), replaced as a whole when runs come and go. Null
    // until the history has been read once.
    private static volatile Dictionary<string, Tally>? _tallies;

    public static void Load(string modDir) => _testDir = Path.Combine(modDir, "test-history");

    /// <summary>The player's numbers for an item on a screen, zero for one never offered. Null until the history is read.</summary>
    public static PersonalStats? Get(string character, StatsKind kind, string group, string id)
    {
        if (_tallies is not { } tallies)
            return null;
        var key = (kind, group, id);
        var others = tallies.Where(t => t.Key != character).Select(t => t.Value.Counts.GetValueOrDefault(key)).OfType<Count>().ToList();
        var (othersTaken, othersOffered) = (others.Sum(c => c.Taken), others.Sum(c => c.Offered));
        var item = id != Stats.SingleId;
        if (!tallies.TryGetValue(character, out var tally))
            return new PersonalStats(0, 0, null, 0, 0, 0, item, othersTaken, othersOffered);
        var count = tally.Counts.GetValueOrDefault(key);
        int? inWins = group is "cards" or "relics" && tally.Wins > 0 ? tally.InWins.GetValueOrDefault((group, id)) : null;
        return new PersonalStats(count?.Taken ?? 0, count?.Offered ?? 0, inWins, tally.Wins, tally.Runs,
            tally.LowerRuns.GetValueOrDefault(key), item, othersTaken, othersOffered);
    }

    public static void OnProcessFrame()
    {
        if (_scan is { IsCompleted: false } || DateTime.UtcNow - _lastScan < ScanEvery)
            return;
        _lastScan = DateTime.UtcNow;
        var folders = Folders();
        if (folders == null)
            return;
        _scan = Task.Run(() =>
        {
            try
            {
                Scan(folders);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Reading the run history failed: {ex}");
            }
        });
    }

    // The history folders of the current profile number, modded and not. Null until the game
    // has chosen a profile.
    private static List<string>? Folders()
    {
        if (Directory.Exists(_testDir))
            return new List<string> { _testDir };
        int profile;
        string account;
        try
        {
            profile = SaveManager.Instance.CurrentProfileId;
            account = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null));
        }
        catch (Exception)
        {
            return null;
        }
        return new List<string>
        {
            Path.Combine(account, $"profile{profile}", "saves", "history"),
            Path.Combine(account, "modded", $"profile{profile}", "saves", "history"),
        };
    }

    private static void Scan(List<string> folders)
    {
        // `.run.backup` and `.corrupt` files are the game's spares.
        var files = folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*.run"))
            .ToHashSet();
        var changed = !folders.SequenceEqual(_folders);
        if (changed)
            Runs.Clear();
        _folders = folders;
        foreach (var gone in Runs.Keys.Where(f => !files.Contains(f)).ToList())
        {
            Runs.Remove(gone);
            changed = true;
        }
        foreach (var file in files.Where(f => !Runs.ContainsKey(f)))
        {
            Runs[file] = Read(file);
            changed = true;
        }
        if (!changed)
            return;
        var read = Runs.Values.OfType<Run>().ToList();
        var tallies = read.GroupBy(r => r.Character).ToDictionary(g => g.Key, g => new Tally(g.ToList()));
        _tallies = tallies;
        GD.Print($"[TierListOverlay] Run history: {Runs.Count} runs, {read.Count(r => !r.Lower)} counted, {read.Count(r => r.Lower)} lower "
                 + $"({string.Join(", ", tallies.Select(t => $"{t.Key} {t.Value.Runs} with {t.Value.Wins} wins"))})");
    }

    // An item's offers: how often it was offered and taken.
    private sealed class Count
    {
        public int Offered;
        public int Taken;
    }

    private sealed class Tally
    {
        public readonly int Runs;
        public readonly int Wins;
        public readonly Dictionary<(StatsKind, string, string), Count> Counts = new();
        // Won runs that ended with the item, by group ("cards", "relics") and ID.
        public readonly Dictionary<(string, string), int> InWins = new();
        // In how many runs below Ascension 7 each item was taken.
        public readonly Dictionary<(StatsKind, string, string), int> LowerRuns = new();

        public Tally(List<Run> runs)
        {
            foreach (var run in runs.Where(r => r.Lower))
            {
                foreach (var key in run.Offers.Where(o => o.Item2).Select(o => o.Item1).Distinct())
                    LowerRuns[key] = LowerRuns.GetValueOrDefault(key) + 1;
            }
            runs = runs.Where(r => !r.Lower).ToList();
            Runs = runs.Count;
            foreach (var run in runs)
            {
                foreach (var (key, taken) in run.Offers)
                {
                    if (!Counts.TryGetValue(key, out var count))
                        Counts[key] = count = new Count();
                    count.Offered++;
                    if (taken)
                        count.Taken++;
                }
                if (!run.Win)
                    continue;
                Wins++;
                foreach (var item in run.Final)
                    InWins[item] = InWins.GetValueOrDefault(item) + 1;
            }
        }
    }

    // One run: the character, whether it was below Ascension 7, whether it was won, each offer
    // (screen kind, group, ID) and whether it was taken, and the cards and relics it ended with.
    private sealed record Run(string Character, bool Lower, bool Win, List<((StatsKind, string, string), bool)> Offers, HashSet<(string, string)> Final);

    // A run that counts (at any ascension), or null.
    private static Run? Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            GD.PrintErr($"[TierListOverlay] Skipping run history file {Path.GetFileName(file)}: {ex.Message}");
            return null;
        }
    }

    // The game's IDs carry their kind ("CARD.BASH"), the screens' don't.
    private static string Id(string? id) => id == null ? "" : id[(id.IndexOf('.') + 1)..];

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Run? Parse(JsonElement root)
    {
        var players = Array(root, "players").ToList();
        if (players.Count != 1 || Text(root, "game_mode") != "standard" || !root.TryGetProperty("ascension", out var ascension))
            return null;
        var player = players[0];
        var offers = new List<((StatsKind, string, string), bool)>();
        void Offer(StatsKind kind, string group, string id, bool taken) => offers.Add(((kind, group, id), taken));

        foreach (var floor in Array(root, "map_point_history").SelectMany(act => act.EnumerateArray()))
        {
            // Solo runs have the one player's stats.
            var stats = Array(floor, "player_stats").FirstOrDefault();
            if (stats.ValueKind != JsonValueKind.Object)
                continue;
            var cards = Array(stats, "card_choices").ToList();
            if (Text(floor, "map_point_type") == "shop")
            {
                // Leaving the shop logs what was left unbought. Bought cards are the ones gained,
                // bought relics and potions are logged as picked.
                foreach (var card in cards)
                    Offer(StatsKind.Shop, "cards", Id(Text(card.GetProperty("card"), "id")), false);
                foreach (var card in Array(stats, "cards_gained"))
                    Offer(StatsKind.Shop, "cards", Id(Text(card, "id")), true);
                foreach (var relic in Array(stats, "relic_choices"))
                    Offer(StatsKind.Shop, "relics", Id(Text(relic, "choice")), Flag(relic, "was_picked"));
                foreach (var potion in Array(stats, "potion_choices"))
                    Offer(StatsKind.Shop, "potions", Id(Text(potion, "choice")), Flag(potion, "was_picked"));
                Offer(StatsKind.Shop, "remove", Stats.SingleId, Array(stats, "cards_removed").Any());
            }
            else if (cards.Count > 0)
            {
                // A card reward, with rerolled ones. Taking none is a skip.
                var picked = false;
                foreach (var card in cards)
                {
                    var taken = Flag(card, "was_picked");
                    picked |= taken;
                    Offer(StatsKind.Reward, "cards", Id(Text(card.GetProperty("card"), "id")), taken);
                }
                Offer(StatsKind.Skip, "skip", Stats.SingleId, !picked);
            }
            // Ancients name their relics without the "RELIC." the other entries have.
            foreach (var choice in Array(stats, "ancient_choice"))
                Offer(StatsKind.Ancient, "relics", Id(Text(choice, "TextKey")), Flag(choice, "was_chosen"));
        }

        var final = Array(player, "deck").Select(c => ("cards", Id(Text(c, "id"))))
            .Concat(Array(player, "relics").Select(r => ("relics", Id(Text(r, "id")))))
            .ToHashSet();
        return new Run(Id(Text(player, "character")), ascension.GetInt32() < MinAscension, Flag(root, "win"), offers, final);
    }
}
