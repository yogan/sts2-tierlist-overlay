using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
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
using MegaCrit.Sts2.Core.Rooms;

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
        Account.Load(Path.Combine(modDir, "account.cfg"));
        History.Load(modDir);
        // Lists are made per game version, so ask for the installed one ("v0.107.1").
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
        if (node is NCard or NRelic or NPotion or NRewardButton or NEventOptionButton or NCardRewardAlternativeButton or NMerchantCardRemoval)
            Tracked.Add((Control)node);
        Tips.OnNodeAdded(node);
        SettingsUi.OnNodeAdded(node);
        Hint.OnNodeAdded(node);
    }

    private static void OnProcessFrame()
    {
        SettingsUi.OnProcessFrame();
        Hint.OnProcessFrame();
        Account.OnProcessFrame();
        History.OnProcessFrame();
        // The game's model database may not be ready in the first frames. If so, try again.
        if (TierLists.Generation != _checkedGeneration && LogUnknownIds())
            _checkedGeneration = TierLists.Generation;
        // A pooled node leaves the tree and is added again (and tracked again) on reuse.
        Tracked.RemoveWhere(c => !GodotObject.IsInstanceValid(c) || !c.IsInsideTree());
        var characterId = CurrentCharacterId();
        Tips.OnProcessFrame(characterId);
        if (Tracked.Count == 0)
            return;
        foreach (var node in Tracked)
        {
            try
            {
                var (itemId, mode, placement, bounds) = Describe(node);
                var (keep, content) = Content(node, characterId, itemId, mode);
                if (keep)
                    continue;
                Badges.Sync(node, content, bounds, placement);
                if (node is NRelic or NPotion or NMerchantCardRemoval)
                    MakeRoomForBadge(node);
                if (node is NCardRewardAlternativeButton button)
                    Tips.ShowOnFocus(button);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Updating badge failed: {ex}");
            }
        }
    }

    // The badge for a node: the stats the player chose where there are some, otherwise its
    // tier. The stats it has (whichever the badge shows) go along for the hover tip.
    // Keep: global stats are on their way. Leave the item's badge as it is until they arrive,
    // so it does not flash from tier to stats. Not a badge left from another item on a reused
    // node.
    private static (bool Keep, BadgeContent? Content) Content(Control node, string? characterId, string? itemId, BadgeMode mode)
    {
        if (itemId == null || mode == BadgeMode.None)
            return (false, null);
        var isCard = node is NCard;
        var tier = itemId is SkipId or RemoveId ? null : Lookup(characterId, itemId, isCard);
        var tierBadge = tier is var (id, tierEntry) ? Badges.ForTier(id, tierEntry, isCard && HasAncestor<NDeckUpgradeSelectScreen>(node)) : null;
        if (characterId == null || StatsSlot(node) is not var (screen, kind, group))
            return (false, tierBadge);
        var statsId = itemId is SkipId or RemoveId ? Stats.SingleId : itemId;
        var tips = Settings.StatsTips;
        // The run history has no upgrade stats: the game does not log what the smith offered.
        var mine = (mode == BadgeMode.Personal || tips) && kind != StatsKind.Upgrade ? History.Get(characterId, kind, group, statsId) : null;
        if (mine is { Shown: false })
            mine = null;
        StatEntry? global = null;
        var (act, answered) = (0, false);
        if ((mode == BadgeMode.Global || tips) && Account.Usable && RequestFor(screen, characterId) is { } request)
        {
            act = request.Act;
            switch (Stats.Get(request, out var answer))
            {
                case Stats.Lookup.Pending when mode == BadgeMode.Global:
                    var shown = node.GetNodeOrNull<Control>(Badges.BadgeName)?.GetMeta("item_id", "").AsString();
                    return (shown == null || shown == itemId, null);
                case Stats.Lookup.Ready:
                    answered = true;
                    global = answer!.Get(group, statsId) is { HasData: true } entry ? entry : null;
                    // All characters' runs are the character's own for items only it can get.
                    if (global is { AllCharacters: true } && OwnItem(group, itemId))
                        global = global with { AllCharacters = false };
                    break;
            }
        }
        var info = mine != null || global != null ? new StatsInfo(kind, global, act, mine) : null;
        var badge = mode switch
        {
            BadgeMode.Global when global != null => Badges.ForStats(itemId, info!, tier?.Entry.Tier ?? ""),
            BadgeMode.Personal when mine != null => mine.Unpicked
                ? Badges.ForUnpicked(itemId, info!, tier?.Entry.Tier ?? "")
                : Badges.ForPersonal(itemId, info!, tier?.Entry.Tier ?? ""),
            // Global stats, but too little data for this item: no badge rather than a tier beside
            // the others' stats. A hidden tier badge still brings up the item's tips. Without any
            // stats (no answer yet, offline, history not read) the whole screen shows tiers.
            BadgeMode.Global when answered => tierBadge == null ? null : tierBadge with { Hidden = true },
            _ => tierBadge,
        };
        if (info == null)
            return (false, badge);
        return (false, badge != null ? badge with { Stats = info } : tips ? Badges.ForTipOnly(itemId, info) : null);
    }

    // Item IDs for the two offers that are no items.
    internal const string SkipId = "SKIP";
    internal const string RemoveId = "CARD_REMOVAL";

    // The screen whose stats request covers the node, which stats it gets, and its group in
    // the answer. Null on screens without stats (deck view, remove, transform, …).
    private static (Node Screen, StatsKind Kind, string Group)? StatsSlot(Control node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            switch (n)
            {
                case NInspectCardScreen:
                    return null;
                case NCardRewardSelectionScreen when node is NCard:
                    return (n, StatsKind.Reward, "cards");
                case NCardRewardSelectionScreen when node is NCardRewardAlternativeButton:
                    return (n, StatsKind.Skip, "skip");
                case NMerchantInventory:
                    return node switch
                    {
                        NCard => (n, StatsKind.Shop, "cards"),
                        NRelic => (n, StatsKind.Shop, "relics"),
                        NPotion => (n, StatsKind.Shop, "potions"),
                        NMerchantCardRemoval => (n, StatsKind.Shop, "remove"),
                        _ => null,
                    };
                case NDeckUpgradeSelectScreen when node is NCard:
                    return (n, StatsKind.Upgrade, "cards");
            }
        }
        // Ancients offer relics as event options. The options' container is the screen.
        if (node is NEventOptionButton && node.GetParent() is { } options)
            return (options, StatsKind.Ancient, "relics");
        return null;
    }

    // Each screen's stats request, made at most every half second: building one reads the
    // whole offer (on the upgrade screen, the deck). What a screen offers changes rarely.
    private static readonly Dictionary<Node, (ulong At, StatsRequest? Request)> Requests = new();
    private const ulong RequestRefreshMs = 500;

    private static StatsRequest? RequestFor(Node screen, string characterId)
    {
        var now = Time.GetTicksMsec();
        if (Requests.TryGetValue(screen, out var made) && now - made.At < RequestRefreshMs)
            return made.Request;
        foreach (var gone in Requests.Keys.Where(n => !GodotObject.IsInstanceValid(n)).ToList())
            Requests.Remove(gone);
        var request = BuildRequest(screen, characterId);
        Requests[screen] = (now, request);
        return request;
    }

    private static readonly FieldInfo? RewardOptionsField = typeof(NCardRewardSelectionScreen).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? RewardAlternativesField = typeof(NCardRewardSelectionScreen).GetField("_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? GridCardsField = typeof(NCardGridSelectionScreen).GetField("_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    private static Stats.Card StatsCard(CardModel card, int price = 0) =>
        new(card.Id.Entry, card.CurrentUpgradeLevel, card.Enchantment?.Id.Entry, price);

    // What the screen offers, as a stats request, or null when there is nothing to ask about.
    private static StatsRequest? BuildRequest(Node screen, string characterId)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null)
            return null;
        var act = state.CurrentActIndex + 1;
        var party = state.Players.Count;
        switch (screen)
        {
            case NCardRewardSelectionScreen reward when RewardOptionsField?.GetValue(reward) is IReadOnlyList<CardCreationResult> options:
                var room = state.CurrentRoom?.RoomType ?? RoomType.Unassigned;
                // A boss's cards are for the next act.
                return Stats.CardReward(options.Select(o => StatsCard(o.Card)), room == RoomType.Boss ? act + 1 : act, characterId, party, (int)room);
            case NMerchantInventory { Inventory: { } inventory }:
                return Stats.Shop(
                    inventory.CardEntries.Where(e => e.IsStocked).Select(e => StatsCard(e.CreationResult!.Card, e.Cost)),
                    inventory.RelicEntries.Where(e => e.IsStocked).Select(e => new Stats.Priced(e.Model!.Id.Entry, e.Cost)),
                    inventory.PotionEntries.Where(e => e.IsStocked).Select(e => new Stats.Priced(e.Model!.Id.Entry, e.Cost)),
                    inventory.CardRemovalEntry?.Cost ?? 0, act, characterId, party);
            case NDeckUpgradeSelectScreen upgrade when GridCardsField?.GetValue(upgrade) is IReadOnlyList<CardModel> cards:
                return Stats.Upgrade(cards.Select(c => StatsCard(c)), act, characterId, party);
            // Other events offer relics as options too (mushrooms), but only ancients have stats.
            default:
                if (state.CurrentRoom is not EventRoom { CanonicalEvent: AncientEventModel })
                    return null;
                var relics = screen.GetChildren().OfType<NEventOptionButton>().Select(o => o.Option?.Relic?.Id.Entry).OfType<string>().ToList();
                return relics.Count > 0 ? Stats.Ancient(relics, act, characterId, party) : null;
        }
    }

    // The card reward's alternatives (Skip, and Reroll with some relics) are buttons in the
    // order of the screen's alternatives.
    private static bool IsSkip(NCardRewardAlternativeButton button)
    {
        var screen = FindAncestor<NCardRewardSelectionScreen>(button);
        if (screen == null || RewardAlternativesField?.GetValue(screen) is not IReadOnlyList<CardRewardAlternative> alternatives)
            return false;
        var index = button.GetParent().GetChildren().OfType<NCardRewardAlternativeButton>().Where(b => !b.IsQueuedForDeletion()).ToList().IndexOf(button);
        return index >= 0 && index < alternatives.Count && alternatives[index].OptionId == "Skip";
    }

    // NCard is centred on its origin and has no size of its own. NCard.defaultSize is the frame.
    private static readonly Rect2 CardBounds = new(-NCard.defaultSize / 2, NCard.defaultSize);

    // The item a node shows, what its badge should show (the settings per kind of badge), where
    // the badge goes, and the node's visible rectangle in its own coordinates.
    private static (string? ItemId, BadgeMode Mode, Placement Placement, Rect2 Bounds) Describe(Control node)
    {
        var own = new Rect2(Vector2.Zero, node.Size);
        if (node is NCard card)
        {
            var mode = CardScreen(card) switch
            {
                CardScreenKind.Choice => Settings.Cards,
                // No stats there.
                CardScreenKind.Deck when Settings.Deck => BadgeMode.Tiers,
                _ => BadgeMode.None,
            };
            return (card.Model?.Id.Entry, mode, Placement.InsideBottom, CardBounds);
        }
        // Skip and card removal have stats but no tier, and go with the cards.
        if (node is NCardRewardAlternativeButton skip)
            return (IsSkip(skip) ? SkipId : null, Settings.Cards, Placement.AboveTop, own);
        // The slot's own rectangle is not where its coin is drawn, the hitbox is.
        if (node is NMerchantCardRemoval removal)
            return (RemoveId, Settings.Cards, Placement.AcrossBottom,
                removal.Hitbox is { } hitbox ? new Rect2(hitbox.Position, hitbox.Size) : own);
        var (relics, potions) = (Settings.Relics, Settings.Potions);
        return node switch
        {
            NRelic relic when HasAncestor<NMerchantInventory>(relic) || HasAncestor<NTreasureRoomRelicCollection>(relic) || HasAncestor<NChooseARelicSelection>(relic)
                => (relic.Model.Id.Entry, relics, Placement.AcrossBottom, own),
            NPotion potion when HasAncestor<NMerchantInventory>(potion) => (potion.Model.Id.Entry, potions, Placement.AcrossBottom, own),
            // Elite and event loot: the relic or potion is only an icon on the button.
            NRewardButton { Reward: RelicReward r } => (r.Relic?.Id.Entry, relics, Placement.RightEdge, own),
            NRewardButton { Reward: PotionReward p } => (p.Potion?.Id.Entry, potions, Placement.RightEdge, own),
            // Ancients (Neow, Darv, …) offer relics as event options.
            NEventOptionButton option => (option.Option?.Relic?.Id.Entry, relics, Placement.TopCentre, own),
            _ => (null, BadgeMode.None, Placement.InsideBottom, own),
        };
    }

    // Lists can name items the game does not have (renamed, removed, or from a newer game
    // version). They never get a badge, so say which ones once per load.
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

    // In the shop a relic's or potion's price sits right below the icon, where the badge goes,
    // so move it down while there is a badge.
    private const string CostOffsetMeta = "tierlist_cost_offset";

    private static void MakeRoomForBadge(Control icon)
    {
        var slot = icon as NMerchantSlot ?? icon.GetParent()?.GetParent() as NMerchantSlot;
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

    internal static (string Id, TierEntry Entry)? Lookup(string? characterId, string? itemId, bool isCard)
    {
        if (itemId == null)
            return null;
        if (TierLists.TryGet(characterId, itemId, out var entry, isCard))
            return (itemId, entry);
        if (LoggedUnrated.Add(itemId))
            GD.Print($"[TierListOverlay] No tier for {itemId}");
        return null;
    }

    // The IDs in the current character's own card, relic and potion pools, which no other
    // character gets, by group ("cards", "relics", "potions").
    private static (string Character, Dictionary<string, HashSet<string>> Ids)? _ownItems;

    private static bool OwnItem(string group, string itemId)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null || LocalContext.GetMe(state)?.Character is not { } character)
            return false;
        if (_ownItems?.Character != character.Id.Entry)
            _ownItems = (character.Id.Entry, new Dictionary<string, HashSet<string>>
            {
                ["cards"] = character.CardPool.AllCardIds.Select(id => id.Entry).ToHashSet(),
                ["relics"] = character.RelicPool.AllRelicIds.Select(id => id.Entry).ToHashSet(),
                ["potions"] = character.PotionPool.AllPotionIds.Select(id => id.Entry).ToHashSet(),
            });
        return _ownItems.Value.Ids.TryGetValue(group, out var ids) && ids.Contains(itemId);
    }

    // The local player's character ("IRONCLAD"), or null outside a run.
    internal static string? CurrentCharacterId()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        return state == null ? null : LocalContext.GetMe(state)?.Character.Id.Entry;
    }

    private static bool HasAncestor<T>(Node node) where T : Node => FindAncestor<T>(node) != null;

    private static T? FindAncestor<T>(Node node) where T : Node
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
        {
            if (n is T t)
                return t;
        }
        return null;
    }

    private enum CardScreenKind
    {
        None,
        // Picking, buying or upgrading cards: the screens with stats, and similar ones.
        Choice,
        // Working on the deck otherwise.
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
                case NDeckUpgradeSelectScreen:
                    return CardScreenKind.Choice;
                case NDeckTransformSelectScreen or NDeckEnchantSelectScreen or NDeckCardSelectScreen:
                case NDeckViewScreen:
                    return CardScreenKind.Deck;
            }
        }
        return CardScreenKind.None;
    }
}
