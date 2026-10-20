using System;
using System.Collections.Generic;
using Godot;

namespace TierListOverlay;

/// <summary>What a kind of badge shows. Kept in settings.cfg by its lower-case name.</summary>
internal enum BadgeMode
{
    None,
    Tiers,
    // The player's own numbers from their run history on the screens that have them, tiers elsewhere.
    Personal,
    // Untapped.gg's stats where it has them (and the login works), tiers elsewhere.
    Global,
}

/// <summary>
/// The player's choices, kept in settings.cfg next to the mod DLL and edited in the mod's tab
/// of the game's settings screen (see SettingsUi). Badge settings name what the badge shows
/// ("tiers"), so new sources are new values. A change of layout raises Version and adds a
/// step to Migrate.
/// </summary>
internal static class Settings
{
    public const string Baalorlord = "Baalorlord";
    public const string Jorbs = "Jorbs";

    private const string Section = "tierlist";
    private const string VersionKey = "settings_version";
    private const int Version = 2;

    // The cards on offer: rewards (and Skip), "choose a card", bundles, the shop (and card
    // removal), upgrades.
    public const string CardsKey = "cards";
    public const string RelicsKey = "relics";
    public const string PotionsKey = "potions";
    // The options of events (not ancients, whose relics go with the relics): none or personal.
    public const string EventsKey = "events";
    // The cards in the deck: deck view, remove, transform, enchant.
    public const string DeckKey = "deck";
    public const string AuthorKey = "author";
    public const string CommentaryKey = "commentary";
    // The player's and Untapped.gg's stats as hover tips, whatever the badges show.
    public const string StatsTipsKey = "stats_tips";
    // How many offers or runs the stats are based on, in the stats tips.
    public const string StatsSamplesKey = "stats_samples";

    private static readonly Dictionary<string, Variant> Defaults = new()
    {
        [CardsKey] = Value(BadgeMode.Tiers),
        [RelicsKey] = Value(BadgeMode.Tiers),
        [PotionsKey] = Value(BadgeMode.Tiers),
        [EventsKey] = Value(BadgeMode.Personal),
        [DeckKey] = Value(BadgeMode.Tiers),
        [AuthorKey] = Baalorlord,
        [CommentaryKey] = true,
        [StatsTipsKey] = true,
        [StatsSamplesKey] = false,
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

    public static Variant Get(string key) => Config.GetValue(Section, key, Defaults[key]);

    public static void Set(string key, Variant value)
    {
        Parsed.Remove(key);
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

    // What the player chose. Unknown values (from a newer version, or hand-edited) fall back
    // to the default. Read every frame for every badge, so parsed once per change.
    private static readonly Dictionary<string, BadgeMode> Parsed = new();

    public static BadgeMode Chosen(string key)
    {
        if (Parsed.TryGetValue(key, out var mode))
            return mode;
        var value = Get(key).AsString();
        if (!Enum.TryParse(value, ignoreCase: true, out mode) || !Enum.IsDefined(mode) || int.TryParse(value, out _))
            mode = Enum.Parse<BadgeMode>(Defaults[key].AsString(), ignoreCase: true);
        return Parsed[key] = mode;
    }

    // What the badges show: the choice, except that without a working login global stats
    // become tiers. The choice itself stays, for the next login.
    private static BadgeMode Shown(string key) =>
        Chosen(key) is var mode && mode == BadgeMode.Global && !Account.Usable ? BadgeMode.Tiers : mode;

    public static BadgeMode Cards => Shown(CardsKey);
    public static BadgeMode Relics => Shown(RelicsKey);
    public static BadgeMode Potions => Shown(PotionsKey);
    // Events have no tiers and no global stats.
    public static BadgeMode Events => Chosen(EventsKey) == BadgeMode.Personal ? BadgeMode.Personal : BadgeMode.None;
    // No stats for the cards in the deck.
    public static bool Deck => Chosen(DeckKey) != BadgeMode.None;
    public static string Author => Get(AuthorKey).AsString() == Jorbs ? Jorbs : Baalorlord;
    public static bool Commentary => Get(CommentaryKey).AsBool();
    public static bool StatsTips => Get(StatsTipsKey).AsBool();
    public static bool StatsSamples => Get(StatsSamplesKey).AsBool();

    /// <summary>Whether any badge is set to global stats, shown or not (without a login).</summary>
    public static bool AnyGlobalChosen =>
        Chosen(CardsKey) == BadgeMode.Global || Chosen(RelicsKey) == BadgeMode.Global || Chosen(PotionsKey) == BadgeMode.Global;

    /// <summary>Whether tiers show anywhere (badges, fallbacks or tips), so the choice of list matters.</summary>
    public static bool AnyTiers => Cards != BadgeMode.None || Relics != BadgeMode.None || Potions != BadgeMode.None || Deck || Commentary;
}
