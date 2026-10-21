using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace TierListOverlay;

/// <summary>
/// Adds a "Tier List" tab with the mod's settings to the game's settings screen, after Input.
/// Its rows are copies of the game's own rows (the timer's tickbox, the screenshake's left/right
/// choice, the feedback row's button) with their scripts swapped for the game's plain ones, so
/// they look like the game's rows but no longer change the game's settings. As in the game,
/// related rows form a group between dividers, each row explains itself in a hover tip, and
/// rows that don't apply are greyed out.
/// </summary>
internal static class SettingsUi
{
    private const string TabName = "TierListOverlay";
    private const string TabLabel = "Tier List";
    private const string PanelName = "TierListOverlaySettings";
    private const string RowPrefix = "TierListOverlay_";
    private const string AccountKey = "account";

    private const string TickboxScript = "res://src/Core/Nodes/CommonUi/NTickbox.cs";
    private const string PaginatorScript = "res://src/Core/Nodes/Screens/Settings/NPaginator.cs";
    // The plain button: the game's NSettingsButton cannot run without a subclass of its own.
    private const string ButtonScript = "res://src/Core/Nodes/GodotExtensions/NButton.cs";

    // A row: settings.cfg key, label, hover tip (title and text, made when shown, as it can
    // depend on the login), and whether it applies with the other settings and the login as
    // they are. `Group`: the first row of a group, which gets a divider above it.
    private abstract record Row(string Key, string Label, string TipTitle, Func<string> Tip, Func<bool> Applies, bool Group = false)
    {
        public virtual string TipText => Tip();
    }

    private sealed record Toggle(string Key, string Label, string TipTitle, Func<string> Tip, Func<bool> Applies, bool Group = false)
        : Row(Key, Label, TipTitle, Tip, Applies, Group);

    // A left/right choice. An option can be unavailable for now (global stats without a login).
    // A chosen one that is unavailable shows as `Fallback` but stays chosen. `Tip`: the option's
    // line in the row's tip, while it is offered.
    private sealed record Option(string Value, string Label, Func<bool>? Available = null, string? Tip = null)
    {
        public bool Offered => Available?.Invoke() ?? true;
    }

    // `LabelNow`: the label as things are, where it names what the row affects only sometimes.
    private sealed record Choice(string Key, string Label, string TipTitle, Func<string> Tip, Func<bool> Applies, Option[] Options, string Fallback = "", bool Group = false,
        Func<string>? LabelNow = null)
        : Row(Key, Label, TipTitle, Tip, Applies, Group)
    {
        public override string TipText =>
            string.Join("\n", Options.Where(o => o.Offered && o.Tip != null).Select(o => $"[gold]{o.Label}[/gold]: {o.Tip}").Prepend(Tip()));
    }

    // The login, with a button to log in or out.
    private sealed record AccountRow(string Key, string Label, string TipTitle, Func<string> Tip, bool Group)
        : Row(Key, Label, TipTitle, Tip, () => true, Group);

    private static readonly Option None = new(Settings.Value(BadgeMode.None), "None");
    private static readonly Option Tiers = new(Settings.Value(BadgeMode.Tiers), "Tier list", Tip: "the list's rating.");
    private static readonly Option Personal = new(Settings.Value(BadgeMode.Personal), "Personal stats",
        Tip: "how often you took it in your own Ascension 7+ runs with this character.");
    private static readonly Option Global = new(Settings.Value(BadgeMode.Global), "Global stats", () => Account.Usable,
        "how often other players took it at Ascension 7+ with this character.");

    private static readonly Option[] BadgeOptions = { None, Tiers, Personal, Global };

    // The history keeps what you picked at an event, not what it offered. Locked options are
    // judged from the deck and gold before the visit (Events).
    private static readonly Option EventCounts = new(Settings.Value(BadgeMode.Personal), "Personal stats",
        Tip: "in how many of your Ascension 7+ visits, with any character, you picked it. "
             + "Visits where it was locked don't count.");

