using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;

namespace DevTools;

/// <summary>
/// Describes what is currently on screen in a form tests can assert on: cards, relics and
/// potions with their IDs, tier badges, and open hover tips. All rectangles are in window
/// pixels (the same as screenshot pixels), as [x, y, width, height].
/// </summary>
internal static class Inspector
{
    // Groups the tier list overlay mod puts its badges and hover tips into.
    private const string BadgeGroup = "tierlist_badge";
    private const string TipGroup = "tierlist_tip";
    private const string StatsTipGroup = "tierlist_stats_tip";
    private const string HintGroup = "tierlist_hint";
    private const string TipBadgeName = "TierListOverlayTipBadge";

    public static Dictionary<string, object?> Inspect()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = tree.Root;
        var toWindow = root.GetFinalTransform();

        var cards = new List<object>();
        var relics = new List<object>();
        var potions = new List<object>();
        var hoverTips = new List<object>();
        Walk(root, node =>
        {
            if (node is not Control control || !control.IsVisibleInTree())
                return;
            switch (node)
            {
                // NCard rather than NCardHolder: shop cards have no holder.
                case NCard card:
                    // The card frame is the visible card. NCard itself has no size and other
                    // children (glow, hitbox) extend far beyond the card.
                    cards.Add(Describe(card, card.Model?.Id.Entry, toWindow, CardFrameField?.GetValue(card) as Control));
                    break;
                case NRelic relic:
                    relics.Add(Describe(relic, relic.Model?.Id.Entry, toWindow));
                    break;
                case NPotion potion:
                    potions.Add(Describe(potion, potion.Model?.Id.Entry, toWindow));
                    break;
                // Loot buttons and ancient options show a relic or potion only as an icon.
                case NRewardButton { Reward: RelicReward r } button:
                    relics.Add(Describe(button, r.Relic?.Id.Entry, toWindow));
                    break;
                case NRewardButton { Reward: PotionReward p } button:
                    potions.Add(Describe(button, p.Potion?.Id.Entry, toWindow));
                    break;
                case NEventOptionButton { Option.Relic: { } relic } option:
                    relics.Add(Describe(option, relic.Id.Entry, toWindow));
                    break;
                case NHoverTipSet tips:
                    // The set stays at the origin and positions its tip containers instead. Each
                    // tip is reported as its panel. Its textures reach a little further with a
                    // transparent shadow margin, where things behind still show.
                    foreach (var container in tips.GetChildren().OfType<Control>())
                    {
                        foreach (var tip in container.GetChildren().OfType<Control>())
                        {
                            if (tip.IsVisibleInTree() && tip.Size.X > 0 && tip.Size.Y > 0)
                                hoverTips.Add(new Dictionary<string, object?> { ["rect"] = ToArray(OwnRect(tip, toWindow)) });
                        }
                    }
                    break;
            }
        });

        var badges = tree.GetNodesInGroup(BadgeGroup).OfType<Control>().Select(badge =>
        {
            var rect = Rect(badge, toWindow);
            return new Dictionary<string, object?>
            {
                ["item_id"] = badge.GetMeta("item_id", "").AsString(),
                ["tier"] = badge.GetMeta("tier", "").AsString(),
                ["label"] = badge.GetMeta("label", "").AsString(),
                ["kind"] = badge.GetMeta("kind", "").AsString(),
                ["visible"] = badge.IsVisibleInTree(),
                ["rect"] = rect,
                ["on_screen"] = OnScreen(rect),
                ["context"] = Context(badge),
            };
        }).ToList();

