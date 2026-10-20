using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace TierListOverlay;

/// <summary>
/// Adds the author's tier and commentary, and the item's stats (the player's own and
/// Untapped.gg's), as more hover tips in the game's own tip style whenever the game shows hover
/// tips for the item.
/// </summary>
internal static class Tips
{
    private const string TipScene = "res://scenes/ui/hover_tip.tscn";
    private const string TipGroup = "tierlist_tip";
    private const string StatsTipGroup = "tierlist_stats_tip";
    private const string SettingTipGroup = "tierlist_setting_tip";
    private const string FocusMeta = "tierlist_focus_tip";
    private const string DoneMeta = "tierlist_tip_done";

    private static readonly FieldInfo? OwnerField = typeof(NHoverTipSet).GetField("_owner", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? ContainerField = typeof(NHoverTipSet).GetField("_textHoverTipContainer", BindingFlags.NonPublic | BindingFlags.Instance);

    // Move the tips back on screen when they run off it. The game ran them before the added
    // tip made the stack taller, or, for items without keyword tips, wider than empty.
    private static readonly MethodInfo? CorrectVerticalOverflow = typeof(NHoverTipSet).GetMethod("CorrectVerticalOverflow", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? CorrectHorizontalOverflow = typeof(NHoverTipSet).GetMethod("CorrectHorizontalOverflow", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly HashSet<NHoverTipSet> Pending = new();
    private static PackedScene? _scene;

    public static void OnNodeAdded(Node node)
    {
        if (node is NHoverTipSet set)
            Pending.Add(set);
    }

    // A tip set is added to the tree before the game gives it its owner and tips, so it is
    // handled on the next frame.
    public static void OnProcessFrame(string? characterId)
    {
        if (Pending.Count == 0)
            return;
        foreach (var set in Pending)
        {
            if (!GodotObject.IsInstanceValid(set) || !set.IsInsideTree() || set.HasMeta(DoneMeta))
                continue;
            set.SetMeta(DoneMeta, true);
            if (OwnerField?.GetValue(set) is not Control owner || ContainerField?.GetValue(set) is not Container container)
                continue;
            // The owner is the hovered item or its holder. Its badge says what it shows.
            if (owner.FindChild(Badges.BadgeName, recursive: true, owned: false) is not Control badge)
                continue;
            var itemId = badge.GetMeta("item_id", "").AsString();
            var isCard = badge.GetParent() is NCard;
            var stats = Badges.StatsFor(badge);
            var added = false;
            if (stats != null && Settings.StatsTips)
            {
                AddStatsTip(container, itemId, stats, characterId);
                added = true;
            }
            if (Settings.Commentary && TierLists.TryGet(characterId, itemId, out var entry, isCard))
            {
                AddTip(container, itemId, entry);
                added = true;
            }
            if (added)
            {
                CorrectVerticalOverflow?.Invoke(set, null);
                CorrectHorizontalOverflow?.Invoke(set, null);
            }
        }
        Pending.Clear();
    }

    // The lists' commentary is typed like a typewriter: two spaces after a sentence
    // (Baalorlord) and a blank line between paragraphs (Jorbs). One space, and a paragraph
    // starts on the next line: the tip is narrow.
    private static string Tidy(string commentary)
    {
        var lines = commentary.Replace("\r\n", "\n").Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    // The author's commentary, with the tier as a badge in the tip's top right corner.
    private static void AddTip(Container container, string itemId, TierEntry entry)
    {
        var text = string.IsNullOrWhiteSpace(entry.Commentary) ? "" : Tidy(entry.Commentary).Replace("[", "[lb]");
        // A flag in the list, shown in the green the game gives upgraded card names.
        if (entry.GoodUpgrade)
            text += (text == "" ? "" : "\n") + $"[color=#{StsColors.green.ToHtml(includeAlpha: false)}]Good upgrade[/color]";
        var tip = AddTip(container, TipGroup, itemId, string.IsNullOrEmpty(entry.Author) ? "Tier list" : entry.Author, text);
        // On the title's line, at the tip's right edge: as far in as the title is from the left,
        // plus room for the badge's pointed end.
        // The tip lays out its children. The title, a label, leaves its own alone.
        var title = tip.GetNode<MegaLabel>("%Title");
        var badge = Badges.ForTip(Badges.ForTier(itemId, entry, upgrading: false), title.GetThemeFont("font"));
        title.AddChild(badge);
        void Place()
        {
            var inset = Vector2.Zero;
            for (Node n = title; n != tip; n = n.GetParent())
                inset += ((Control)n).Position;
            var size = badge.GetCombinedMinimumSize();
            badge.Size = size;
            badge.Position = new Vector2(tip.Size.X - 2 * inset.X - size.X - size.Y / 2, (title.Size.Y - size.Y) / 2);
        }
        Place();
        tip.ItemRectChanged += Place;
        title.ItemRectChanged += Place;
    }

    private static string Colour(Color c) => "#" + c.ToHtml(includeAlpha: false);

    private static readonly string Dim = Colour(new Color("#a0a8aa"));

    private static readonly string Heading = Colour(StsColors.gold);
    private static readonly System.Globalization.CultureInfo Numbers = System.Globalization.CultureInfo.InvariantCulture;

    // One tip, your numbers first, then Untapped.gg's, each where there are some. Lines as
    // players read them: how often the item was taken (or, never taken, how often passed over),
    // then (yours) how many of your wins ended with it, or (Untapped.gg's) how the act's and the
    // run's winrate changed when players took it, and (if wanted) what that is based on.
    private static void AddStatsTip(Container container, string itemId, StatsInfo info, string? characterId)
    {
        var (kind, global, act, mine) = info;
        var removal = itemId == TierListOverlayMod.RemoveId;
        var (title, took, offers) = kind switch
        {
            StatsKind.Skip => ("Skip Stats", "Picked instead of a card", "Card Rewards"),
            StatsKind.Ancient => ("Ancient Choice Stats", "Picked when offered", "Ancient Choices"),
            StatsKind.Shop => ("Shop Stats", "Bought when offered", "Shops"),
            StatsKind.Upgrade => ("Card Upgrade Stats", "Picked to upgrade", "Smith Events"),
            _ => ("Card Reward Stats", "Picked when offered", "Card Rewards"),
        };
        // Both are the character's stats: "Your Ironclad runs", "Other players' Ironclad runs".
        var character = characterId == null ? "" : Capital(characterId.ToLowerInvariant()) + " ";
        var lines = new List<string>();
        if (mine != null)
        {
            lines.Add($"[color={Heading}]Your {character}runs[/color]");
            var verb = kind switch
            {
                // "Skipped" on the Skip button reads both ways.
                StatsKind.Skip => "picked Skip",
                _ when removal => "removed",
                StatsKind.Shop => "bought",
                _ => "picked",
            };
            if (mine.Unpicked)
            {
                lines.Add(mine.Offered == 0 ? "Never offered yet" : $"Never {verb} in {mine.Offered} {(mine.Offered == 1 ? "offer" : "offers")}");
                if (mine.LowerRuns > 0)
                    lines.Add($"But {verb} in {mine.LowerRuns} {(mine.LowerRuns == 1 ? "run" : "runs")} below Ascension 7");
            }
            else
            {
                var share = Badges.Percent(100.0 * mine.Taken / mine.Offered);
                lines.Add($"{Capital(verb)} {mine.Taken} of {mine.Offered} ({share})");
                if (mine.InWins is { } inWins)
                    lines.Add($"In {inWins} of your {mine.Wins} {(mine.Wins == 1 ? "win" : "wins")}");
            }
            if (mine.OthersOffered > 0)
                lines.Add($"[color={Dim}]Other characters: {mine.OthersTaken} of {mine.OthersOffered}[/color]");
            if (Settings.StatsSamples)
                lines.Add($"[color={Dim}]Based on {mine.Runs.ToString("N0", Numbers)} runs at Ascension 7+[/color]");
        }
        if (global != null)
        {
            lines.Add(global.AllCharacters
                ? $"[color={Heading}]Other players, all characters[/color]"
                : $"[color={Heading}]Other players' {character}runs[/color]");
            lines.Add($"{Badges.Percent(global.ActionPct!.Value)} {took}");
            // The server leaves out winrates until enough players took the item.
            var showAct = act < 3;
            if (global.RunDelta == null && (global.ActDelta == null || !showAct))
                lines.Add($"[color={Dim}]Winrates: too little data[/color]");
            else
            {
                if (showAct)
                    lines.Add(Winrate(global.ActDelta, global.ActTier, "Act Winrate"));
                lines.Add(Winrate(global.RunDelta, global.RunTier, "Run Winrate"));
            }
            if (Settings.StatsSamples)
                lines.Add($"[color={Dim}]Based on {global.TimesOffered.ToString("N0", Numbers)} Act {Math.Min(act, 3)} {offers} at Ascension 7+[/color]");
        }
        AddTip(container, StatsTipGroup, itemId, title, string.Join("\n", lines));
    }

    private static string Capital(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private static string Winrate(double? delta, string? tier, string label) =>
        delta == null
            ? $"[color={Dim}]{label}: too little data[/color]"
            : $"[color={Colour((Badges.TierColour(tier) ?? new Color("#888888")).Lightened(0.25f))}]{Badges.Percent(delta.Value, signed: true)}[/color] {label}";

    /// <summary>A setting's explanation, where the game shows its own settings' tips.</summary>
    public static void ShowForSetting(Control row, string title, string text)
    {
        // The row's control can take the mouse from the row and back. One tip at a time.
        NHoverTipSet.Remove(row);
        var set = NHoverTipSet.CreateAndShow(row, Array.Empty<IHoverTip>());
        if (set == null || ContainerField?.GetValue(set) is not Container container)
            return;
        set.SetMeta(DoneMeta, true);
        AddTip(container, SettingTipGroup, row.Name, title, text);
        set.GlobalPosition = row.GlobalPosition + MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen.settingTipsOffset;
        // Left just enough to stay on screen, should a title make the tip wider. (The game's
        // own correction moves it onto the row's controls.)
        var overflow = container.GetGlobalRect().End.X - row.GetViewportRect().Size.X;
        if (overflow > 0)
            set.GlobalPosition += Vector2.Left * overflow;
    }

    /// <summary>
    /// The game shows no hover tips for the card reward's Skip button. While it has stats, give
    /// it a tip set of its own for the stats tip.
    /// </summary>
    public static void ShowOnFocus(NClickableControl button)
    {
        if (button.HasMeta(FocusMeta))
            return;
        button.SetMeta(FocusMeta, true);
        button.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(b =>
        {
            if (Settings.StatsTips && b.GetNodeOrNull<Control>(Badges.BadgeName) is { } badge && Badges.StatsFor(badge) != null)
                NHoverTipSet.CreateAndShow(b, Array.Empty<IHoverTip>(), HoverTipAlignment.Right);
        }));
        button.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(NHoverTipSet.Remove));
    }

    private static Control AddTip(Container container, string group, string itemId, string title, string text)
    {
        _scene ??= ResourceLoader.Load<PackedScene>(TipScene);
        var tip = _scene.Instantiate<Control>();
        container.AddChild(tip);
        // Lets tooling (tools/DevTools) find the tip and check what it is for.
        tip.AddToGroup(group);
        tip.SetMeta("item_id", itemId);

        tip.GetNode<MegaLabel>("%Title").SetTextAutoSize(title);
        tip.GetNode<MegaRichTextLabel>("%Description").Text = text;
        tip.GetNode<TextureRect>("%Icon").Texture = null;
        tip.ResetSize();
        // The game sizes the container itself as it adds tips (NHoverTipSet.Init).
        var widened = 360f - container.Size.X;
        container.Size = new Vector2(360f, container.Size.Y + tip.Size.Y + 5f);
        // Tips shown left of their owner (ReverseFill, NHoverTipSet.SetAlignment) were moved
        // left by the container's width before the added tip widened it, which is none for
        // items without keyword tips. The tip would then cover the item.
        if (container is FlowContainer { ReverseFill: true } && widened > 0)
            container.Position += Vector2.Left * widened;
        return tip;
    }
}