    private static readonly Row[] Rows =
    {
        new Choice(Settings.CardsKey, "Badges on cards", "Badges on cards", () => "On cards you can pick or buy.",
            () => true, BadgeOptions, Tiers.Value),
        new Choice(Settings.RelicsKey, "Badges on relics", "Badges on relics", () => "On relics you can pick or buy.",
            () => true, BadgeOptions, Tiers.Value),
        new Choice(Settings.PotionsKey, "Badges on potions", "Badges on potions", () => "On potions you can pick or buy.",
            () => true, BadgeOptions, Tiers.Value),
        new Choice(Settings.EventsKey, "Badges on events", "Badges on events",
            () => "On event options. Ancients' relics go with the relic badges.",
            () => true, new[] { None, EventCounts }, EventCounts.Value),
        new Choice(Settings.DeckKey, "Badges on deck screens", "Deck screens",
            () => "The tier list's rating on the cards you have: deck view, remove, transform, enchant.",
            () => true, new[] { None, Tiers }),

        new Choice(Settings.AuthorKey, "Tier list", "Tier list",
            () => $"Whose ratings the badges and tips show{(Settings.EventAdvice ? ", and whose event advice comes first" : "")}. "
                  + "Jorbs rates cards only, so relics and potions keep Baalorlord's.",
            () => Settings.AnyTiers || Settings.EventAdvice, new[] { new Option(Settings.Baalorlord, Settings.Baalorlord), new Option(Settings.Jorbs, Settings.Jorbs) },
            Group: true, LabelNow: () => Settings.EventAdvice ? "Tier list and event advice" : "Tier list"),
        new Toggle(Settings.CommentaryKey, "Tier list commentary in hover tips", "Commentary",
            () => "An extra hover tip on rated items with the author's tier and commentary.", () => true),
        new Toggle(Settings.StatsTipsKey, "Stats in hover tips", "Stats tips",
            () => $"An extra hover tip with your own stats{(Account.Usable ? " and other players'" : "")}, whatever the badges show.",
            () => true),
        new Toggle(Settings.EventAdviceKey, "Event advice in hover tips", "Event advice",
            () => Account.Usable
                ? "An extra hover tip on event options with the tier list author's advice on the event, or the other author's where there is none."
                : "Log in to Untapped.gg for Baalorlord's and Jorbs' advice on events.",
            () => Account.Usable),

        new Toggle(Settings.StatsSamplesKey, "Sample sizes in stats tips", "Sample sizes",
            () => "Below the stats, how many runs and offers they are based on.", () => Settings.StatsTips, Group: true),

        new AccountRow(AccountKey, "Untapped.gg", "Untapped.gg",
            () => (Account.Usable
                ? "Badges can show other players' pick rates, and tips their win rates and event advice."
                : "Log in for other players' pick and win rates, and for event advice.")
                + " The mod only reads these stats and sends none of your runs.", Group: true),
    };

