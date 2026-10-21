using System;
using System.Linq;
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
    // The same on other events' option boxes, whose top edge the texture draws lower. In the
    // size of their usual right-end badges (RightEdge), which they move from.
    EventTop,
    // Centred on top of the top edge, clear of the label below: the card reward's Skip.
    AboveTop,
}

/// <summary>
/// What a badge shows: a tier ("A-Tier") or a share of offers taken, other players' or the
/// player's own ("30%", or "2/4" from few offers).
/// `Tier` is the item's tier list tier, if any. Tools read it.
/// </summary>
/// `Ink`: the text's own colour instead of the light default.
/// `Hidden`: in place but not drawn, so the item's hover tips still find it.
internal sealed record BadgeContent(string ItemId, string Kind, string Tier, Color Colour, string Label, Color? Ink = null, StatsInfo? Stats = null, bool Hidden = false);

/// <summary>
/// Badges: a flat hexagon with pointed ends, light text in the game's title font, added as a
/// child of the item's node, its face in the tier's colour or, for stats, a dark slate.
/// </summary>
internal static class Badges
{
    public const string BadgeName = "TierListOverlayBadge";
    private const string BadgeGroup = "tierlist_badge";

    public const string TierKind = "tier";
    public const string StatsKindName = "stats";

    /// <summary>Letter tiers read as "A-Tier". Named tiers ("Always Amazing") stand on their own.</summary>
    public static string TierLabel(string tier) => tier.Length <= 2 ? $"{tier}-Tier" : tier;

    /// <summary>
    /// "A-Tier" everywhere. On the upgrade screen a card worth upgrading gets a "+", as the
    /// game marks upgraded cards ("Bully+").
    /// </summary>
    public static BadgeContent ForTier(string itemId, TierEntry entry, bool upgrading) =>
        new(itemId, TierKind, entry.Tier, new Color(entry.Color),
            TierLabel(entry.Tier) + (upgrading && entry.GoodUpgrade ? " +" : ""));

    // The stats badges' own colour, a dark slate.
    private static readonly Color StatsColour = new("#3c4b4e");

