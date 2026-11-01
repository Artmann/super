using System.Numerics;
using Super;
using Super.Ui;

namespace Crafting;

/// <summary>A crafting bench: pick a recipe, see what it takes, make it, and sort the bag by hand.</summary>
public sealed class CraftingGame(Content content) : IGame
{
    public const int Columns = 12;
    public const int Rows = 3;
    public const float SlotSize = 64f;
    public const float Gap = 8f;

    private const float Margin = 24f;
    private const float PanelPadding = 16f;
    private const float TopHeight = 420f;
    private const float RecipesWidth = 360f;
    private const float BagHeight = 240f;
    private const float BagWidth = 1232f;

    /// <summary>Where the bag's first slot starts, in a 1280 by 720 window.</summary>
    public static readonly Vector2 BagOrigin = new(Margin + PanelPadding, Margin + TopHeight + Gap + 4f + PanelPadding);

    private static readonly Color Page = Color.Hex("#14171F");
    private static readonly Color Panel = Color.Hex("#1E232E");
    private static readonly Color Edge = Color.Hex("#343B4A");
    private static readonly Color SlotFill = Color.Hex("#161A23");
    private static readonly Color SlotHover = Color.Hex("#2A3140");
    private static readonly Color Muted = Color.Hex("#9AA4B5");
    private static readonly Color Enough = Color.Hex("#8CCB8C");
    private static readonly Color Lacking = Color.Hex("#E08A7C");

    private Catalog<Item> _items = null!;
    private Catalog<Recipe> _recipes = null!;
    private readonly Dictionary<string, GameImage> _icons = [];

    public Inventory Bag { get; } = new(Columns * Rows);