    private static readonly FieldInfo? TabsField = typeof(NSettingsTabManager).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? SwitchTabMethod = typeof(NSettingsTabManager).GetMethod("SwitchTabTo", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? PaginatorOptionsField = typeof(NPaginator).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? PaginatorIndexField = typeof(NPaginator).GetField("_currentIndex", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? PaginatorLabelField = typeof(NPaginator).GetField("_label", BindingFlags.NonPublic | BindingFlags.Instance);

    // The button colours of the game's settings rows (hue, saturation, value of their shader):
    // green to go ahead (Send Feedback), red to undo (Reset Gameplay).
    private static (Variant H, Variant S, Variant V) _goColour, _undoColour;

    private static readonly HashSet<NSettingsScreen> Pending = new();

    // The rows on screen, to keep them in step with the settings and the login.
    private sealed class Shown
    {
        public required Row Row;
        public required Control Node;
        public required MegaRichTextLabel Label;
        public NTickbox? Tickbox;
        public NPaginator? Paginator;
        public NButton? Button;
        public bool? Enabled;
        // A choice's options as offered now, and the one shown.
        public Option[] Offered = Array.Empty<Option>();
        public int Index = -1;
        public string Text = "";
        public string ButtonText = "";
    }

    private static readonly List<Shown> ShownRows = new();

    public static void OnNodeAdded(Node node)
    {
        if (node is NSettingsScreen screen)
            Pending.Add(screen);
    }

    // The screen's panels are filled in its _Ready, so the tab is added on the next frame.
    public static void OnProcessFrame()
    {
        ShownRows.RemoveAll(r => !GodotObject.IsInstanceValid(r.Node));
        // The game keeps a hidden settings screen around from the main menu on.
        foreach (var row in ShownRows)
        {
            if (row.Node.IsVisibleInTree())
                Update(row);
        }
        if (Pending.Count == 0)
            return;
        var screens = new List<NSettingsScreen>(Pending);
        Pending.Clear();
        foreach (var screen in screens)
        {
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                continue;
            try
            {
                AddTab(screen);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TierListOverlay] Adding settings failed: {ex}");
            }
        }
    }

    private static void AddTab(NSettingsScreen screen)
    {
        var manager = FindDescendant<NSettingsTabManager>(screen);
        var general = screen.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
        var tickboxRow = general?.Content.GetNodeOrNull<Control>("ShowRunTimer");
        var choiceRow = general?.Content.GetNodeOrNull<Control>("Screenshake");
        var buttonRow = general?.Content.GetNodeOrNull<Control>("SendFeedback");
        var go = buttonRow?.GetNodeOrNull<CanvasItem>("FeedbackButton/Image")?.Material as ShaderMaterial;
        var undo = (general == null ? null : FindDescendant<NResetGameplayButton>(general))?.GetNodeOrNull<CanvasItem>("Image")?.Material as ShaderMaterial;
        var divider = general?.Content.GetNodeOrNull<Control>("ShowTimerDivider");
        var lastTab = manager?.GetNodeOrNull<NSettingsTab>("Input");
        if (manager == null || general == null || tickboxRow == null || choiceRow == null || buttonRow?.GetNodeOrNull("FeedbackButton") == null
            || go == null || undo == null || divider == null || lastTab == null
            || TabsField?.GetValue(manager) is not Dictionary<NSettingsTab, NSettingsPanel> tabs || SwitchTabMethod == null
            || PaginatorOptionsField == null || PaginatorIndexField == null || PaginatorLabelField == null)
        {
            GD.PrintErr("[TierListOverlay] Settings screen layout changed, not adding the mod's settings");
            return;
        }
        if (manager.GetNodeOrNull(TabName) != null)
            return;
        _goColour = Hsv(go);
        _undoColour = Hsv(undo);

        // A panel like General's, holding only the mod's rows. The panel sizes its scroll area
        // to its rows when it enters the tree.
        var panel = (NSettingsPanel)general.Duplicate();
        panel.Name = PanelName;
        panel.UniqueNameInOwner = false;
        var content = panel.GetNode<VBoxContainer>("VBoxContainer");
        // General's rows, and its language dropdown, which sits beside the rows.
        foreach (var child in content.GetChildren().Concat(panel.GetChildren().Where(c => c != content)))
        {
            child.GetParent().RemoveChild(child);
            child.Free();
        }
        var rows = new List<(Row Row, Control Node)>();
        foreach (var row in Rows)
        {
            if (row.Group)
            {
                var line = (Control)divider.Duplicate();
                line.Name = RowPrefix + row.Key + "_Divider";
                content.AddChild(line);
            }
            var node = row switch
            {
                Choice => CopyRow(choiceRow, "Paginator", PaginatorScript),
                AccountRow => CopyRow(buttonRow, "FeedbackButton", ButtonScript),
                _ => CopyRow(tickboxRow, "SettingsTickbox", TickboxScript),
            };
            node.Name = RowPrefix + row.Key;
            content.AddChild(node);
            rows.Add((row, node));
        }
        panel.Visible = false;
        general.GetParent().AddChild(panel);

        var (barPosition, barSize) = (manager.Position, manager.Size);
        var tab = (NSettingsTab)lastTab.Duplicate();
        OwnMaterials(tab);
        tab.Name = TabName;
        manager.AddChild(tab);
        manager.MoveChild(tab, lastTab.GetIndex() + 1);
        tab.SetLabel(TabLabel);
        tab.Deselect();
        LayOutTabs(tabs.Keys.Append(tab).ToList());
        // The tab bar grew for the fifth tab. Back to its own width, which the narrower tabs share.
        manager.Size = barSize;
        manager.Position = barPosition;
        tabs.Add(tab, panel);
        tab.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => SwitchTabMethod.Invoke(manager, new object[] { tab })));