    /// <summary>
    /// "29%" for a share, "+5%" or "-3%" for a change. A share that is neither none nor all
    /// never rounds to either: "&lt;1%", "&gt;99%".
    /// </summary>
    public static string Percent(double value, bool signed = false)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (!signed && rounded == 0 && value > 0)
            return "<1%";
        if (!signed && rounded == 100 && value < 100)
            return ">99%";
        return (signed && rounded >= 0 ? "+" : "") + rounded + "%";
    }

    // Below this many offers a share says little: the player's own badge shows the counts,
    // other players' none.
    public const int MinOffersForShare = 5;

    /// <summary>
    /// Other players' share of the offers they took (picked, bought or upgraded), "29%", like
    /// the player's own badge. The tip adds the winrates. `tier` is the item's tier list tier,
    /// if any. Only for global stats with data (StatEntry.HasData) from MinOffersForShare
    /// offers on.
    /// </summary>
    public static BadgeContent ForStats(string itemId, StatsInfo info, string tier) =>
        new(itemId, StatsKindName, tier, StatsColour, Percent(info.Global!.ActionPct!.Value), Stats: info);

    /// <summary>
    /// The player's own numbers, on one line without a label, as the tip spells out: the share
    /// they took of the times the item was offered ("30%"), or with fewer offers than
    /// MinOffersForShare, the counts ("2/4"). Only for items with them (StatsInfo.Mine).
    /// </summary>
    public static BadgeContent ForPersonal(string itemId, StatsInfo info, string tier)
    {
        var mine = info.Mine!;
        var text = mine.Offered < MinOffersForShare ? $"{mine.Taken}/{mine.Offered}" : Percent(100.0 * mine.Taken / mine.Offered);
        return new(itemId, StatsKindName, tier, StatsColour, text, Stats: info);
    }

    /// <summary>
    /// An item the player never took at Ascension 7+, to try: a count badge with gold text,
    /// "Unpicked". The tip says whether lower runs took it.
    /// </summary>
    public static BadgeContent ForUnpicked(string itemId, StatsInfo info, string tier) =>
        new(itemId, StatsKindName, tier, StatsColour, "Unpicked", MegaCrit.Sts2.Core.Helpers.StsColors.gold, info);

    /// <summary>No badge to see, only the stats for the item's hover tip (Skip and card removal, which have no tier).</summary>
    public static BadgeContent ForTipOnly(string itemId, StatsInfo info) =>
        new(itemId, StatsKindName, "", StatsColour, "", Stats: info, Hidden: true);

    /// <summary>
    /// Makes the item's badge match `content`: none when null, otherwise one showing it.
    /// `bounds` is the item's visible rectangle in the item node's own coordinates.
    /// </summary>
    public static void Sync(Control item, BadgeContent? content, Rect2 bounds, Placement placement)
    {
        var existing = item.GetNodeOrNull<Control>(BadgeName);
        if (existing != null)
        {
            if (content != null && existing.GetMeta("key", "").AsString() == Key(content))
            {
                if (StatsFor(existing) != content.Stats)
                    SetStats(existing, content.Stats);
                Place(item, existing, bounds, placement);
                return;
            }
            item.RemoveChild(existing);
            existing.QueueFree();
        }
        if (content != null)
        {
            GD.Print($"[TierListOverlay] {content.ItemId}: {content.Label}");
            var badge = Create(content, FontFrom(item), FontSize(placement));
            badge.Visible = !content.Hidden;
            // Lets tooling (tools/DevTools) find badges and check what they show.
            badge.AddToGroup(BadgeGroup);
            SetStats(badge, content.Stats);
            // No ZIndex: the badge must stay in the item's own draw order so hover tips cover it.
            item.AddChild(badge);
            Place(item, badge, bounds, placement);
        }
    }

    private static string Key(BadgeContent c) => $"{c.ItemId}|{c.Kind}|{c.Tier}|{c.Hidden}|{c.Colour.ToHtml()}|{c.Label}|{c.Ink?.ToHtml()}";

    // The stats a stats badge shows, for its hover tip.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, StatsInfo> StatsOf = new();

    private static void SetStats(Control badge, StatsInfo? info)
    {
        StatsOf.Remove(badge);
        if (info != null)
            StatsOf.Add(badge, info);
    }

    public static StatsInfo? StatsFor(Control badge) => StatsOf.TryGetValue(badge, out var info) ? info : null;

    // The game's card title font, taken from the first card seen, until then Godot's default.
    private static Font? _font;

    private static Font? FontFrom(Control item)
    {
        if (_font == null && item.FindChild("TitleLabel", recursive: true, owned: false) is Label title)
            _font = title.GetThemeFont("font");
        return _font;
    }

    private static int FontSize(Placement placement) => placement switch
    {
        // A bit smaller than the option's relic name.
        Placement.TopCentre or Placement.AboveTop => 18,
        Placement.AcrossBottom => 12,
        _ => 20,
    };

    public const string TipBadgeName = "TierListOverlayTipBadge";

    /// <summary>A badge for the top right corner of a hover tip, in the tip title's font.</summary>
    public static Control ForTip(BadgeContent content, Font? font)
    {
        var badge = Create(content, font ?? _font, 16);
        badge.Name = TipBadgeName;
        return badge;
    }

    private static Control Create(BadgeContent content, Font? font, int fontSize)
    {
        // Room for the pointed ends (each about half the badge's height deep) plus some air.
        var tip = fontSize * 1.35f;
        var edge = fontSize * 0.12f;
        var style = new StyleBoxEmpty { ContentMarginLeft = tip, ContentMarginRight = tip, ContentMarginTop = edge, ContentMarginBottom = edge };
        var badge = new PanelContainer { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel", style);
        badge.SetMeta("item_id", content.ItemId);
        badge.SetMeta("tier", content.Tier);
        badge.SetMeta("kind", content.Kind);
        badge.SetMeta("label", content.Label);
        badge.SetMeta("key", Key(content));
        badge.AddChild(Text(content.Label, font, fontSize, content.Colour, content.Ink));

        var rim = Mathf.Max(1.5f, fontSize / 10f);
        // A container draws itself before its children, so the hexagon ends up behind the text.
        badge.Draw += () => DrawBadge(badge, content.Colour, rim);
        return badge;
    }

    private static Label Text(string text, Font? font, int size, Color colour, Color? textColour)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        if (font != null)
            label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", textColour ?? new Color(1f, 0.97f, 0.9f));
        label.AddThemeColorOverride("font_outline_color", colour.Darkened(0.75f));
        label.AddThemeConstantOverride("outline_size", Mathf.Max(2, size / 4));
        return label;
    }

    // A dark rim, then the face with a top-to-bottom gradient. Flat, without shine.
    private static void DrawBadge(Control badge, Color colour, float rim)
    {
        var size = badge.Size;
        badge.DrawColoredPolygon(Hexagon(size, 0), colour.Darkened(0.55f));
        DrawShaded(badge, Hexagon(size, rim), size.Y, colour.Lightened(0.22f), colour.Darkened(0.12f));
    }

    // Fills the polygon with a gradient from `top` at y = 0 to `bottom` at y = height.
    private static void DrawShaded(Control badge, Vector2[] polygon, float height, Color top, Color bottom)
    {
        var colours = polygon.Select(p => top.Lerp(bottom, Mathf.Clamp(p.Y / height, 0, 1))).ToArray();
        badge.DrawPolygon(polygon, colours);
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
        for (Node? n = item; n != null; n = n.GetParent())
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
        // grow with a shop slot's hover zoom, like the slot's price.
        if (placement == Placement.AcrossBottom)
            badge.Scale = HoverZoom(item) / item.GetGlobalTransform().Scale;
        var size = badge.GetCombinedMinimumSize() * badge.Scale;
        var centreX = bounds.GetCenter().X - size.X / 2;
        badge.Position = placement switch
        {
            // The pointed ends on the frame's light inner border line, whose middle is 28.6 of
            // the card's 422 units above its bottom edge (measured in screenshots, and check.py's
            // deck_view scenario checks it).
            Placement.InsideBottom => new Vector2(centreX, bounds.End.Y - 28.6f - size.Y / 2),
            Placement.AcrossBottom => new Vector2(centreX, bounds.End.Y - size.Y * 0.55f),
            // The pointed ends on the option box's visible top edge, which the texture draws
            // 8.5% of the box's height below the node's top.
            Placement.TopCentre => new Vector2(centreX, bounds.Position.Y + bounds.Size.Y * 0.085f - size.Y / 2),
            // On an event option box, 11.7% (measured in screenshots, and check.py's
            // personal_event scenario checks it).
            Placement.EventTop => new Vector2(centreX, bounds.Position.Y + bounds.Size.Y * 0.117f - size.Y / 2),
            // Resting on the button's visible top edge, which the texture draws 14% of the
            // button's height below the node's top.
            Placement.AboveTop => new Vector2(centreX, bounds.Position.Y + bounds.Size.Y * 0.14f - size.Y),
            _ => new Vector2(RightEdgeLeft(bounds.End, badge), bounds.GetCenter().Y - size.Y / 2),
        };
    }

    /// <summary>Where a badge's left edge goes inside the right edge of an item ending at `end`.</summary>
    public static float RightEdgeLeft(Vector2 end, Control badge) => end.X - badge.GetCombinedMinimumSize().X * badge.Scale.X - 16;
}
