using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace TierListOverlay;

/// <summary>
/// Adds the author's tier and commentary as one more hover tip, in the game's own tip style,
/// whenever the game shows hover tips for a rated item.
/// </summary>
internal static class Tips
{
    private const string TipScene = "res://scenes/ui/hover_tip.tscn";
    private const string TipGroup = "tierlist_tip";
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
            if (!Settings.HoverTips || OwnerField?.GetValue(set) is not Control owner || ContainerField?.GetValue(set) is not Container container)
                continue;
            // The owner is the hovered item or its holder; its badge says what it is rated.
            if (owner.FindChild(Badges.BadgeName, recursive: true, owned: false) is not Control badge)
                continue;
            var itemId = badge.GetMeta("item_id", "").AsString();
            var isCard = badge.GetParent() is NCard;
            if (TierLists.TryGet(characterId, itemId, out var entry, isCard))
            {
                AddTip(container, itemId, entry);
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

    private static void AddTip(Container container, string itemId, TierEntry entry)
    {
        _scene ??= ResourceLoader.Load<PackedScene>(TipScene);
        var tip = _scene.Instantiate<Control>();
        container.AddChild(tip);
        // Lets tooling (tools/DevTools) find the tip and check what it is for.
        tip.AddToGroup(TipGroup);
        tip.SetMeta("item_id", itemId);

        tip.GetNode<MegaLabel>("%Title").SetTextAutoSize(string.IsNullOrEmpty(entry.Author) ? "Tier list" : entry.Author);
        var colour = new Color(entry.Color).Lightened(0.3f).ToHtml(includeAlpha: false);
        var text = $"[color=#{colour}]{Badges.TierLabel(entry.Tier)}[/color]";
        if (!string.IsNullOrWhiteSpace(entry.Commentary))
            text += "\n" + Tidy(entry.Commentary).Replace("[", "[lb]");
        // A flag in the list, shown in the green the game gives upgraded card names.
        if (entry.GoodUpgrade)
            text += $"\n[color=#{StsColors.green.ToHtml(includeAlpha: false)}]Good upgrade[/color]";
        tip.GetNode<MegaRichTextLabel>("%Description").Text = text;
        tip.GetNode<TextureRect>("%Icon").Texture = null;
        tip.ResetSize();
        // The game sizes the container itself as it adds tips (NHoverTipSet.Init).
        container.Size = new Vector2(360f, container.Size.Y + tip.Size.Y + 5f);
    }
}
