using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace TierListOverlay;

/// <summary>
/// One short note on the main menu, once per game start, when stats are on but the login does
/// not work: the badges then show tiers, and the settings say why. Nothing during a run.
/// </summary>
internal static class Hint
{
    private const string HintName = "TierListOverlayHint";
    private static NMainMenu? _menu;
    private static bool _shown;

    public static void OnNodeAdded(Node node)
    {
        if (node is NMainMenu menu && !_shown)
            _menu = menu;
    }

    // Waits for the check of the saved login at game start before deciding.
    public static void OnProcessFrame()
    {
        if (_menu == null || _shown || Account.Status == Account.State.Checking)
            return;
        var menu = _menu;
        _menu = null;
        _shown = true;
        if (!GodotObject.IsInstanceValid(menu) || !menu.IsInsideTree() || !Settings.AnyGlobalChosen || Account.Usable)
            return;
        var why = Account.Status switch
        {
            Account.State.Expired => "the Untapped.gg login has expired",
            Account.State.Offline => "Untapped.gg is not reachable",
            _ => "not logged in to Untapped.gg",
        };
        Show(menu, $"Tier List Overlay: no stats ({why}), showing tiers. Log in under Settings, Tier List.");
    }

    private static void Show(Control menu, string text)
    {
        var label = new Label
        {
            Name = HintName,
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 22);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.97f, 0.9f));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
        label.AddThemeConstantOverride("outline_size", 6);
        // Lets tooling (tools/DevTools) find it.
        label.AddToGroup("tierlist_hint");
        menu.AddChild(label);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        label.Position = new Vector2((menu.Size.X - label.GetCombinedMinimumSize().X) / 2, 24);
        var tween = label.CreateTween();
        tween.TweenInterval(8.0);
        tween.TweenProperty(label, "modulate:a", 0f, 1.0);
        tween.TweenCallback(Callable.From(label.QueueFree));
    }
}
