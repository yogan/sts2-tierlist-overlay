using System.Collections.Generic;
using Godot;

namespace TierListOverlay;

/// <summary>
/// The player's choices, kept in settings.cfg next to the mod DLL and edited in the game's
/// settings screen (see SettingsUi).
/// </summary>
internal static class Settings
{
    public const string Baalorlord = "Baalorlord";
    public const string Jorbs = "Jorbs";

    private const string Section = "tierlist";

    // Toggle keys, in the order the settings screen lists them, with their labels.
    public static readonly (string Key, string Label)[] Toggles =
    {
        ("use_jorbs", "Tier lists: use Jorbs' card ratings (relics stay Baalorlord's)"),
        ("card_choices", "Tier badges on card rewards and the shop"),
        ("deck_screens", "Tier badges on deck screens (upgrade, remove, deck view)"),
        ("relics", "Tier badges on relics and potions"),
        ("hover_tips", "Tier list commentary in hover tips"),
    };

    private static readonly Dictionary<string, bool> Defaults = new()
    {
        ["use_jorbs"] = false,
        ["card_choices"] = true,
        ["deck_screens"] = true,
        ["relics"] = true,
        ["hover_tips"] = true,
    };

    private static readonly ConfigFile Config = new();
    private static string _path = "";

    public static void Load(string path)
    {
        _path = path;
        var err = Config.Load(path);
        if (err != Error.Ok && err != Error.FileNotFound)
            GD.PrintErr($"[TierListOverlay] Reading {path} failed: {err}");
    }

    public static bool Get(string key) => Config.GetValue(Section, key, Defaults[key]).AsBool();

    public static void Set(string key, bool value)
    {
        Config.SetValue(Section, key, value);
        var err = Config.Save(_path);
        if (err != Error.Ok)
            GD.PrintErr($"[TierListOverlay] Saving {_path} failed: {err}");
        GD.Print($"[TierListOverlay] Setting {key} = {value}");
    }

    public static string Author => Get("use_jorbs") ? Jorbs : Baalorlord;
    public static bool CardChoices => Get("card_choices");
    public static bool DeckScreens => Get("deck_screens");
    public static bool Relics => Get("relics");
    public static bool HoverTips => Get("hover_tips");
}
