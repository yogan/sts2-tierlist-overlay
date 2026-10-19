using System;
using System.Collections.Generic;
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
    // Centred on top of the top edge, clear of the label below: the card reward's Skip.
    AboveTop,
}

/// <summary>
/// One part of a badge: the value, optionally with a small label above it ("Pick"), and a
/// colour its face shades into towards the bottom (the value's tier).
/// </summary>
internal sealed record Segment(string? Label, string Text, Color? Shade = null, bool Dim = false);

/// <summary>
/// What a badge shows: a tier ("A-Tier") or stats ("Pick 29% | Act +5% | Run +1%").
/// `Tier` is the item's tier list tier, if any. Tools read it.
/// </summary>
/// `Hidden`: in place but not drawn, so the item's hover tips still find it.
internal sealed record BadgeContent(string ItemId, string Kind, string Tier, Color Colour, Segment[] Segments, StatsInfo? Stats = null, bool Hidden = false)
{
    public string Label => string.Join(" | ", Segments.Select(s => s.Label == null ? s.Text : $"{s.Label} {s.Text}"));
}

/// <summary>
/// Badges: a flat hexagon with pointed ends, light text in the game's title font, added as a
/// child of the item's node. A tier badge is one face in the tier's colour. A stats badge is
/// split into slanted parts, one per value.
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
            new[] { new Segment(null, TierLabel(entry.Tier) + (upgrading && entry.GoodUpgrade ? " +" : "")) });

    // The stats badges' own colour, a dark slate. Their winrate parts shade into the tier's
    // colour.
    private static readonly Color StatsColour = new("#3c4b4e");
    private static readonly Color DimText = new("#8a9699");

    private static readonly Dictionary<string, Color> TierColours = new()
    {
        ["S"] = new Color("#408abf"),
        ["A"] = new Color("#6ba036"),
        ["B"] = new Color("#92a036"),
        ["C"] = new Color("#a9792c"),
        ["D"] = new Color("#a04836"),
        ["F"] = new Color("#a03636"),
    };

    public static Color? TierColour(string? tier) => tier != null && TierColours.TryGetValue(tier, out var c) ? c : null;

    /// <summary>"29%" for a share, "+5%" or "-3%" for a change.</summary>
    public static string Percent(double value, bool signed = false)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return (signed && rounded >= 0 ? "+" : "") + rounded + "%";
    }

    /// <summary>
    /// Pick (or Buy, Upgr.) / Act / Run, as the stats tip spells out. The act's winrate is left
    /// out in the last act, where it is the run's. `tier` is the item's tier list tier, if any.
    /// Only for stats with data (StatEntry.HasData).
    /// </summary>
    public static BadgeContent ForStats(string itemId, StatsInfo info, string tier)
    {
        var (kind, stats, act) = info;
        var lastAct = act >= 3;
        var label = kind switch
        {
            StatsKind.Shop => "Buy",
            StatsKind.Upgrade => "Upgr.",
            _ => "Pick",
        };
        var segments = new List<Segment> { new(label, Percent(stats.ActionPct!.Value)) };
        if (!lastAct)
            segments.Add(Winrate("Act", stats.ActDelta, stats.ActTier));
        segments.Add(Winrate("Run", stats.RunDelta, stats.RunTier));
        return new(itemId, StatsKindName, tier, StatsColour, segments.ToArray(), info);
    }

    private static Segment Winrate(string label, double? delta, string? tier) =>
        delta == null ? new Segment(label, "—", Dim: true) : new Segment(label, Percent(delta.Value, signed: true), TierColour(tier));

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

    private static string Key(BadgeContent c) => $"{c.ItemId}|{c.Kind}|{c.Tier}|{c.Hidden}|{c.Colour.ToHtml()}|{string.Join("|", c.Segments.Select(s => s.ToString()))}";

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
        var split = content.Segments.Length > 1 || content.Segments[0].Label != null;
        // Room for the pointed ends (each about half the badge's height deep) plus some air.
        // A stats badge is taller (label above value), so its ends reach further.
        var tip = fontSize * (split ? 1.0f : 1.35f);
        var edge = fontSize * 0.12f;
        var style = new StyleBoxEmpty { ContentMarginLeft = tip, ContentMarginRight = tip, ContentMarginTop = edge, ContentMarginBottom = edge };
        var badge = new PanelContainer { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel", style);
        badge.SetMeta("item_id", content.ItemId);
        badge.SetMeta("tier", content.Tier);
        badge.SetMeta("kind", content.Kind);
        badge.SetMeta("label", content.Label);
        badge.SetMeta("key", Key(content));

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        badge.AddChild(row);
        foreach (var segment in content.Segments)
        {
            var part = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            var padding = split ? Mathf.RoundToInt(fontSize * 0.4f) : 0;
            part.AddThemeConstantOverride("margin_left", padding);
            part.AddThemeConstantOverride("margin_right", padding);
            var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            column.AddThemeConstantOverride("separation", -Mathf.RoundToInt(fontSize * 0.3f));
            part.AddChild(column);
            if (segment.Label != null)
                column.AddChild(Text(segment.Label, font, Mathf.RoundToInt(fontSize * 0.65f), content.Colour, outline: 2));
            column.AddChild(Text(segment.Text, font, fontSize, segment.Shade ?? content.Colour, segment.Dim ? DimText : null));
            row.AddChild(part);
        }

        var rim = Mathf.Max(1.5f, fontSize / 10f);
        // A container draws itself before its children, so the hexagon ends up behind the text.
        badge.Draw += () => DrawBadge(badge, row, content, rim);
        // The parts get their places only after the first draw. Draw again once they have.
        foreach (var part in row.GetChildren().OfType<Control>())
            part.ItemRectChanged += badge.QueueRedraw;
        return badge;
    }

    // `outline`: thinner for small text, which a thick outline smudges.
    private static Label Text(string text, Font? font, int size, Color colour, Color? textColour = null, int? outline = null)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        if (font != null)
            label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", textColour ?? new Color(1f, 0.97f, 0.9f));
        label.AddThemeColorOverride("font_outline_color", colour.Darkened(0.75f));
        label.AddThemeConstantOverride("outline_size", outline ?? Mathf.Max(2, size / 4));
        return label;
    }

    // A dark rim, then the face with a top-to-bottom gradient. Flat, without shine.
    // Parts of a stats badge shade into their own colour and are divided by slanted lines.
    private static void DrawBadge(Control badge, Control row, BadgeContent content, float rim)
    {
        var size = badge.Size;
        var colour = content.Colour;
        badge.DrawColoredPolygon(Hexagon(size, 0), colour.Darkened(0.55f));
        var face = Hexagon(size, rim);
        var top = colour.Lightened(0.22f);
        var bottom = colour.Darkened(0.12f);
        DrawShaded(badge, face, size.Y, top, bottom);

        var parts = row.GetChildren().OfType<Control>().ToArray();
        if (parts.Length < 2 && content.Segments.All(s => s.Shade == null))
            return;
        // A divider leans like the pointed ends, a quarter of the height across.
        var slant = size.Y * 0.25f;
        float Divider(int i) => row.Position.X + parts[i].Position.X;
        for (var i = 0; i < parts.Length; i++)
        {
            if (content.Segments[i].Shade is not { } shade)
                continue;
            var left = i == 0 ? -size.X : Divider(i);
            var right = i == parts.Length - 1 ? 2 * size.X : Divider(i + 1);
            var slab = new[]
            {
                new Vector2(left + slant / 2, 0), new Vector2(right + slant / 2, 0),
                new Vector2(right - slant / 2, size.Y), new Vector2(left - slant / 2, size.Y),
            };
            foreach (var piece in Geometry2D.IntersectPolygons(face, slab))
                DrawShaded(badge, piece, size.Y, top, shade.Darkened(0.1f));
        }
        var line = new Color(0, 0, 0, 0.3f);
        for (var i = 1; i < parts.Length; i++)
        {
            var x = Divider(i);
            badge.DrawLine(new Vector2(x + slant / 2 * (1 - 2 * rim / size.Y), rim), new Vector2(x - slant / 2 * (1 - 2 * rim / size.Y), size.Y - rim), line, 1.5f, antialiased: true);
        }
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
            // Resting on the button's visible top edge, which the texture draws 14% of the
            // button's height below the node's top.
            Placement.AboveTop => new Vector2(centreX, bounds.Position.Y + bounds.Size.Y * 0.14f - size.Y),
            _ => new Vector2(bounds.End.X - size.X - 16, bounds.GetCenter().Y - size.Y / 2),
        };
    }
}
