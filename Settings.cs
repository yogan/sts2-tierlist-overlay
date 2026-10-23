using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TierListOverlay;

/// <summary>
/// What a badge shows. The badges' source (all but None) is kept in settings.cfg by its
/// lower-case name.
/// </summary>
internal enum BadgeMode
{
    None,
    Tiers,
    // The player's own numbers from their run history, on the screens that have them.
    Personal,
    // Untapped.gg's stats, on the screens it has them for (and while the login works).
    Global,
}

/// <summary>
/// The player's choices, kept in settings.cfg next to the mod DLL and edited in the mod's tab
/// of the game's settings screen (see SettingsUi). One source for every badge on what the game
/// offers ("tiers"), so new sources are new values, and a toggle per kind of item. A screen
/// never mixes sources. A change of layout raises Version and adds a step to Migrate.
/// </summary>
internal static class Settings
{
    public const string Baalorlord = "Baalorlord";
    public const string Jorbs = "Jorbs";

    private const string Section = "tierlist";
    private const string VersionKey = "settings_version";
    private const int Version = 3;

    // What the badges on offers show: tiers, personal or global.
    public const string BadgesKey = "badges";
    // Where: the cards on offer (rewards and Skip, "choose a card", bundles, the shop and card
    // removal, upgrades), relics (ancients' among them) and potions.
    public const string CardsKey = "cards";
    public const string RelicsKey = "relics";
    public const string PotionsKey = "potions";
    // The player's own counts on event options (not ancients'), with personal badges.
    public const string EventsKey = "events";
    // Tiers on the cards in the deck, whatever the source: deck view, remove, transform, enchant.
    public const string DeckKey = "deck";
    public const string AuthorKey = "author";
    public const string CommentaryKey = "commentary";
    // The player's and Untapped.gg's stats as hover tips, whatever the badges show.
    public const string StatsTipsKey = "stats_tips";
    // How many offers or runs the stats are based on, in the stats tips.
    public const string StatsSamplesKey = "stats_samples";
    // Baalorlord's or Jorbs' advice on the event, as a hover tip on its options (with a login).
    public const string EventAdviceKey = "event_advice";

    private static readonly Dictionary<string, Variant> Defaults = new()
    {
        [BadgesKey] = Value(BadgeMode.Tiers),
        [CardsKey] = true,
        [RelicsKey] = true,
        [PotionsKey] = true,
        [EventsKey] = true,
        [DeckKey] = true,
        [AuthorKey] = Baalorlord,
        [CommentaryKey] = true,
        [StatsTipsKey] = true,
        [StatsSamplesKey] = false,
        [EventAdviceKey] = true,
    };

    private static readonly ConfigFile Config = new();
    private static string _path = "";

    public static void Load(string path)
    {
        _path = path;
        var err = Config.Load(path);
        if (err != Error.Ok && err != Error.FileNotFound)
            GD.PrintErr($"[TierListOverlay] Reading {path} failed: {err}");
        Migrate();
    }

    // From the version a file was written by (1 before versions were written) to the current.
    private static void Migrate()
    {
        if (!Config.HasSection(Section))
            return;
        var from = Config.GetValue(Section, VersionKey, 1).AsInt32();
        if (from >= Version)
            return;
        if (from < 2)
            FromVersion1();
        if (from < 3)
            FromVersion2();
        Config.SetValue(Section, VersionKey, Version);
        Save();
        GD.Print($"[TierListOverlay] Settings carried over from version {from} to {Version}");
    }

    // Version 1 (mod 1.1): a toggle per kind of badge, one for relics and potions together, and
    // one for Jorbs.
    private static void FromVersion1()
    {
        Variant Mode(string key) => Value(Config.GetValue(Section, key, true).AsBool() ? BadgeMode.Tiers : BadgeMode.None);
        var old = new Dictionary<string, Variant>
        {
            [CardsKey] = Mode("card_choices"),
            [RelicsKey] = Mode("relics"),
            [PotionsKey] = Mode("relics"),
            [DeckKey] = Mode("deck_screens"),
            [AuthorKey] = Config.GetValue(Section, "use_jorbs", false).AsBool() ? Jorbs : Baalorlord,
            [CommentaryKey] = Config.GetValue(Section, "hover_tips", true),
        };
        foreach (var key in Config.GetSectionKeys(Section))
            Config.EraseSectionKey(Section, key);
        foreach (var (key, value) in old)
            Config.SetValue(Section, key, value);
    }

