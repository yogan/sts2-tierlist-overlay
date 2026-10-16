using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace TierListOverlay;

/// <summary>
/// Adds a tier badge to every card offered on card reward and card choice screens.
/// Harmony does not work on macOS arm64, so instead of patching game methods this watches
/// the scene tree for card holders and keeps their badges in sync with their cards.
/// </summary>
[ModInitializer("Initialize")]
public static class TierListOverlayMod
{
    private const string BadgeName = "TierListOverlayBadge";
    private const string BadgeGroup = "tierlist_badge";

    // Holders currently in the tree. The game pools and reuses holders (NGridCardHolder) and
    // can reassign a holder's card, so badges are kept in sync every frame instead of once.
    private static readonly HashSet<NCardHolder> TrackedHolders = new();
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
        if (node is NCardHolder holder)
            TrackedHolders.Add(holder);
    }

    private static void OnProcessFrame()
    {
        // A pooled holder leaves the tree and is added again (and tracked again) on reuse.
        TrackedHolders.RemoveWhere(h => !GodotObject.IsInstanceValid(h) || !h.IsInsideTree());
        if (TrackedHolders.Count == 0)
            return;
        var characterId = CurrentCharacterId();
        foreach (var holder in TrackedHolders)
        {
            try
            {
                SyncBadge(holder, characterId);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Updating badge failed: {ex}");
            }
        }
    }

    // Makes the holder's badge match its current card: none off the supported screens or for
    // unrated cards, otherwise one showing the card's tier.
    private static void SyncBadge(NCardHolder holder, string? characterId)
    {
        var existing = holder.GetNodeOrNull<Control>(BadgeName);
        var cardId = IsOnCardChoiceScreen(holder) ? holder.CardModel?.Id.Entry : null;
        TierEntry? entry = null;
        if (cardId != null && !TierLists.TryGet(characterId, cardId, out entry) && LoggedUnrated.Add(cardId))
            GD.Print($"[TierListOverlay] No tier for {cardId}");

        if (existing != null)
        {
            if (entry != null && existing.GetMeta("item_id", "").AsString() == cardId && existing.GetMeta("tier", "").AsString() == entry.Tier)
                return;
            holder.RemoveChild(existing);
            existing.QueueFree();
        }
        if (cardId != null && entry != null)
            AddBadge(holder, cardId, entry);
    }

    // The local player's character ("IRONCLAD"), or null outside a run.
    private static string? CurrentCharacterId()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        return state == null ? null : LocalContext.GetMe(state)?.Character.Id.Entry;
    }

    // Card rewards after combat, and "choose 1 of N" offers from relics, events and potions.
    private static bool IsOnCardChoiceScreen(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            if (n is NCardRewardSelectionScreen or NChooseACardSelectionScreen)
                return true;
        }
        return false;
    }

    private static void AddBadge(Control holder, string cardId, TierEntry entry)
    {
        GD.Print($"[TierListOverlay] {cardId}: {entry.Tier} — {entry.Commentary}");

        var style = new StyleBoxFlat
        {
            BgColor = new Color(entry.Color),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };
        var badge = new PanelContainer { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel", style);
        // Lets tooling (tools/DevTools) find badges and check what they show.
        badge.AddToGroup(BadgeGroup);
        badge.SetMeta("item_id", cardId);
        badge.SetMeta("tier", entry.Tier);

        var label = new Label { Text = entry.Tier, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 32);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 6);
        badge.AddChild(label);

        holder.AddChild(badge);
        // Card holders are centred on their origin; place the badge above the card's top edge.
        // No ZIndex: the badge must stay in the card's own draw order so hover tips cover it.
        badge.Position = new Vector2(-30, -250);
    }
}
