using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace TierListOverlay;

/// <summary>
/// Adds the mod's toggles to the General tab of the game's settings screen. Each row is a copy
/// of the game's "Always show the Timer" row with its scripts swapped for plain ones, so it
/// looks like the game's own rows but no longer changes the timer setting.
/// </summary>
internal static class SettingsUi
{
    private const string TemplateLine = "ShowRunTimer";
    private const string TemplateDivider = "ShowTimerDivider";
    private const string RowPrefix = "TierListOverlay_";
    private const string TickboxScript = "res://src/Core/Nodes/CommonUi/NTickbox.cs";

    private static readonly HashSet<NSettingsScreen> Pending = new();

    public static void OnNodeAdded(Node node)
    {
        if (node is NSettingsScreen screen)
            Pending.Add(screen);
    }

    // The screen's panels are filled in its _Ready, so rows are added on the next frame.
    public static void OnProcessFrame()
    {
        if (Pending.Count == 0)
            return;
        var screens = new List<NSettingsScreen>(Pending);
        Pending.Clear();
        foreach (var screen in screens)
        {
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                continue;
            try
            {
                Inject(screen);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Adding settings failed: {ex}");
            }
        }
    }

    private static void Inject(NSettingsScreen screen)
    {
        var content = screen.GetNodeOrNull<NSettingsPanel>("%GeneralSettings")?.Content;
        var line = content?.GetNodeOrNull<Control>(TemplateLine);
        var divider = content?.GetNodeOrNull<Control>(TemplateDivider);
        if (content == null || line == null || divider == null)
        {
            GD.PrintErr("[TierListOverlay] Settings screen layout changed; not adding the mod's settings");
            return;
        }
        if (content.GetNodeOrNull(RowPrefix + Settings.Toggles[0].Key) != null)
            return;

        // After the timer row, so the rows sit together near the top of the tab.
        var index = line.GetIndex() + 1;
        foreach (var (key, label) in Settings.Toggles)
        {
            var newDivider = (Control)divider.Duplicate();
            newDivider.Name = RowPrefix + key + "_Divider";
            content.AddChild(newDivider);
            content.MoveChild(newDivider, index++);

            // A script swap disposes the node's C# object, so nodes are found again by ID.
            var copy = line.Duplicate();
            copy.Name = RowPrefix + key;
            var rowId = copy.GetInstanceId();
            var tickboxId = copy.GetNode("SettingsTickbox").GetInstanceId();
            // Plain Control instead of the timer row's script, which shows the timer's tip.
            copy.SetScript(default(Variant));
            // Plain NTickbox instead of the timer tickbox, whose clicks change the timer setting.
            GodotObject.InstanceFromId(tickboxId)!.SetScript(ResourceLoader.Load(TickboxScript));
            var row = (Control)GodotObject.InstanceFromId(rowId)!;
            content.AddChild(row);
            content.MoveChild(row, index++);

            row.GetNode<MegaRichTextLabel>("Label").Text = label;
            var tickbox = row.GetNode<NTickbox>("SettingsTickbox");
            tickbox.IsTicked = Settings.Get(key);
            var k = key;
            tickbox.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(t => Settings.Set(k, t.IsTicked)));
        }
        GD.Print("[TierListOverlay] Settings added to the settings screen");
    }
}
