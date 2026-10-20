using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;

namespace TierListOverlay;

/// <summary>
/// Event options, for the player's own counts of what they picked. The run history keeps the
/// options picked by their title's text key ("SAPPHIRE_SEED.pages.INITIAL.options.EAT.title"),
/// not what else was offered. An option's key, without ".title", names it here too. A visit
/// offered an option if it reached the option's page: the first page always, a later one when
/// the visit picked something on it or the option of the same name that leads there, or as
/// `Requires` says. Options the event locks without a card to enchant or enough gold count
/// only the visits that offered them, from the deck and gold before the visit (`DeckGates`,
/// `GoldGates`, see History). Ancients are not events here: their options are relics.
/// </summary>
internal static class Events
{
    private const string Pages = ".pages.";
    private const string Options = ".options.";
    private const string FirstPage = "INITIAL";

    // Options (by key prefix) that a visit offered only after picking another (by key prefix,
    // "" for every visit), where page names don't tell. First match wins.
    private static readonly (string Option, string After)[] Requires =
    {
        // Giving up is on every page after the first deciphering. Each deciphering leads to the
        // next page.
        ("TABLET_OF_TRUTH.pages.DECIPHER.options.", "TABLET_OF_TRUTH.pages.INITIAL.options.DECIPHER_1"),
        ("TABLET_OF_TRUTH.pages.DECIPHER_2.options.", "TABLET_OF_TRUTH.pages.DECIPHER_1.options.DECIPHER"),
        ("TABLET_OF_TRUTH.pages.DECIPHER_3.options.", "TABLET_OF_TRUTH.pages.DECIPHER_2.options.DECIPHER"),
        ("TABLET_OF_TRUTH.pages.DECIPHER_4.options.", "TABLET_OF_TRUTH.pages.DECIPHER_3.options.DECIPHER"),
        // The dish is on the first page too, leaving only after grabbing one.
        ("ENDLESS_CONVEYOR.pages.ALL.options.", ""),
        ("ENDLESS_CONVEYOR.pages.GRAB_SOMETHING_OFF_THE_BELT.options.", "ENDLESS_CONVEYOR.pages.ALL.options."),
        // Each card type has its own riders.
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.SAPPING", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.ATTACK"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.VIOLENCE", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.ATTACK"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.CHOKING", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.ATTACK"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.ENERGIZED", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.SKILL"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.WISDOM", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.SKILL"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.CHAOS", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.SKILL"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.EXPERTISE", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.POWER"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.CURIOUS", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.POWER"),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.IMPROVEMENT", "TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.POWER"),
    };

    // Pages (by option key prefix) that a visit can show again and again, each time ending in
    // one pick among the options there, by the key prefix of the picks that show the page.
    // Their options count per showing, not per visit.
    private static readonly (string Option, string ShownBy)[] Repeats =
    {
        // Lingering shows the page again. Leaving ends the visit.
        ("ABYSSAL_BATHS.pages.ALL.options.", "ABYSSAL_BATHS.pages.ALL.options."),
        // Every dish grabbed shows the next.
        ("ENDLESS_CONVEYOR.pages.GRAB_SOMETHING_OFF_THE_BELT.options.", "ENDLESS_CONVEYOR.pages.ALL.options."),
    };

    // Options (by key) that their event locks when no card in the deck (of the type, if given)
    // can take the enchantment, as the event's GenerateInitialOptions decides. With GoldGates,
    // every option the game can lock by the deck or gold, even where the event's own conditions
    // all but rule that out. Not Luminous Choir's tribute, whose cost is random.
    private static readonly (string Option, System.Func<EnchantmentModel> Enchantment, CardType? Type)[] DeckGates =
    {
        ("SELF_HELP_BOOK.pages.INITIAL.options.READ_THE_BACK", ModelDb.Enchantment<Sharp>, CardType.Attack),
        ("SELF_HELP_BOOK.pages.INITIAL.options.READ_PASSAGE", ModelDb.Enchantment<Nimble>, CardType.Skill),
        ("SELF_HELP_BOOK.pages.INITIAL.options.READ_ENTIRE_BOOK", ModelDb.Enchantment<Swift>, CardType.Power),
        ("SYMBIOTE.pages.INITIAL.options.APPROACH", ModelDb.Enchantment<Corrupted>, null),
        ("STONE_OF_ALL_TIME.pages.INITIAL.options.PUSH", ModelDb.Enchantment<Vigorous>, null),
        ("WOOD_CARVINGS.pages.INITIAL.options.SNAKE", ModelDb.Enchantment<Slither>, null),
        ("GRAVE_OF_THE_FORGOTTEN.pages.INITIAL.options.CONFRONT", ModelDb.Enchantment<SoulsPower>, null),
    };

    // Options (by key) that their event locks below a cost, by the event and the cost's
    // dynamic variable.
    private static readonly (string Option, System.Func<EventModel> Event, string Cost)[] GoldGates =
    {
        ("WATERLOGGED_SCRIPTORIUM.pages.INITIAL.options.TENTACLE_QUILL", ModelDb.Event<WaterloggedScriptorium>, "Gold"),
        ("WATERLOGGED_SCRIPTORIUM.pages.INITIAL.options.PRICKLY_SPONGE", ModelDb.Event<WaterloggedScriptorium>, "PricklySpongeGold"),
        ("WELCOME_TO_WONGOS.pages.INITIAL.options.BARGAIN_BIN", ModelDb.Event<WelcomeToWongos>, "BargainBinCost"),
        ("WELCOME_TO_WONGOS.pages.INITIAL.options.FEATURED_ITEM", ModelDb.Event<WelcomeToWongos>, "FeaturedItemCost"),
        ("WELCOME_TO_WONGOS.pages.INITIAL.options.MYSTERY_BOX", ModelDb.Event<WelcomeToWongos>, "MysteryBoxCost"),
        ("ZEN_WEAVER.pages.INITIAL.options.EMOTIONAL_AWARENESS", ModelDb.Event<ZenWeaver>, "EmotionalAwarenessCost"),
        ("ZEN_WEAVER.pages.INITIAL.options.ARACHNID_ACUPUNCTURE", ModelDb.Event<ZenWeaver>, "ArachnidAcupunctureCost"),
        ("TEA_MASTER.pages.INITIAL.options.BONE_TEA", ModelDb.Event<TeaMaster>, "BoneTeaCost"),
        ("TEA_MASTER.pages.INITIAL.options.EMBER_TEA", ModelDb.Event<TeaMaster>, "EmberTeaCost"),
    };

    // Options (by key prefix) that a visit to their page does not always offer, at random, for
    // the tip. First match wins.
    private static readonly (string Option, string Note)[] Notes =
    {
        ("ENDLESS_CONVEYOR.pages.ALL.options.", "A random dish each time."),
        ("TINKER_TIME.pages.CHOOSE_CARD_TYPE.options.", "Two of the three types, at random."),
        ("TINKER_TIME.pages.CHOOSE_RIDER.options.", "Two of the three, at random."),
        ("COLORFUL_PHILOSOPHERS.pages.INITIAL.options.", "Other characters' colours, three at most."),
        ("THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION", "One option per potion."),
    };

    /// <summary>The event an option belongs to ("SAPPHIRE_SEED").</summary>
    public static string EventOf(string key) => key[..key.IndexOf(Pages)];

    /// <summary>The key of a picked option as the history names it, or null for other entries (an ancient's relic).</summary>
    public static string? FromHistory(string? table, string? title) =>
        table == "events" && title != null && title.EndsWith(".title") && IsOptionKey(title[..^".title".Length]) ? title[..^".title".Length] : null;

    private static bool IsOptionKey(string key) =>
        key.IndexOf(Pages) is var pages and > 0 && key.IndexOf(Options, pages) > pages;

    private static string Page(string key)
    {
        var start = key.IndexOf(Pages) + Pages.Length;
        return key[start..key.IndexOf(Options, start)];
    }

    private static string Name(string key) => key[(key.IndexOf(Options) + Options.Length)..];

    // An option on screen that the history would keep when picked: its key, or null.
    private static string? Key(EventOption? option) =>
        option is { IsLocked: false, ShouldSaveChoiceToHistory: true, Relic: null } && option.HistoryName is { } name
            ? FromHistory(name.LocTable, name.LocEntryKey)
            : null;

    /// <summary>
    /// The option's key when it gets the player's counts: one the history keeps, among two or
    /// more such options on screen. A page with a single option leaves nothing to choose.
    /// </summary>
    public static string? Key(NEventOptionButton button)
    {
        if (Key(button.Option) is not { } key || button.GetParent() is not { } options)
            return null;
        var choices = options.GetChildren().OfType<NEventOptionButton>().Count(b => !b.IsQueuedForDeletion() && Key(b.Option) != null);
        return choices >= 2 ? key : null;
    }

    /// <summary>
    /// For an option on a page that repeats within a visit: the key prefix of the picks that
    /// show the page, once each. Null for the others.
    /// </summary>
    public static string? ShownBy(string key) => Repeats.FirstOrDefault(r => key.StartsWith(r.Option)).ShownBy;

    /// <summary>Whether a visit (the keys it picked) offered the option.</summary>
    public static bool Reached(IReadOnlyCollection<string> visit, string key)
    {
        foreach (var (option, after) in Requires)
        {
            if (key.StartsWith(option))
                return after == "" || visit.Any(c => c.StartsWith(after));
        }
        var page = Page(key);
        return page == FirstPage || visit.Any(c => Page(c) == page || Name(c) == page);
    }

    /// <summary>Why a visit to the option's page may not have offered it, or null.</summary>
    public static string? Note(string key) => Notes.FirstOrDefault(n => key.StartsWith(n.Option)).Note;

    /// <summary>Whether the event ("SELF_HELP_BOOK") locks an option by the deck, and by gold.</summary>
    public static (bool Deck, bool Gold) Gated(string eventId) =>
        (DeckGates.Any(g => EventOf(g.Option) == eventId), GoldGates.Any(g => EventOf(g.Option) == eventId));

    /// <summary>
    /// Whether a visit with this deck (card and enchantment IDs, "CARD.BASH", "ENCHANTMENT.SHARP")
    /// and gold before it offered the option, or could have: true for options without a gate
    /// and for a deck or gold not known. A card the game doesn't know can't take an enchantment.
    /// Uses the game's models, so only on the main thread.
    /// </summary>
    public static bool Available(string key, IReadOnlyList<(string Card, string? Enchantment)>? deck, int? gold)
    {
        foreach (var (option, enchantment, type) in DeckGates)
        {
            if (option == key && deck != null)
                return deck.Any(c => CanEnchant(c.Card, c.Enchantment, enchantment(), type));
        }
        foreach (var (option, eventModel, cost) in GoldGates)
        {
            if (option == key && gold != null)
                return gold >= eventModel().DynamicVars[cost].IntValue;
        }
        return true;
    }

    // EnchantmentModel.CanEnchant on the game's own card, with the enchantment the card had then.
    // It skips the unplayable check for a card in no pile, so that comes first here.
    private static bool CanEnchant(string cardId, string? had, EnchantmentModel enchantment, CardType? type)
    {
        if (cardId.Split('.') is not [var category, var entry] || ModelDb.GetByIdOrNull<CardModel>(new ModelId(category, entry)) is not { } card
            || type != null && card.Type != type || card.Keywords.Contains(CardKeyword.Unplayable))
            return false;
        if (had != null && (!enchantment.IsStackable || had != enchantment.Id.ToString()))
            return false;
        return enchantment.CanEnchant(card);
    }
}