        foreach (var (row, node) in rows)
            ShownRows.Add(Set(row, node));
        GD.Print("[TierListOverlay] Settings tab added to the settings screen");
    }

    private static (Variant, Variant, Variant) Hsv(ShaderMaterial material) =>
        (material.GetShaderParameter("h"), material.GetShaderParameter("s"), material.GetShaderParameter("v"));

    // A copy of a game row with plain scripts: a script swap disposes the node's C# object, so
    // nodes are found again by ID.
    private static Control CopyRow(Control template, string controlName, string controlScript)
    {
        var copy = template.Duplicate();
        OwnMaterials(copy);
        var rowId = copy.GetInstanceId();
        var controlId = copy.GetNode(controlName).GetInstanceId();
        // Plain Control instead of the row's script, which shows the game setting's tip.
        copy.SetScript(default(Variant));
        // The plain control instead of the game setting's, which would change that setting.
        GodotObject.InstanceFromId(controlId)!.SetScript(ResourceLoader.Load(controlScript));
        return (Control)GodotObject.InstanceFromId(rowId)!;
    }

    // A copy shares the original's materials, through which the game's controls light up when
    // hovered, so both would light up together.
    private static void OwnMaterials(Node copy)
    {
        foreach (var item in SelfAndDescendants(copy).OfType<CanvasItem>().Where(c => c.Material != null))
            item.Material = (Material)item.Material.Duplicate();
    }

    // Five tabs in the width of the game's four, which the tab bar (a box container) lays out
    // side by side at their minimum width.
    private static void LayOutTabs(List<NSettingsTab> tabs)
    {
        var gap = tabs[0].GetParent<Control>().GetThemeConstant("separation");
        var gameTabs = tabs.Count - 1;
        var span = tabs[0].Size.X * gameTabs + gap * (gameTabs - 1);
        var width = Mathf.Floor((span - gap * (tabs.Count - 1)) / tabs.Count);
        foreach (var tab in tabs)
        {
            // A tab's images are as wide as their textures unless told otherwise.
            foreach (var image in tab.GetChildren().OfType<TextureRect>())
                image.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            tab.CustomMinimumSize = new Vector2(width, tab.CustomMinimumSize.Y);
        }
    }

    // Fills a copied row (now in the tree) for its setting and wires it up.
    private static Shown Set(Row row, Control node)
    {
        var shown = new Shown { Row = row, Node = node, Label = node.GetNode<MegaRichTextLabel>("Label") };
        // Some game rows (the feedback row) leave the mouse to their label, but the tip needs the
        // row to see it.
        node.MouseFilter = Control.MouseFilterEnum.Pass;
        shown.Label.MouseFilter = Control.MouseFilterEnum.Pass;
        ShowTipOnHover(node, node, row);
        switch (row)
        {
            case Choice:
                shown.Paginator = node.GetNode<NPaginator>("Paginator");
                break;
            case AccountRow:
                shown.Button = node.GetNode<NButton>("FeedbackButton");
                SetUpButton(shown.Button, node, row);
                break;
            default:
                shown.Tickbox = node.GetNode<NTickbox>("SettingsTickbox");
                shown.Tickbox.IsTicked = Settings.Get(row.Key).AsBool();
                shown.Tickbox.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(t => Settings.Set(row.Key, t.IsTicked)));
                break;
        }
        Update(shown);
        return shown;
    }

    private static void ShowTipOnHover(Control hovered, Control row, Row setting)
    {
        hovered.Connect(Control.SignalName.MouseEntered, Callable.From(() => Tips.ShowForSetting(row, setting.TipTitle, setting.TipText)));
        hovered.Connect(Control.SignalName.MouseExited, Callable.From(() => NHoverTipSet.Remove(row)));
    }

    // The account button, with the plain button's script: it grows a little while hovered, as
    // the game's settings buttons do, and keeps the row's tip up (it takes the mouse from the
    // row).
    private static void SetUpButton(NButton button, Control row, Row setting)
    {
        button.PivotOffset = button.Size / 2;
        Tween? tween = null;
        void Scale(float to, double seconds)
        {
            tween?.Kill();
            tween = button.CreateTween();
            tween.TweenProperty(button, "scale", Vector2.One * to, seconds).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
        button.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(_ => Scale(1.05f, 0.05)));
        button.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(_ => Scale(1f, 0.5)));
        button.Connect(NClickableControl.SignalName.MousePressed, Callable.From<InputEvent>(_ => Scale(0.95f, 0.25)));
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            Scale(1.05f, 0.05);
            OnAccountButton();
        }));
        ShowTipOnHover(button, row, setting);
    }

    private static void ShowOption(Shown shown) =>
        ((MegaLabel)PaginatorLabelField!.GetValue(shown.Paginator)!).SetTextAutoSize(shown.Offered[shown.Index].Label);

    // The game's paginator only tells subclasses about a change, so the choice is read back
    // every frame. Likewise the login, which changes in the background.
    private static void Update(Shown shown)
    {
        if (shown.Row is Choice choice)
            UpdateChoice(shown, choice);

        var text = shown.Row switch
        {
            AccountRow => $"{shown.Row.Label}   [color={StatusColour}]{AccountStatus()}[/color]",
            Choice { LabelNow: { } now } => now(),
            _ => shown.Row.Label,
        };
        if (text != shown.Text)
            shown.Label.Text = shown.Text = text;
        if (shown.Button != null && AccountAction() is var (action, colour) && action != shown.ButtonText)
        {
            shown.Button.GetNode<MegaLabel>("Label").SetTextAutoSize(shown.ButtonText = action);
            var material = (ShaderMaterial)shown.Button.GetNode<CanvasItem>("Image").Material;
            material.SetShaderParameter("h", colour.H);
            material.SetShaderParameter("s", colour.S);
            material.SetShaderParameter("v", colour.V);
        }

        var enabled = shown.Row.Applies();
        if (enabled == shown.Enabled)
            return;
        shown.Enabled = enabled;
        shown.Label.Modulate = enabled ? Colors.White : DisabledText;
        IEnumerable<NClickableControl> controls = shown.Paginator != null
            ? shown.Paginator.GetChildren().OfType<NClickableControl>()
            : new NClickableControl?[] { shown.Tickbox, shown.Button }.OfType<NClickableControl>();
        foreach (var control in controls)
        {
            if (enabled)
                control.Enable();
            else
                control.Disable();
        }
    }

    // Offers the options available now. The player paging to another saves it.
    private static void UpdateChoice(Shown shown, Choice choice)
    {
        var offered = choice.Options.Where(o => o.Offered).ToArray();
        var options = (List<string>)PaginatorOptionsField!.GetValue(shown.Paginator)!;
        if (!offered.SequenceEqual(shown.Offered))
        {
            shown.Offered = offered;
            options.Clear();
            options.AddRange(offered.Select(o => o.Label));
            var chosen = Settings.Get(choice.Key).AsString();
            shown.Index = Array.FindIndex(offered, o => o.Value == chosen);
            if (shown.Index < 0)
                shown.Index = Math.Max(0, Array.FindIndex(offered, o => o.Value == choice.Fallback));
            PaginatorIndexField!.SetValue(shown.Paginator, shown.Index);
            ShowOption(shown);
            return;
        }
        var index = (int)PaginatorIndexField!.GetValue(shown.Paginator)!;
        if (index == shown.Index)
            return;
        shown.Index = index;
        ShowOption(shown);
        Settings.Set(choice.Key, offered[index].Value);
    }

    private static readonly Color DisabledText = new(1, 1, 1, 0.4f);
    // The account's status beside its name, quieter, like the hint grey of the game's tips.
    private const string StatusColour = "#8f9b9d";

    private static string AccountStatus() => Account.Status switch
    {
        Account.State.Checking => "Checking the login",
        Account.State.LoggingIn => "Finish logging in in the browser",
        Account.State.Connected => Account.Email == "" ? "Logged in" : Account.Email,
        Account.State.Expired => "Login expired, log in again",
        Account.State.Offline => "Not reachable right now",
        _ => Account.Problem == "" ? "Log in for global pick and win rates" : $"Login failed: {Account.Problem.Replace("[", "[lb]")}",
    };

    // The button does what the login needs next: green to log in, red to log out or cancel.
    private static (string Action, (Variant H, Variant S, Variant V) Colour) AccountAction() => Account.Status switch
    {
        Account.State.NotLoggedIn or Account.State.Expired => ("Log in", _goColour),
        Account.State.LoggingIn => ("Cancel", _undoColour),
        _ => ("Log out", _undoColour),
    };

    private static void OnAccountButton()
    {
        switch (Account.Status)
        {
            case Account.State.NotLoggedIn or Account.State.Expired:
                Account.LogIn();
                break;
            case Account.State.LoggingIn:
                Account.CancelLogIn();
                break;
            default:
                Account.LogOut();
                break;
        }
    }

    private static IEnumerable<Node> SelfAndDescendants(Node node) =>
        node.GetChildren().SelectMany(SelfAndDescendants).Prepend(node);

    private static T? FindDescendant<T>(Node node) where T : Node =>
        SelfAndDescendants(node).Skip(1).OfType<T>().FirstOrDefault();
}
