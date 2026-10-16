using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;

namespace TierListOverlay;

/// <summary>
/// Adds a tier badge to every card, relic and potion the game offers as a choice (rewards,
/// shop, treasure, ancients, deck grids). Harmony does not work on macOS arm64, so instead of
/// patching game methods this watches the scene tree for item nodes and keeps their badges in
/// sync with what they show.
/// </summary>
[ModInitializer("Initialize")]
public static class TierListOverlayMod
{
    // Item nodes currently in the tree. The game pools and reuses card nodes and holders and
    // can reassign a node's model, so badges are kept in sync every frame instead of added once.
    private static readonly HashSet<Control> Tracked = new();
    private static readonly HashSet<string> LoggedUnrated = new();

    // The game version the mod was last checked against (tools/check.py, docs/UPDATING.md).
    // Badges and tips depend on game internals that an update can change without a build error.
    private const string TestedGameVersion = "v0.107.1";
    private static int _checkedGeneration;

    public static void Initialize()
    {
        var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        Settings.Load(Path.Combine(modDir, "settings.cfg"));
        // Lists are made per game version; ask for the installed one ("v0.107.1").
        var gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        GD.Print($"[TierListOverlay] Game version {gameVersion ?? "unknown"}");
        if (gameVersion != null && gameVersion != TestedGameVersion)
            GD.PrintErr($"[TierListOverlay] Last tested on game {TestedGameVersion}, this is {gameVersion}: run the update routine in docs/UPDATING.md");
        TierLists.LoadInBackground(Path.Combine(modDir, "cache"), gameVersion);

        var tree = (SceneTree)Engine.GetMainLoop();
        tree.NodeAdded += OnNodeAdded;
        tree.ProcessFrame += OnProcessFrame;
        GD.Print("[TierListOverlay] Initialized");
    }

    private static void OnNodeAdded(Node node)
    {
        if (node is NCard or NRelic or NPotion or NRewardButton or NEventOptionButton)
            Tracked.Add((Control)node);
        Tips.OnNodeAdded(node);
        SettingsUi.OnNodeAdded(node);
    }

    private static void OnProcessFrame()
    {
        SettingsUi.OnProcessFrame();
        // The game's model database may not be ready in the first frames; then try again.
        if (TierLists.Generation != _checkedGeneration && LogUnknownIds())
            _checkedGeneration = TierLists.Generation;
        // A pooled node leaves the tree and is added again (and tracked again) on reuse.
        Tracked.RemoveWhere(c => !GodotObject.IsInstanceValid(c) || !c.IsInsideTree());
        if (Tracked.Count == 0)
            return;
        var characterId = CurrentCharacterId();
        Tips.OnProcessFrame(characterId);
        foreach (var node in Tracked)
        {
            try
            {
                var (itemId, placement, bounds) = Describe(node);
                Badges.Sync(node, Lookup(characterId, itemId, node is NCard), bounds, placement, node is NCard && HasAncestor<NDeckUpgradeSelectScreen>(node));
                if (node is NRelic or NPotion)
                    MakeRoomForBadge(node);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Updating badge failed: {ex}");
            }
        }
    }

    // NCard is centred on its origin and has no size of its own; NCard.defaultSize is the frame.
    private static readonly Rect2 CardBounds = new(-NCard.defaultSize / 2, NCard.defaultSize);

    // The item a node shows if it should be rated (null otherwise, also when the settings turn
    // its badges off), where its badge goes, and the node's visible rectangle in its own
    // coordinates.
    private static (string? ItemId, Placement Placement, Rect2 Bounds) Describe(Control node)
    {
        var own = new Rect2(Vector2.Zero, node.Size);
        if (node is NCard card)
        {
            var on = CardScreen(card) switch
            {
                CardScreenKind.Choice => Settings.CardChoices,
                CardScreenKind.Deck => Settings.DeckScreens,
                _ => false,
            };
            return (on ? card.Model?.Id.Entry : null, Placement.InsideBottom, CardBounds);
        }
        if (!Settings.Relics)
            return (null, Placement.AcrossBottom, own);
        return node switch
        {
            NRelic relic when HasAncestor<NMerchantInventory>(relic) || HasAncestor<NTreasureRoomRelicCollection>(relic) || HasAncestor<NChooseARelicSelection>(relic)
                => (relic.Model.Id.Entry, Placement.AcrossBottom, own),
            NPotion potion when HasAncestor<NMerchantInventory>(potion) => (potion.Model.Id.Entry, Placement.AcrossBottom, own),
            // Elite and event loot: the relic or potion is only an icon on the button.
            NRewardButton { Reward: RelicReward r } => (r.Relic?.Id.Entry, Placement.RightEdge, own),
            NRewardButton { Reward: PotionReward p } => (p.Potion?.Id.Entry, Placement.RightEdge, own),
            // Ancients (Neow, Darv, …) offer relics as event options.
            NEventOptionButton option => (option.Option?.Relic?.Id.Entry, Placement.TopCentre, own),
            _ => (null, Placement.InsideBottom, own),
        };
    }

