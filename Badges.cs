using Godot;

namespace TierListOverlay;

internal enum Placement
{
    // Inside the bottom edge: cards, whose frame leaves room above the border.
    InsideBottom,
    // Across the top edge: relic and potion icons, which have their price below.
    AcrossTop,
    // Inside the right edge, vertically centred: single-line buttons (loot).
    RightEdge,
    // Inside the top right corner, level with the title: two-line buttons (ancient options),
    // whose description can run to the right edge.
    TopRight,
}

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
    public static void Sync(Control item, (string Id, TierEntry Entry)? rated, Rect2 bounds, Placement placement)
    {
        var existing = item.GetNodeOrNull<Control>(BadgeName);
        if (existing != null)
        {
            if (rated is var (id, entry) && existing.GetMeta("item_id", "").AsString() == id && existing.GetMeta("tier", "").AsString() == entry.Tier)
            {
                Place(item, existing, bounds, placement);
                return;
            }
            item.RemoveChild(existing);
            existing.QueueFree();
        }
        if (rated is var (newId, newEntry))
        {
            GD.Print($"[TierListOverlay] {newId}: {newEntry.Tier} — {newEntry.Commentary}");
            var badge = Create(newId, newEntry, placement);
            // No ZIndex: the badge must stay in the item's own draw order so hover tips cover it.
            item.AddChild(badge);
            Place(item, badge, bounds, placement);
        }
    }

    private static Control Create(string itemId, TierEntry entry, Placement placement)
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

        // Letter tiers read as "A-Tier" on cards and as plain "A" on the smaller relics and
        // potions; named tiers ("Always Amazing") stand on their own.
        var text = entry.Tier.Length <= 2 && placement == Placement.InsideBottom ? $"{entry.Tier}-Tier" : entry.Tier;
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 22);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 6);
        badge.AddChild(label);
        return badge;
    }

    private static void Place(Control item, Control badge, Rect2 bounds, Placement placement)
    {
        // Icons are drawn at very different scales (small in the shop, large in a chest), so
        // their badges undo the icon's scale and keep one size on screen.
        if (placement == Placement.AcrossTop)
            badge.Scale = Vector2.One / item.GetGlobalTransform().Scale;
        var size = badge.GetCombinedMinimumSize() * badge.Scale;
        var centreX = bounds.GetCenter().X - size.X / 2;
        badge.Position = placement switch
        {
            // Clear of a card's frame border.
            Placement.InsideBottom => new Vector2(centreX, bounds.End.Y - size.Y - 22),
            Placement.AcrossTop => new Vector2(centreX, bounds.Position.Y - size.Y / 2),
            Placement.TopRight => new Vector2(bounds.End.X - size.X - 16, bounds.Position.Y + 22),
            _ => new Vector2(bounds.End.X - size.X - 16, bounds.GetCenter().Y - size.Y / 2),
        };
    }
}