        List<Dictionary<string, object?>> Tips(string group) => tree.GetNodesInGroup(group).OfType<Control>().Select(tip => new Dictionary<string, object?>
        {
            ["item_id"] = tip.GetMeta("item_id", "").AsString(),
            ["visible"] = tip.IsVisibleInTree(),
            ["rect"] = Rect(tip, toWindow),
            ["title"] = (tip.FindChild("Title", true, false) as Label)?.Text,
            ["text"] = (tip.FindChild("Description", true, false) as RichTextLabel)?.GetParsedText(),
            ["bbcode"] = (tip.FindChild("Description", true, false) as RichTextLabel)?.Text,
            // The tier badge in the tip's top right corner.
            ["badge"] = (tip.FindChild(TipBadgeName, true, false) as Control)?.GetMeta("label", "").AsString(),
            ["badge_rect"] = tip.FindChild(TipBadgeName, true, false) is Control tipBadge ? Rect(tipBadge, toWindow) : null,
        }).ToList();
        var hints = tree.GetNodesInGroup(HintGroup).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text).ToList();

        var windowSize = DisplayServer.WindowGetSize();
        var runState = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
        return new Dictionary<string, object?>
        {
            ["window"] = new[] { windowSize.X, windowSize.Y },
            ["character"] = runState == null ? null : MegaCrit.Sts2.Core.Context.LocalContext.GetMe(runState)?.Character.Id.Entry,
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["badges"] = badges,
            ["hover_tips"] = hoverTips,
            ["tier_tips"] = Tips(TipGroup),
            ["stats_tips"] = Tips(StatsTipGroup),
            ["hints"] = hints,
        };
    }

    private static readonly System.Reflection.FieldInfo? CardFrameField =
        typeof(NCard).GetField("_frame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static Dictionary<string, object?> Describe(Control node, string? id, Transform2D toWindow, Control? bounds = null)
    {
        var rect = Rect(bounds ?? node, toWindow);
        return new()
        {
            ["id"] = id,
            ["type"] = node.GetType().Name,
            ["rect"] = rect,
            ["on_screen"] = OnScreen(rect),
            ["context"] = Context(node),
        };
    }

    private static bool OnScreen(float[] rect)
    {
        var window = DisplayServer.WindowGetSize();
        return rect[0] < window.X && rect[1] < window.Y && rect[0] + rect[2] > 0 && rect[1] + rect[3] > 0;
    }

    /// <summary>
    /// The subtree below the first node of a type: name, type, visibility and window rect of
    /// each node, down to `depth` levels. For finding where things are on unfamiliar screens.
    /// </summary>
    public static Dictionary<string, object?> Tree(string typeName, int depth)
    {
        var node = FindByType(typeName) ?? throw new System.ArgumentException($"no {typeName} in tree");
        var toWindow = ((SceneTree)Engine.GetMainLoop()).Root.GetFinalTransform();
        return Describe(node, depth, toWindow);
    }

    private static Dictionary<string, object?> Describe(Node node, int depth, Transform2D toWindow)
    {
        var d = new Dictionary<string, object?> { ["name"] = node.Name.ToString(), ["type"] = node.GetType().Name };
        if (node is Control c)
        {
            d["visible"] = c.IsVisibleInTree();
            d["rect"] = ToArray(OwnRect(c, toWindow));
        }
        // Many game nodes expose their state as a State property (e.g. an epoch slot's
        // NotObtained/Obtained/Complete). Report it so tools can tell such nodes apart.
        var state = node.Get("State");
        if (state.VariantType != Variant.Type.Nil)
            d["state"] = state.ToString();
        if (node is MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl clickable)
            d["enabled"] = clickable.IsEnabled;
        // What an image shows and how it is tinted, to tell look-alike nodes apart.
        if (node is TextureRect { Texture: { } texture })
            d["texture"] = texture.ResourcePath;
        if (node is CanvasItem item && (item.SelfModulate != Colors.White || item.Material is ShaderMaterial))
            d["tint"] = $"{item.SelfModulate.ToHtml()} {(item.Material as ShaderMaterial)?.Shader?.ResourcePath}"
                + (item.Material is ShaderMaterial hsv && hsv.Shader?.ResourcePath.EndsWith("hsv.gdshader") == true
                    ? $" h={hsv.GetShaderParameter("h")} s={hsv.GetShaderParameter("s")} v={hsv.GetShaderParameter("v")}" : "");
        if (node is Label l)
            d["text"] = l.Text;
        else if (node is RichTextLabel r)
            d["text"] = r.GetParsedText();
        if (depth > 0 && node.GetChildCount() > 0)
            d["children"] = node.GetChildren().Select(child => Describe(child, depth - 1, toWindow)).ToList();
        return d;
    }

    /// <summary>Finds the first node in the tree whose type has the given name.</summary>
    public static Node? FindByType(string typeName)
    {
        Node? found = null;
        Walk(((SceneTree)Engine.GetMainLoop()).Root, node =>
        {
            if (found == null && node.GetType().Name == typeName)
                found = node;
        });
        return found;
    }

    // Game node types of the ancestors, innermost first, e.g. ["NCardRow", "NCardRewardSelectionScreen", ...],
    // so tests can tell which screen something belongs to.
    private static List<string> Context(Node node)
    {
        var types = new List<string>();
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            var type = n.GetType();
            if (type.Namespace?.StartsWith("MegaCrit") == true)
                types.Add(type.Name);
        }
        return types;
    }

    // Some nodes (NCard) have zero size and draw only through their children, so fall back to
    // the union of the visible descendants' rectangles.
    private static float[] Rect(Control control, Transform2D toWindow)
    {
        var rect = OwnRect(control, toWindow);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            rect = DescendantUnion(control, toWindow) ?? rect;
        return ToArray(rect);
    }

    private static Rect2? DescendantUnion(Control control, Transform2D toWindow)
    {
        Rect2? union = null;
        Walk(control, node =>
        {
            if (node is Control c && c != control && c.IsVisibleInTree() && c.Size.X > 0 && c.Size.Y > 0)
            {
                var r = OwnRect(c, toWindow);
                union = union?.Merge(r) ?? r;
            }
        });
        return union;
    }

    private static float[] ToArray(Rect2 rect) => new[] { rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y };

    private static Rect2 OwnRect(Control control, Transform2D toWindow) =>
        toWindow * control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);

    private static void Walk(Node node, System.Action<Node> visit)
    {
        visit(node);
        foreach (var child in node.GetChildren())
            Walk(child, visit);
    }
}
