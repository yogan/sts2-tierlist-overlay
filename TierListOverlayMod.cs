using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace TierListOverlay;

/// <summary>
/// Adds a tier badge to every card the game offers as a choice (rewards, shop, deck grids).
/// Harmony does not work on macOS arm64, so instead of patching game methods this watches
/// the scene tree for cards and keeps their badges in sync with what they show.
/// </summary>
[ModInitializer("Initialize")]
public static class TierListOverlayMod
{
    // Cards currently in the tree. The game pools and reuses card nodes and holders and can
    // reassign a card's model, so badges are kept in sync every frame instead of added once.
    private static readonly HashSet<NCard> TrackedCards = new();
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
        if (node is NCard card)
            TrackedCards.Add(card);
    }

    private static void OnProcessFrame()
    {
        // A pooled card leaves the tree and is added again (and tracked again) on reuse.
        TrackedCards.RemoveWhere(c => !GodotObject.IsInstanceValid(c) || !c.IsInsideTree());
        if (TrackedCards.Count == 0)
            return;
        var characterId = CurrentCharacterId();
        foreach (var card in TrackedCards)
        {
            try
            {
                var cardId = IsOnCardChoiceScreen(card) ? card.Model?.Id.Entry : null;
                Badges.Sync(card, Lookup(characterId, cardId), CardBounds);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Updating badge failed: {ex}");
            }
        }
    }

    // NCard is centred on its origin and has no size of its own; NCard.defaultSize is the frame.
    private static readonly Rect2 CardBounds = new(-NCard.defaultSize / 2, NCard.defaultSize);

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