    public Recipe? Selected { get; private set; }

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["tooltip"] = new InputScript().MoveMouse(SlotCenter(2)).Wait(30),
        ["dragging"] = new InputScript().MoveMouse(SlotCenter(0)).Wait(1).Hold(MouseButton.Left).Wait(1).MoveMouse(SlotCenter(5)).Wait(2).MoveMouse(SlotCenter(14)).Wait(30),
        ["scrolled"] = new InputScript().Click(RecipeRow(3)).MoveMouse(RecipeRow(5)).Wait(1).Scroll(-3f).Wait(30),
        ["crafted"] = new InputScript().Click(RecipeRow(1)).Click(CraftButton).Click(CraftButton).MoveMouse(CraftButton).Wait(30),
    };

    /// <summary>The Craft button, in a 1280 by 720 window.</summary>
    public static Vector2 CraftButton => new(Margin + RecipesWidth + Gap + 4f + PanelPadding + 40f, Margin + TopHeight - PanelPadding - 16f);

    public static Vector2 SlotCenter(int slot) =>
        BagOrigin + new Vector2(slot % Columns, slot / Columns) * (SlotSize + Gap) + new Vector2(SlotSize / 2f);

    /// <summary>The middle of a recipe's row, before any scrolling.</summary>
    public static Vector2 RecipeRow(int row) => new(Margin + PanelPadding + 100f, Margin + PanelPadding + 50f + row * 44f);

    public void Load()
    {
        _items = content.LoadAll<Item>("items");
        _recipes = content.LoadAll<Recipe>("recipes");
        foreach (var item in _items)
        {
            _icons[item.Id] = content.Image(item.Icon);
        }

        Bag.Add("wood", 12);
        Bag.Add("stone", 7);
        Bag.Add("iron", 9);
        Bag.Add("coal", 5);
        Bag.Add("leather", 6);
        Bag.Add("cloth", 3);
        Bag.Add("herb", 8);
        Bag.Add("fiber", 10);
        Bag.Add("ingot", 2);
        Selected = _recipes["bandage"];
    }

    public void Update(GameTime time)
    {
    }

    public void Draw(View view)
    {
    }

    public UiView? Ui => new VStack(HorizontalAlignment.Leading, Gap + 4f)
    {
        new HStack(VerticalAlignment.Top, Gap + 4f)
        {
            RecipePanel(),
            DetailPanel(),
        },
        BagPanel(),
    }
    .Padding(Margin)
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading)
    .Background(Page);

    private static UiView Card(UiView content, float? width, float height) =>
        content.Padding(PanelPadding)
            .Frame(width, height, alignment: Alignment.TopLeading)
            .Background(Panel)
            .Border(1f, Edge)
            .CornerRadius(10f);

    private UiView Icon(string item, float size) =>
        new Image(_icons[item], ImageFit.Contain).Frame(size, size);

    private UiView RecipePanel() => Card(
        new VStack(HorizontalAlignment.Leading, 8f)
        {
            new Text("Recipes").Bold().FontSize(20f),
            new ListView<Recipe>(_recipes.ToArray(), RecipeRowView, Selected, recipe => Selected = recipe)
                .Frame(height: TopHeight - PanelPadding * 2f - 36f),
        },
        RecipesWidth,
        TopHeight);

    private UiView RecipeRowView(Recipe recipe) => new HStack(Spacing: 10f)
    {
        Icon(recipe.Id, 32f),
        new Text(_items[recipe.Id].Name).Foreground(Bag.Has(recipe) ? Color.White : Muted),
    };

    private UiView DetailPanel()
    {
        if (Selected is not { } recipe)
        {
            return Card(new Text("Pick a recipe").Foreground(Muted), BagWidth - RecipesWidth - Gap - 4f, TopHeight);
        }

        var item = _items[recipe.Id];
        var needs = new VStack(HorizontalAlignment.Leading, 8f) { new Text("Needs").Bold() };
        foreach (var need in recipe.Needs)
        {
            int have = Bag.Count(need.Item);
            needs.Add(new HStack(Spacing: 10f)
            {
                Icon(need.Item, 32f),
                new Text(_items[need.Item].Name).Frame(width: 160f, alignment: Alignment.Leading),
                new Text($"{have} / {need.Count}").MonospacedDigit().Foreground(have >= need.Count ? Enough : Lacking),
            });
        }

        return Card(
            new VStack(HorizontalAlignment.Leading, 14f)
            {
                new HStack(Spacing: 16f)
                {
                    Icon(item.Id, 96f),
                    new VStack(HorizontalAlignment.Leading, 6f)
                    {
                        new Text(recipe.Makes > 1 ? $"{item.Name} x{recipe.Makes}" : item.Name).Bold().FontSize(26f),
                        new Text(item.Description).Foreground(Muted),
                    },
                },
                needs,
                new Spacer(),
                new Button("Craft", () => Bag.Craft(recipe), Disabled: !Bag.Has(recipe)),
            },
            BagWidth - RecipesWidth - Gap - 4f,
            TopHeight);
    }

    private UiView BagPanel()
    {
        var grid = new Grid(Columns, Gap);
        for (int slot = 0; slot < Bag.Slots; slot++)
        {
            grid.Add(Slot(slot).Id(slot));
        }

        return new HStack(VerticalAlignment.Top, 24f)
        {
            grid.Frame(width: Columns * SlotSize + (Columns - 1) * Gap),
            new VStack(HorizontalAlignment.Leading, 8f)
            {
                new Text("Bag").Bold().FontSize(20f),
                new Text("Drag a stack to move it. Drop it on the same item to merge.").Foreground(Muted),
            },
        }
        .Padding(PanelPadding)
        .Frame(BagWidth, BagHeight, alignment: Alignment.TopLeading)
        .Background(Panel)
        .Border(1f, Edge)
        .CornerRadius(10f);
    }

    private UiView Slot(int slot)
    {
        var stack = Bag[slot];
        if (stack is null)
        {
            return new Pressable(look => SlotFace(stack, look))
                .DropTarget<int>(from => Bag.Move(from, slot), from => from != slot);
        }

        var item = _items[stack.Item];
        return new Pressable(look => SlotFace(stack, look))
            .Draggable(slot, Icon(stack.Item, 56f).Opacity(0.85f))
            .Tooltip(new VStack(HorizontalAlignment.Leading, 4f)
                {
                    new Text(item.Name).Bold(),
                    new Text(item.Description).Foreground(Muted).Frame(maxWidth: 240f, alignment: Alignment.Leading),
                }
                .Padding(10f)
                .Background(SlotFill)
                .Border(1f, Edge)
                .CornerRadius(6f))
            .DropTarget<int>(from => Bag.Move(from, slot), from => from != slot);
    }

    private UiView SlotFace(Stack? stack, PressState look)
    {
        var face = new ZStack(Alignment.BottomTrailing);
        if (stack is not null)
        {
            face.Add(Icon(stack.Item, 52f).Frame(SlotSize, SlotSize));
            face.Add(new Text($"{stack.Count}").Bold().FontSize(13f).MonospacedDigit().Padding(5f));
        }

        return face
            .Frame(SlotSize, SlotSize)
            .Background(look.Hovered ? SlotHover : SlotFill)
            .Border(1f, look.Hovered ? Muted : Edge)
            .CornerRadius(6f);
    }
}
