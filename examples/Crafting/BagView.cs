using Super;
using Super.Ui;
using static Crafting.BenchColors;

namespace Crafting;

/// <summary>The bag along the bottom: a grid of slots, each stack dragged to move it and hovered for its tooltip.</summary>
internal sealed class BagView(Inventory bag, Catalog<Item> items, Func<string, float, UiView> icon)
{
    /// <summary>Slots across the bag.</summary>
    public const int Columns = 12;

    /// <summary>Rows of slots.</summary>
    public const int Rows = 3;

    /// <summary>How wide and high a slot is, in a 1280 by 720 window.</summary>
    public const float SlotSize = 64f;

    /// <summary>The gap between slots, and between the bench's panels.</summary>
    public const float Gap = 8f;

    /// <summary>The bag's panel, <paramref name="width"/> by <paramref name="height"/>, padded by <paramref name="padding"/>.</summary>
    public UiView Panel(float width, float height, float padding)
    {
        var grid = new Grid(Columns, Gap);
        for (int slot = 0; slot < bag.Slots; slot++)
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
        .Padding(padding)
        .Frame(width, height, alignment: Alignment.TopLeading)
        .Background(BenchColors.Panel)
        .Border(1f, Edge)
        .CornerRadius(10f);
    }

    private UiView Slot(int slot)
    {
        var stack = bag[slot];
        if (stack is null)
        {
            return new Pressable(look => SlotFace(stack, look))
                .DropTarget<int>(from => bag.Move(from, slot), from => from != slot);
        }

        var item = items[stack.Item];
        return new Pressable(look => SlotFace(stack, look))
            .Draggable(slot, icon(stack.Item, 56f).Opacity(0.85f))
            .Tooltip(new VStack(HorizontalAlignment.Leading, 4f)
                {
                    new Text(item.Name).Bold(),
                    new Text(item.Description).Foreground(Muted).Frame(maxWidth: 240f, alignment: Alignment.Leading),
                }
                .Padding(10f)
                .Background(SlotFill)
                .Border(1f, Edge)
                .CornerRadius(6f))
            .DropTarget<int>(from => bag.Move(from, slot), from => from != slot);
    }

    private UiView SlotFace(Stack? stack, PressState look)
    {
        var face = new ZStack(Alignment.BottomTrailing);
        if (stack is not null)
        {
            face.Add(icon(stack.Item, 52f).Frame(SlotSize, SlotSize));
            face.Add(new Text($"{stack.Count}").Bold().FontSize(13f).MonospacedDigit().Padding(5f));
        }

        return face
            .Frame(SlotSize, SlotSize)
            .Background(look.Hovered ? SlotHover : SlotFill)
            .Border(1f, look.Hovered ? Muted : Edge)
            .CornerRadius(6f);
    }
}
