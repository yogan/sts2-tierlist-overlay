using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
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

    public static void Initialize()
    {
        var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        TierLists.LoadInBackground(Path.Combine(modDir, "cache"));

        var tree = (SceneTree)Engine.GetMainLoop();
        tree.NodeAdded += OnNodeAdded;
        tree.ProcessFrame += OnProcessFrame;
        GD.Print("[TierListOverlay] Initialized");
    }

    private static void OnNodeAdded(Node node)
    {
        if (node is NCard or NRelic or NPotion or NRewardButton or NEventOptionButton)
            Tracked.Add((Control)node);
    }

    private static void OnProcessFrame()
    {
        // A pooled node leaves the tree and is added again (and tracked again) on reuse.
        Tracked.RemoveWhere(c => !GodotObject.IsInstanceValid(c) || !c.IsInsideTree());
        if (Tracked.Count == 0)
            return;
        var characterId = CurrentCharacterId();
        foreach (var node in Tracked)
        {
            try
            {
                var (itemId, placement, bounds) = Describe(node);
                Badges.Sync(node, Lookup(characterId, itemId), bounds, placement);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Updating badge failed: {ex}");
            }
        }
    }

    // NCard is centred on its origin and has no size of its own; NCard.defaultSize is the frame.
    private static readonly Rect2 CardBounds = new(-NCard.defaultSize / 2, NCard.defaultSize);

    // The item a node shows if it should be rated (null otherwise), where its badge goes, and
    // the node's visible rectangle in its own coordinates.
    private static (string? ItemId, Placement Placement, Rect2 Bounds) Describe(Control node)
    {
        var own = new Rect2(Vector2.Zero, node.Size);
        return node switch
        {
            NCard card => (IsOnCardChoiceScreen(card) ? card.Model?.Id.Entry : null, Placement.InsideBottom, CardBounds),
            NRelic relic when HasAncestor<NMerchantInventory>(relic) || HasAncestor<NTreasureRoomRelicCollection>(relic) || HasAncestor<NChooseARelicSelection>(relic)
                => (relic.Model.Id.Entry, Placement.AcrossTop, own),
            NPotion potion when HasAncestor<NMerchantInventory>(potion) => (potion.Model.Id.Entry, Placement.AcrossTop, own),
            // Elite and event loot: the relic or potion is only an icon on the button.
            NRewardButton { Reward: RelicReward r } => (r.Relic?.Id.Entry, Placement.RightEdge, own),
            NRewardButton { Reward: PotionReward p } => (p.Potion?.Id.Entry, Placement.RightEdge, own),
            // Ancients (Neow, Darv, …) offer relics as event options.
            NEventOptionButton option => (option.Option?.Relic?.Id.Entry, Placement.TopRight, own),
            _ => (null, Placement.InsideBottom, own),
        };
    }

    private static (string Id, TierEntry Entry)? Lookup(string? characterId, string? itemId)
    {
        if (itemId == null)
            return null;
        if (TierLists.TryGet(characterId, itemId, out var entry))
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

    // Screens where the player picks, buys or works on cards. Not combat piles and hands, and
    // not the inspect screen (a single enlarged card opened from one of these).
    private static bool IsOnCardChoiceScreen(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            switch (n)
            {
                case NInspectCardScreen:
                    return false;
                case NCardRewardSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen:
                case NMerchantInventory:
                case NDeckUpgradeSelectScreen or NDeckTransformSelectScreen or NDeckEnchantSelectScreen or NDeckCardSelectScreen or NSimpleCardSelectScreen:
                case NDeckViewScreen:
                    return true;
            }
        }
        return false;
    }
}