    // Version 2 (mod 1.2 to 1.5): a source per kind of badge (none, tiers, personal, global),
    // events none or personal, the deck none or tiers. The source becomes the strongest one
    // chosen (global, then personal, then tiers), and each kind's choice whether it has badges.
    private static void FromVersion2()
    {
        string Old(string key, string fallback) => Config.GetValue(Section, key, fallback).AsString();
        var kinds = new[] { CardsKey, RelicsKey, PotionsKey }.ToDictionary(k => k, k => Old(k, Value(BadgeMode.Tiers)));
        var source = new[] { BadgeMode.Global, BadgeMode.Personal }.FirstOrDefault(m => kinds.ContainsValue(Value(m)), BadgeMode.Tiers);
        Config.SetValue(Section, BadgesKey, Value(source));
        foreach (var (key, value) in kinds)
            Config.SetValue(Section, key, value != Value(BadgeMode.None));
        Config.SetValue(Section, EventsKey, Old(EventsKey, Value(BadgeMode.Personal)) == Value(BadgeMode.Personal));
        Config.SetValue(Section, DeckKey, Old(DeckKey, Value(BadgeMode.Tiers)) != Value(BadgeMode.None));
    }

    public static Variant Get(string key) => Config.GetValue(Section, key, Defaults[key]);

    public static void Set(string key, Variant value)
    {
        _source = null;
        Config.SetValue(Section, key, value);
        Config.SetValue(Section, VersionKey, Version);
        Save();
        GD.Print($"[TierListOverlay] Setting {key} = {value}");
    }

    private static void Save()
    {
        var err = Config.Save(_path);
        if (err != Error.Ok)
            GD.PrintErr($"[TierListOverlay] Saving {_path} failed: {err}");
    }

    public static string Value(BadgeMode mode) => mode.ToString().ToLowerInvariant();

    // The source the player chose. An unknown value (from a newer version, or hand-edited) falls
    // back to the default. Read every frame for every badge, so parsed once per change.
    private static BadgeMode? _source;

    public static BadgeMode Source
    {
        get
        {
            if (_source is { } source)
                return source;
            var value = Get(BadgesKey).AsString();
            if (!Enum.TryParse(value, ignoreCase: true, out source) || source == BadgeMode.None || !Enum.IsDefined(source) || int.TryParse(value, out _))
                source = Enum.Parse<BadgeMode>(Defaults[BadgesKey].AsString(), ignoreCase: true);
            return (_source = source).Value;
        }
    }

    // What the badges show: the source, except that without a working login global stats
    // become the player's own, the nearest to them. The choice itself stays, for the next login.
    public static BadgeMode ShownSource => Source == BadgeMode.Global && !Account.Usable ? BadgeMode.Personal : Source;

    private static BadgeMode On(string key) => Get(key).AsBool() ? ShownSource : BadgeMode.None;

    public static BadgeMode Cards => On(CardsKey);
    public static BadgeMode Relics => On(RelicsKey);
    public static BadgeMode Potions => On(PotionsKey);
    // Events have only the player's own counts.
    public static bool EventsApply => Source == BadgeMode.Personal;
    public static BadgeMode Events => EventsApply && Get(EventsKey).AsBool() ? BadgeMode.Personal : BadgeMode.None;
    // Only tiers for the cards in the deck.
    public static bool Deck => Get(DeckKey).AsBool();
    public static string Author => Get(AuthorKey).AsString() == Jorbs ? Jorbs : Baalorlord;
    public static bool Commentary => Get(CommentaryKey).AsBool();
    public static bool StatsTips => Get(StatsTipsKey).AsBool();
    public static bool StatsSamples => Get(StatsSamplesKey).AsBool();
    // The advice comes from Untapped.gg, so it needs the login like global stats.
    public static bool EventAdvice => Get(EventAdviceKey).AsBool() && Account.Usable;

    /// <summary>Whether tiers show anywhere (badges or tips), so the choice of author matters.</summary>
    public static bool AnyTiers =>
        (ShownSource == BadgeMode.Tiers && (Get(CardsKey).AsBool() || Get(RelicsKey).AsBool() || Get(PotionsKey).AsBool())) || Deck || Commentary;

    /// <summary>
    /// After the player's own login: they want other players' stats, so the badges show them.
    /// </summary>
    public static void OnLoggedIn()
    {
        if (Source != BadgeMode.Global)
            Set(BadgesKey, Value(BadgeMode.Global));
    }
}
