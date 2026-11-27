using System.Globalization;
using Super;
using Super.Ui;

namespace StorybookExample;

/// <summary>What the settings screen is set to.</summary>
internal sealed record SettingsValues(
    string Resolution = "1440p",
    bool Fullscreen = true,
    bool VSync = true,
    string Preset = "high",
    float FieldOfView = 90f,
    float Master = 0.8f,
    float Music = 0.5f,
    bool Subtitles = true,
    float Sensitivity = 5f,
    bool InvertLook = false);

/// <summary>A settings screen: tabs of sections, with a footer to reset or apply.</summary>
internal sealed record SettingsScreen : UiView
{
    private static readonly PickerOption<string>[] Resolutions =
    [
        new("1920 × 1080", "1080p"),
        new("2560 × 1440", "1440p"),
        new("3840 × 2160", "2160p"),
    ];

    public override UiView Body
    {
        get
        {
            var values = UseState(new SettingsValues());
            var applied = UseState(false);
            void Set(SettingsValues changed)
            {
                values.Value = changed;
                applied.Value = false;
            }

            var v = values.Value;
            return new VStack(HorizontalAlignment.Leading, 16f)
            {
                new HStack { new Text("Settings").TextStyle(TextStyle.Title2), new Spacer(), ShowcaseParts.Hint("Esc", "Back") },
                new TabView
                {
                    new Tab("Video", Video(v, Set)),
                    new Tab("Audio", Audio(v, Set)),
                    new Tab("Controls", Controls(v, Set)),
                }.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading),
                new HStack(Spacing: 8f)
                {
                    new Button("Reset to defaults", () => Set(new SettingsValues())) { Style = ButtonStyle.Ghost }.Id("reset"),
                    new Spacer(),
                    new Text(applied.Value ? "Applied." : "").Foreground(Tone.Muted).Id("applied"),
                    new Button("Apply", () => applied.Value = true).Id("apply"),
                },
            };
        }
    }

    private static UiView Video(SettingsValues v, Action<SettingsValues> set) => Page(
        new Section("Display")
        {
            new LabeledContent("Resolution", new Picker<string>("Resolution", v.Resolution, Resolutions, value => set(v with { Resolution = value })) { Style = PickerStyle.Menu }),
            new Toggle("Fullscreen", v.Fullscreen, on => set(v with { Fullscreen = on })).Id("fullscreen"),
            new Toggle("Vertical sync", v.VSync, on => set(v with { VSync = on })),
        },
        new Section("Quality", "Ultra needs a recent graphics card.")
        {
            new Picker<string>("Preset", v.Preset, StorybookExample.Controls.Presets, value => set(v with { Preset = value })).Id("preset"),
            Labelled("Field of view", new Slider(v.FieldOfView, value => set(v with { FieldOfView = value }), 60f, 120f) { Step = 5f, Label = "Field of view" }, UiSemantics.Number(v.FieldOfView)),
        });

    private static UiView Audio(SettingsValues v, Action<SettingsValues> set) => Page(
        new Section("Volume")
        {
            Labelled("Master", new Slider(v.Master, value => set(v with { Master = value })) { Label = "Master" }.Id("master"), Percent(v.Master)),
            Labelled("Music", new Slider(v.Music, value => set(v with { Music = value })) { Label = "Music" }, Percent(v.Music)),
        },
        new Section("Speech")
        {
            new Toggle("Subtitles", v.Subtitles, on => set(v with { Subtitles = on })),
        });

    private static UiView Controls(SettingsValues v, Action<SettingsValues> set) => Page(
        new Section("Camera")
        {
            Labelled("Sensitivity", new Slider(v.Sensitivity, value => set(v with { Sensitivity = value }), 1f, 10f) { Step = 1f, Label = "Sensitivity" }, UiSemantics.Number(v.Sensitivity)),
            new Toggle("Invert look", v.InvertLook, on => set(v with { InvertLook = on })),
        });

    private static UiView Page(params Section[] sections)
    {
        var form = new Form();
        form.Add(sections);
        return form.Padding(0f, 16f);
    }

    /// <summary>A label, a control that stretches, and its reading at the end.</summary>
    private static UiView Labelled(string label, UiView control, string reading) => new HStack(Spacing: 16f)
    {
        new Text(label).Frame(width: 140f, alignment: Alignment.Leading),
        control,
        new Text(reading).MonospacedDigit().Foreground(Tone.Muted).Frame(width: 44f, alignment: Alignment.Trailing),
    };

    private static string Percent(float fraction) => (MathF.Round(fraction * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>An item in the inventory: what it's called and what kind, how rare, how many, and a line about it.</summary>
internal sealed record Item(string Name, string Kind, string Rarity, int Count, string About, float Durability);

/// <summary>An inventory: a grid of slots to pick from, and the picked item's details beside it.</summary>
internal sealed record InventoryScreen : UiView
{
    private const int Columns = 6;
    private const float Side = 64f;
    private const float Gap = 8f;

    private static readonly Item[] Items =
    [
        new("Iron sword", "Weapon", "Common", 1, "A plain blade, well kept. It holds an edge.", 0.8f),
        new("Ember bow", "Weapon", "Rare", 1, "Arrows loosed from it smoulder where they land.", 0.55f),
        new("Healing potion", "Potion", "Common", 6, "Mends a third of your health over a few seconds.", 1f),
        new("Lamplighter's cloak", "Armour", "Epic", 1, "Warm, and darker than the night it was woven in.", 0.9f),
        new("Rope", "Tool", "Common", 2, "Thirty feet of it. Climbs down, or ties things up.", 1f),
        new("Crown shard", "Quest item", "Legendary", 1, "One of three pieces. It's warm to the touch.", 1f),
        new("Arrows", "Ammunition", "Common", 48, "Fletched with crow feathers.", 1f),
        new("Torch", "Tool", "Common", 3, "Burns for ten minutes.", 0.4f),
    ];

    public static Color RarityColor(string rarity) => rarity switch
    {
        "Rare" => Color.Hex("#3B82F6"),
        "Epic" => Color.Hex("#A855F7"),
        "Legendary" => Color.Hex("#F59E0B"),
        _ => Color.Hex("#A1A1AA"),
    };

    public override UiView Body
    {
        get
        {
            var picked = UseState(0);
            return new VStack(HorizontalAlignment.Leading, 20f)
            {
                new HStack(Spacing: 16f)
                {
                    new Text("Inventory").TextStyle(TextStyle.Title2),
                    new Spacer(),
                    new Text("1,250 gold").Foreground(Tone.Warning),
                    new ProgressView("Weight", 42f, 60f).Frame(width: 200f),
                },
                new HStack(VerticalAlignment.Top, 24f)
                {
                    Slots(picked.Value, slot => picked.Value = slot),
                    Details(Items[picked.Value]),
                },
                new HStack(Spacing: 20f) { new Spacer(), ShowcaseParts.Hint("Enter", "Use"), ShowcaseParts.Hint("X", "Drop"), ShowcaseParts.Hint("Esc", "Close") },
            };
        }
    }

    private static UiView Slots(int picked, Action<int> pick)
    {
        var grid = new Grid(Columns: Columns, ColumnGap: Gap, RowGap: Gap);
        for (int i = 0; i < Columns * 4; i++)
        {
            grid.Add(i < Items.Length ? Slot(i, i == picked, pick) : new Spacer().Frame(Side, Side).Background(Color.Hex("#71717A", 0.12f)).CornerRadius(6f));
        }

        return grid.Frame(width: Columns * Side + (Columns - 1) * Gap, alignment: Alignment.Leading);
    }

    private static UiView Slot(int index, bool picked, Action<int> pick)
    {
        var item = Items[index];
        var count = item.Count > 1 ? new Text(item.Count.ToString(CultureInfo.InvariantCulture)).TextStyle(TextStyle.Caption).Padding(3f, 5f) : null;
        return new Pressable(
            look => ShowcaseParts.ItemPicture(item.Name, RarityColor(item.Rarity), Side)
                .Overlay(count ?? new Spacer().Frame(0f, 0f), Alignment.BottomTrailing)
                .Border(picked ? 2f : 1f, picked ? RarityColor(item.Rarity) : look.Hovered ? Color.Hex("#A1A1AA") : Color.Hex("#71717A", 0.4f))
                .CornerRadius(6f),
            () => pick(index))
            .Semantics(UiRole.Button, item.Name)
            .Id($"item-{index + 1}");
    }

    private static UiView Details(Item item) => new Card
    {
        new HStack(Spacing: 14f)
        {
            ShowcaseParts.ItemPicture(item.Name, RarityColor(item.Rarity), 56f),
            new VStack(HorizontalAlignment.Leading, 4f)
            {
                new Text(item.Name).TextStyle(TextStyle.Title3).Id("item-name"),
                new HStack(Spacing: 6f) { new Badge(item.Rarity, BadgeStyle.Outline), new Badge(item.Kind, BadgeStyle.Secondary) },
            },
        },
        new Text(item.About).Foreground(Tone.Muted),
        new LabeledContent("Carried", item.Count.ToString(CultureInfo.InvariantCulture)),
        new ProgressView("Durability", item.Durability),
        new HStack(Spacing: 8f)
        {
            new Button(item.Kind == "Potion" ? "Drink" : "Equip", () => { }),
            new Button("Drop", () => { }) { Style = ButtonStyle.Outline },
        },
    }.Frame(maxWidth: UiExtensions.Infinity);
}

/// <summary>What the character creator is set to.</summary>
internal sealed record Character(string Name = "", string Calling = "ranger", string Origin = "coast", int Strength = 1, int Agility = 1, int Mind = 1, int Look = 0)
{
    public const int Points = 9;

    public int Left => Points - Strength - Agility - Mind;
}

/// <summary>A character creator: a name, a calling and an origin, points to spend, a colour, and a card that shows it all.</summary>
internal sealed record CreatorScreen : UiView
{
    private static readonly PickerOption<string>[] Callings = [new("Warrior", "warrior"), new("Ranger", "ranger"), new("Mage", "mage")];

    private static readonly PickerOption<string>[] Origins = [new("Highlands", "highlands"), new("Coast", "coast"), new("City", "city")];

    private static readonly Color[] Looks = [Color.Hex("#F97316"), Color.Hex("#22C55E"), Color.Hex("#3B82F6"), Color.Hex("#A855F7"), Color.Hex("#E11D48")];

    public override UiView Body
    {
        get
        {
            var made = UseState(new Character());
            var created = UseState(false);
            var c = made.Value;
            // A change after creating makes a new character, which hasn't set out yet.
            Action<Character> set = changed =>
            {
                made.Value = changed;
                created.Value = false;
            };
            return new VStack(HorizontalAlignment.Leading, 20f)
            {
                new Text("Create your character").TextStyle(TextStyle.Title2),
                new HStack(VerticalAlignment.Top, 32f)
                {
                    Choices(c, set).Frame(width: 480f),
                    Preview(c, created.Value, Colours(c, set)),
                },
                new Spacer(),
                new HStack(Spacing: 8f)
                {
                    new Button("Back", () => { }) { Style = ButtonStyle.Outline },
                    new Spacer(),
                    new Text(Ready(c)).Foreground(Tone.Muted).Id("ready"),
                    new Button("Create", () => created.Value = true, Disabled: c.Name.Length == 0 || c.Left > 0).Id("create"),
                },
            }.Frame(maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
        }
    }

    private static string Ready(Character c) =>
        c.Name.Length == 0 ? "Give them a name." : c.Left > 0 ? $"{c.Left} points to spend." : "Ready.";

    private static UiView Choices(Character c, Action<Character> set) => new VStack(HorizontalAlignment.Leading, 18f)
    {
        new TextField("Name", c.Name, name => set(c with { Name = name })).Id("hero-name"),
        Captioned("Calling", new Picker<string>("Calling", c.Calling, Callings, calling => set(c with { Calling = calling })).Id("calling")),
        Captioned("Origin", new Picker<string>("Origin", c.Origin, Origins, origin => set(c with { Origin = origin })).Id("origin")),
        new Section("Attributes", $"{c.Left} of {Character.Points} points left.")
        {
            new Stepper("Strength", c.Strength, value => set(c with { Strength = value }), 1, c.Strength + c.Left).Id("strength"),
            new Stepper("Agility", c.Agility, value => set(c with { Agility = value }), 1, c.Agility + c.Left).Id("agility"),
            new Stepper("Mind", c.Mind, value => set(c with { Mind = value }), 1, c.Mind + c.Left).Id("mind"),
        },
    };

    private static UiView Captioned(string caption, UiView control) => new VStack(HorizontalAlignment.Leading, 6f)
    {
        new Text(caption).TextStyle(TextStyle.Subheadline).Foreground(Tone.Muted),
        control,
    };

    private static UiView Colours(Character c, Action<Character> set)
    {
        var row = new HStack(Spacing: 10f) { new Text("Colour").Frame(width: 64f, alignment: Alignment.Leading) };
        for (int i = 0; i < Looks.Length; i++)
        {
            int look = i;
            bool chosen = c.Look == i;
            row.Add(new Pressable(
                _ => new Circle().Fill(Looks[look]).Frame(26f, 26f).Padding(3f).Overlay(chosen ? new Circle().Stroke(Looks[look], 2f) : new Spacer()),
                () => set(c with { Look = look }))
                .Semantics(UiRole.Button, $"Colour {look + 1}")
                .Id($"colour-{look + 1}"));
        }

        return row;
    }

    private static UiView Preview(Character c, bool created, UiView colours)
    {
        var colour = Looks[c.Look];
        var name = c.Name.Length == 0 ? "Nameless" : c.Name;
        return new Card
        {
            new Text(name[..1]).TextStyle(TextStyle.LargeTitle).Foreground(Color.White).Frame(96f, 96f).Background(new Circle().Fill(Paint.Radial(colour, colour.Mix(Color.Black, 0.5f), Alignment.TopLeading))),
            new Text(name).TextStyle(TextStyle.Title3).Id("preview-name"),
            new HStack(Spacing: 6f) { new Badge(Name(Callings, c.Calling)), new Badge(Name(Origins, c.Origin), BadgeStyle.Secondary) },
            new HStack(Spacing: 16f)
            {
                Score("Strength", c.Strength, colour),
                Score("Agility", c.Agility, colour),
                Score("Mind", c.Mind, colour),
            },
            colours,
            new Text(created ? $"{name} sets out from the {Name(Origins, c.Origin).ToLowerInvariant()}." : " ").Foreground(Tone.Success).Id("created"),
        }.Frame(width: 340f);
    }

    private static UiView Score(string label, int value, Color colour) =>
        new Gauge(label, value, 0f, Character.Points - 2) { Tint = colour, ValueLabel = value.ToString(CultureInfo.InvariantCulture) };

    private static string Name(PickerOption<string>[] options, string value) => options.First(option => option.Value == value).Label;
}
