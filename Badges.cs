using Godot;

namespace TierListOverlay;

/// <summary>
/// Tier badges: a small pill in the tier's colour ("A-Tier"), added as a child of the rated
/// item's node.
/// </summary>
internal static class Badges
{
    private const string BadgeName = "TierListOverlayBadge";
    private const string BadgeGroup = "tierlist_badge";

    /// <summary>
    /// Makes the item's badge match `rated`: none when null, otherwise one showing its tier.
    /// `bounds` is the item's visible rectangle in the item node's own coordinates.
    /// </summary>
    public static void Sync(Control item, (string Id, TierEntry Entry)? rated, Rect2 bounds)
    {
        var existing = item.GetNodeOrNull<Control>(BadgeName);
        if (existing != null)
        {
            if (rated is var (id, entry) && existing.GetMeta("item_id", "").AsString() == id && existing.GetMeta("tier", "").AsString() == entry.Tier)
            {
                Place(existing, bounds);
                return;
            }
            item.RemoveChild(existing);
            existing.QueueFree();
        }
        if (rated is var (newId, newEntry))
        {
            GD.Print($"[TierListOverlay] {newId}: {newEntry.Tier} — {newEntry.Commentary}");
            var badge = Create(newId, newEntry);
            // No ZIndex: the badge must stay in the item's own draw order so hover tips cover it.
            item.AddChild(badge);
            Place(badge, bounds);
        }
    }

    private static Control Create(string itemId, TierEntry entry)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(entry.Color),
            BorderColor = new Color(0.1f, 0.08f, 0.06f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
        };
        style.SetCornerRadiusAll(14);
        style.SetBorderWidthAll(2);
        var badge = new PanelContainer { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel", style);
        // Lets tooling (tools/DevTools) find badges and check what they show.
        badge.AddToGroup(BadgeGroup);
        badge.SetMeta("item_id", itemId);
        badge.SetMeta("tier", entry.Tier);

        // Letter tiers read as "A-Tier"; named tiers ("Always Amazing") stand on their own.
        var text = entry.Tier.Length <= 2 ? $"{entry.Tier}-Tier" : entry.Tier;
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 22);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 6);
        badge.AddChild(label);
        return badge;
    }

    // Centred inside the item, just above its bottom edge (clear of a card's frame border).
    private static void Place(Control badge, Rect2 bounds)
    {
        var size = badge.GetCombinedMinimumSize();
        badge.Position = new Vector2(bounds.GetCenter().X - size.X / 2, bounds.End.Y - size.Y - 22);
    }
}
