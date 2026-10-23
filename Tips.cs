using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace TierListOverlay;

/// <summary>
/// Adds the author's tier and commentary, the item's stats (the player's own and
/// Untapped.gg's), and on events the authors' advice, as more hover tips in the game's own tip
/// style whenever the game shows hover tips for the item.
/// </summary>
internal static class Tips
{
    private const string TipScene = "res://scenes/ui/hover_tip.tscn";
    private const string TipGroup = "tierlist_tip";
    private const string StatsTipGroup = "tierlist_stats_tip";
    private const string SettingTipGroup = "tierlist_setting_tip";
    private const string AdviceTipGroup = "tierlist_advice_tip";
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
            var added = false;
            // The owner is the hovered item or its holder. Its badge says what it shows.
            if (owner.FindChild(Badges.BadgeName, recursive: true, owned: false) is Control badge)
            {
                var itemId = badge.GetMeta("item_id", "").AsString();
                var isCard = badge.GetParent() is NCard;
                var stats = Badges.StatsFor(badge);
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
            }
            // Advice is on the event, so every option of it shows the same. Not on ancients'
            // relics, which have none.
            if (owner is NEventOptionButton { Option.Relic: null, Event: { } ev } option && Settings.EventAdvice
                && Advice.For(ev.Id.Entry) is var (author, advice))
            {
                AddAdviceTip(option, container, ev.Id.Entry, author, advice);
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
        // Another character's card: rated for their decks, which may need it more than yours.
        if (entry.RatedFor != null && RatedFor(entry.RatedFor) is { } ratedFor)
            text = ratedFor + (text == "" ? "" : "\n") + text;
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

    // "Rated for Defect": the character's energy icon, as on their cards, and their name in
    // the colour the game gives it. Named like in the stats tips ("Defect", not "The Defect").
    private static string? RatedFor(string list)
    {
        if (ModelDb.AllCharacters.FirstOrDefault(c => c.CardPool.Title == list) is not { } character)
            return null;
        var icon = $"[img]res://images/packed/sprite_fonts/{character.CardPool.EnergyColorName}_energy_icon.png[/img]";
        return $"{icon} [color={Dim}]Rated for[/color] [color={Colour(character.NameColor)}]{Capital(list)}[/color]";
    }

    // An author's advice on the event. Long texts (some run to a few thousand characters) get
    // what height the screen has left below the other tips and scroll with the mouse wheel
    // while the option is hovered: moving onto the tip would close it.
    private const float ScreenMargin = 20f;
    private const float MinAdviceHeight = 150f;
    // A wheel step scrolls an eighth of the text's height on screen.
    private const float ScrollSteps = 8f;
    private const string WheelMeta = "tierlist_advice_wheel";
    private static RichTextLabel? _scrollable;

    private static void AddAdviceTip(NEventOptionButton option, Container container, string eventId, string author, string advice)
    {
        var tip = AddTip(container, AdviceTipGroup, eventId, $"{author}{(author.EndsWith('s') ? "'" : "'s")} Advice", Tidy(advice).Replace("[", "[lb]"));
        var text = tip.GetNode<MegaRichTextLabel>("%Description");
        var excess = container.Size.Y - (container.GetViewportRect().Size.Y - 2 * ScreenMargin);
        var height = Math.Max(MinAdviceHeight, text.Size.Y - excess);
        if (excess > 0 && height < text.Size.Y)
        {
            var before = tip.Size.Y;
            text.FitContent = false;
            text.ScrollActive = true;
            text.CustomMinimumSize = new Vector2(text.CustomMinimumSize.X, height);
            text.Size = new Vector2(text.Size.X, height);
            tip.ResetSize();
            container.Size = new Vector2(container.Size.X, container.Size.Y - (before - tip.Size.Y));
        }
        _scrollable = text;
        if (option.HasMeta(WheelMeta))
            return;
        option.SetMeta(WheelMeta, true);
        option.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(e =>
        {
            // A mouse wheel step, or a two-finger swipe on a trackpad (a pan gesture), by as much
            // as the game's own scroll areas move.
            float? steps = e switch
            {
                InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wheel => Factor(wheel),
                InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wheel => -Factor(wheel),
                InputEventPanGesture pan => pan.Delta.Y,
                _ => null,
            };
            if (steps == null || _scrollable is not { } label || !GodotObject.IsInstanceValid(label) || !label.IsVisibleInTree() || !label.ScrollActive)
                return;
            var bar = label.GetVScrollBar();
            bar.Value += steps.Value * bar.Page / ScrollSteps;
            option.AcceptEvent();
        }));
    }

