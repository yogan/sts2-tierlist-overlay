using Godot;

namespace TierListOverlay;

internal enum Placement
{
    // Inside the bottom edge: cards, whose frame leaves room above the border.
    InsideBottom,
    // Across the bottom edge, just above the price: relic and potion icons.
    AcrossBottom,
    // Inside the right edge, vertically centred: single-line buttons (loot).
    RightEdge,
    // Centred across the top edge: wide two-line buttons (ancient options).
    TopCentre,
}

/// <summary>
/// Tier badges: a flat hexagon with pointed ends in the tier's colour, light text in the
/// game's title font ("A-Tier"), added as a child of the rated item's node.
/// </summary>
internal static class Badges
{
    public const string BadgeName = "TierListOverlayBadge";
    private const string BadgeGroup = "tierlist_badge";

    /// <summary>Letter tiers read as "A-Tier"; named tiers ("Always Amazing") stand on their own.</summary>
    public static string TierLabel(string tier) => tier.Length <= 2 ? $"{tier}-Tier" : tier;

    // "A-Tier" everywhere. On the upgrade screen a card worth upgrading gets a "+", as the
    // game marks upgraded cards ("Bully+").
    private static string Label(TierEntry entry, Placement placement, bool upgrading) =>
        TierLabel(entry.Tier) + (upgrading && entry.GoodUpgrade ? " +" : "");

    /// <summary>
    /// Makes the item's badge match `rated`: none when null, otherwise one showing its tier.
    /// `bounds` is the item's visible rectangle in the item node's own coordinates.
    /// </summary>
    public static void Sync(Control item, (string Id, TierEntry Entry)? rated, Rect2 bounds, Placement placement, bool upgrading = false)
    {
        var existing = item.GetNodeOrNull<Control>(BadgeName);
        if (existing != null)
        {
            if (rated is var (id, entry) && existing.GetMeta("item_id", "").AsString() == id && existing.GetMeta("tier", "").AsString() == entry.Tier
                && existing.GetMeta("label", "").AsString() == Label(entry, placement, upgrading))
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
            var badge = Create(item, newId, newEntry, Label(newEntry, placement, upgrading), placement);
            // No ZIndex: the badge must stay in the item's own draw order so hover tips cover it.
            item.AddChild(badge);
            Place(item, badge, bounds, placement);
        }
    }

    // The game's card title font, taken from the first card seen; until then Godot's default.
    private static Font? _font;

    private static Font? FontFrom(Control item)
    {
        if (_font == null && item.FindChild("TitleLabel", recursive: true, owned: false) is Label title)
            _font = title.GetThemeFont("font");
        return _font;
    }

    private static Control Create(Control item, string itemId, TierEntry entry, string text, Placement placement)
    {
        var fontSize = placement switch
        {
            // A bit smaller than the option's relic name.
            Placement.TopCentre => 18,
            Placement.AcrossBottom => 12,
            _ => 20,
        };
        // Room for the pointed ends (each about half the badge's height deep) plus some air.
        var tip = fontSize * 1.35f;
        var edge = fontSize * 0.12f;
        var style = new StyleBoxEmpty { ContentMarginLeft = tip, ContentMarginRight = tip, ContentMarginTop = edge, ContentMarginBottom = edge };
        var badge = new PanelContainer { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel", style);
        var colour = new Color(entry.Color);
        // A container draws itself before its children, so the hexagon ends up behind the text.
        var rim = Mathf.Max(1.5f, fontSize / 10f);
        badge.Draw += () => DrawHexagon(badge, colour, rim);
        // Lets tooling (tools/DevTools) find badges and check what they show.
        badge.AddToGroup(BadgeGroup);
        badge.SetMeta("item_id", itemId);
        badge.SetMeta("tier", entry.Tier);
        badge.SetMeta("label", text);

        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        if (FontFrom(item) is { } font)
            label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.97f, 0.9f));
        label.AddThemeColorOverride("font_outline_color", colour.Darkened(0.75f));
        label.AddThemeConstantOverride("outline_size", Mathf.Max(2, fontSize / 4));
        badge.AddChild(label);
        return badge;
    }

    // A dark rim, then the face with a top-to-bottom gradient; flat, like the app's badges.
    private static void DrawHexagon(Control badge, Color colour, float rim)
    {
        var size = badge.Size;
        badge.DrawColoredPolygon(Hexagon(size, 0), colour.Darkened(0.55f));
        var top = colour.Lightened(0.22f);
        var bottom = colour.Darkened(0.12f);
        var middle = top.Lerp(bottom, 0.5f);
        badge.DrawPolygon(Hexagon(size, rim), new[] { top, top, middle, bottom, bottom, middle });
    }

    // The hexagon's pointed ends are 45° wedges half the height deep. With `inset`, every edge
    // moves inwards by the same distance, so the rim is equally thick all round (on a 45° edge
    // that is a horizontal shift of inset × √2).
    private static Vector2[] Hexagon(Vector2 size, float inset)
    {
        var (w, h) = (size.X, size.Y);
        var tip = inset * Mathf.Sqrt2;
        var corner = h / 2 - inset + tip;
        return new[]
        {
            new Vector2(corner, inset), new Vector2(w - corner, inset), new Vector2(w - tip, h / 2),
            new Vector2(w - corner, h - inset), new Vector2(corner, h - inset), new Vector2(tip, h / 2),
        };
    }

    // A shop slot is drawn at 0.65 and zooms to 0.8 while hovered (NMerchantSlot._smallScale).
    private const float ShopSlotScale = 0.65f;

    private static Vector2 HoverZoom(Control item)
    {
        for (var n = item.GetParent(); n != null; n = n.GetParent())
        {
            if (n is MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantSlot slot)
                return slot.Scale / ShopSlotScale;
        }
        return Vector2.One;
    }

    private static void Place(Control item, Control badge, Rect2 bounds, Placement placement)
    {
        // Icons are drawn at very different scales (small in the shop, large in a chest), so
        // their badges undo the icon's scale and keep one size on screen, except that they
        // grow with a shop slot's hover zoom, as in the app.
        if (placement == Placement.AcrossBottom)
            badge.Scale = HoverZoom(item) / item.GetGlobalTransform().Scale;
        var size = badge.GetCombinedMinimumSize() * badge.Scale;
        var centreX = bounds.GetCenter().X - size.X / 2;
        badge.Position = placement switch
        {
            // The pointed ends on the frame's light inner border line, whose middle is 28.6 of
            // the card's 422 units above its bottom edge (measured in screenshots; check.py's
            // deck_view scenario checks it).
            Placement.InsideBottom => new Vector2(centreX, bounds.End.Y - 28.6f - size.Y / 2),
            Placement.AcrossBottom => new Vector2(centreX, bounds.End.Y - size.Y * 0.55f),
            // The pointed ends on the option box's visible top edge, which the texture draws
            // 8.5% of the box's height below the node's top.
            Placement.TopCentre => new Vector2(centreX, bounds.Position.Y + bounds.Size.Y * 0.085f - size.Y / 2),
            _ => new Vector2(bounds.End.X - size.X - 16, bounds.GetCenter().Y - size.Y / 2),
        };
    }
}