    // Lists can name items the game does not have (renamed, removed, or from a newer game
    // version); they never get a badge, so say which ones once per load.
    private static bool LogUnknownIds()
    {
        if (TierLists.Generation == 0)
            return false;
        try
        {
            var known = new Dictionary<string, HashSet<string>>
            {
                ["cards"] = ModelDb.AllCards.Select(c => c.Id.Entry).ToHashSet(),
                ["relics"] = ModelDb.AllRelics.Select(r => r.Id.Entry).ToHashSet(),
                ["potions"] = ModelDb.AllPotions.Select(p => p.Id.Entry).ToHashSet(),
            };
            var unknown = TierLists.All()
                .Where(e => known.TryGetValue(e.Entry.Kind, out var ids) && !ids.Contains(e.ItemId))
                .GroupBy(e => e.List)
                .ToList();
            foreach (var list in unknown)
                GD.Print($"[TierListOverlay] Unknown IDs in {list.Key}: {string.Join(", ", list.Select(e => e.ItemId).OrderBy(id => id))}");
            if (unknown.Count == 0)
                GD.Print("[TierListOverlay] All tier list IDs are known to the game");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TierListOverlay] Checking tier list IDs failed: {ex.Message}");
            return false;
        }
    }

    // In the shop a relic's or potion's price sits right below the icon, where the badge goes;
    // move it down while there is a badge.
    private const string CostOffsetMeta = "tierlist_cost_offset";

    private static void MakeRoomForBadge(Control icon)
    {
        var slot = icon.GetParent()?.GetParent() as NMerchantSlot;
        if (slot?.GetNodeOrNull<Control>("Cost") is not { } cost)
            return;
        var badge = icon.GetNodeOrNull<Control>(Badges.BadgeName);
        var offset = 0f;
        if (badge != null)
        {
            // The badge's height in the slot's units, minus the part that overlaps the icon,
            // plus a small gap.
            var scale = badge.GetGlobalTransform().Scale.Y / slot.GetGlobalTransform().Scale.Y;
            offset = Mathf.Round(badge.Size.Y * scale * 0.5f + 4);
        }
        var applied = cost.HasMeta(CostOffsetMeta) ? cost.GetMeta(CostOffsetMeta).AsSingle() : 0f;
        if (Mathf.IsEqualApprox(offset, applied))
            return;
        cost.Position += new Vector2(0, offset - applied);
        cost.SetMeta(CostOffsetMeta, offset);
    }

    private static (string Id, TierEntry Entry)? Lookup(string? characterId, string? itemId, bool isCard)
    {
        if (itemId == null)
            return null;
        if (TierLists.TryGet(characterId, itemId, out var entry, isCard))
            return (itemId, entry);
        if (LoggedUnrated.Add(itemId))
            GD.Print($"[TierListOverlay] No tier for {itemId}");
        return null;
    }

    // The local player's character ("IRONCLAD"), or null outside a run.
    private static string? CurrentCharacterId()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        return state == null ? null : LocalContext.GetMe(state)?.Character.Id.Entry;
    }

    private static bool HasAncestor<T>(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            if (n is T)
                return true;
        }
        return false;
    }

    private enum CardScreenKind
    {
        None,
        // Picking or buying new cards.
        Choice,
        // Working on the deck.
        Deck,
    }

    // Screens where the player picks, buys or works on cards. Not combat piles and hands, and
    // not the inspect screen (a single enlarged card opened from one of these).
    private static CardScreenKind CardScreen(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            switch (n)
            {
                case NInspectCardScreen:
                    return CardScreenKind.None;
                case NCardRewardSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen:
                case NMerchantInventory:
                case NSimpleCardSelectScreen:
                    return CardScreenKind.Choice;
                case NDeckUpgradeSelectScreen or NDeckTransformSelectScreen or NDeckEnchantSelectScreen or NDeckCardSelectScreen:
                case NDeckViewScreen:
                    return CardScreenKind.Deck;
            }
        }
        return CardScreenKind.None;
    }
}