    // A wheel without steps (a smooth wheel) gives how far it turned, a plain one nothing.
    private static float Factor(InputEventMouseButton wheel) => wheel.Factor > 0 ? wheel.Factor : 1f;

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
            StatsKind.Event => ("Event Choice Stats", "", ""),
            _ => ("Card Reward Stats", "Picked when offered", "Card Rewards"),
        };
        // Both are the character's stats: "Your Ironclad runs", "Other players' Ironclad runs".
        var character = characterId == null ? "" : Capital(characterId.ToLowerInvariant()) + " ";
        var lines = new List<string>();
        if (mine != null && kind == StatsKind.Event)
            AddEventLines(lines, itemId, mine);
        else if (mine != null)
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
                    lines.Add($"{Capital(verb)} in {mine.LowerRuns} {(mine.LowerRuns == 1 ? "run" : "runs")} below A7");
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

    // An event option: in how many visits that offered it the player picked it, from all their
    // characters' runs, how many visits offered it when not all did (a later page, a locked
    // option), and why it may be missing.
    private static void AddEventLines(List<string> lines, string key, PersonalStats mine)
    {
        lines.Add($"[color={Heading}]Your runs, all characters[/color]");
        string Visits(int n) => n == 1 ? "visit" : "visits";
        if (mine.Offered == 0)
            lines.Add(mine.Visits == 0 ? "No earlier visits" : "No earlier visit offered it");
        else if (mine.Times)
            lines.Add(mine.Taken == 0
                ? $"Never picked in {mine.Offered} {(mine.Offered == 1 ? "offer" : "offers")}"
                : $"Picked {mine.Taken} of {mine.Offered} times");
        else if (mine.Taken == 0)
            lines.Add($"Never picked in {mine.Offered} {Visits(mine.Offered)}");
        else
            lines.Add($"Picked in {mine.Taken} of {mine.Offered} {Visits(mine.Offered)}");
        if (!mine.Times && mine.Offered > 0 && mine.Offered < mine.Visits)
            lines.Add($"[color={Dim}]{mine.Offered} of your {mine.Visits} visits offered it[/color]");
        if (Events.Note(key) is { } note)
            lines.Add($"[color={Dim}]{note}[/color]");
        if (Settings.StatsSamples)
            lines.Add($"[color={Dim}]Based on {mine.Runs.ToString("N0", Numbers)} runs at Ascension 7+[/color]");
    }

    private static string Capital(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    // A winrate's change in the colour of its tier, as the server rates it.
    private static readonly Dictionary<string, Color> TierColours = new()
    {
        ["S"] = new Color("#408abf"),
        ["A"] = new Color("#6ba036"),
        ["B"] = new Color("#92a036"),
        ["C"] = new Color("#a9792c"),
        ["D"] = new Color("#a04836"),
        ["F"] = new Color("#a03636"),
    };

    private static string Winrate(double? delta, string? tier, string label) =>
        delta == null
            ? $"[color={Dim}]{label}: too little data[/color]"
            : $"[color={Colour((tier != null && TierColours.TryGetValue(tier, out var c) ? c : new Color("#888888")).Lightened(0.25f))}]{Badges.Percent(delta.Value, signed: true)}[/color] {label}";

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
        // Left and up just enough to stay on screen, should a title make the tip wider or the
        // row be near the bottom. (The game's own correction moves it onto the row's controls.)
        var overflow = container.GetGlobalRect().End - row.GetViewportRect().Size + Vector2.One * ScreenMargin;
        set.GlobalPosition -= new Vector2(Math.Max(0, overflow.X - ScreenMargin), Math.Max(0, overflow.Y));
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
