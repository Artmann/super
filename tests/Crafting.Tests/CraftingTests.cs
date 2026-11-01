using System.Numerics;
using Crafting;
using Super;
using Super.Testing;

namespace Crafting.Tests;

public sealed class CraftingTests
{
    private static HeadlessRun<CraftingGame> Start()
    {
        var run = HeadlessGame.Create<CraftingGame>(1);
        run.Ticks(1);
        return run;
    }

    private static void Drag(HeadlessRun<CraftingGame> run, Vector2 from, Vector2 to)
    {
        run.Input.MoveMouse(from);
        run.Ticks(1);
        run.Input.Hold(MouseButton.Left);
        run.Ticks(1);
        run.Input.MoveMouse(to);
        run.Ticks(1);
        run.Input.Release(MouseButton.Left);
        run.Ticks(2);
    }

    private static void Click(HeadlessRun<CraftingGame> run, Vector2 at)
    {
        run.Input.Click(MouseButton.Left, at);
        run.Ticks(2);
    }

    /// <summary>Where text shows on screen, for clicking what a player would click.</summary>
    private static Vector2 At(HeadlessRun<CraftingGame> run, string text)
    {
        var frame = run.ShownText.First(shown => shown.Text == text).Frame;
        return new Vector2(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);
    }

    [Fact]
    public void Dragging_onto_an_empty_slot_moves_the_stack()
    {
        using var run = Start();
        var moved = run.Game.Bag[0];

        Drag(run, CraftingGame.SlotCenter(0), CraftingGame.SlotCenter(20));

        Assert.Null(run.Game.Bag[0]);
        Assert.Equal(moved, run.Game.Bag[20]);
    }

    [Fact]
    public void Dragging_onto_another_item_swaps()
    {
        using var run = Start();
        var (first, second) = (run.Game.Bag[0], run.Game.Bag[1]);

        Drag(run, CraftingGame.SlotCenter(0), CraftingGame.SlotCenter(1));

        Assert.Equal(second, run.Game.Bag[0]);
        Assert.Equal(first, run.Game.Bag[1]);
    }

    [Fact]
    public void Dragging_onto_the_same_item_merges()
    {
        using var run = TwoStacksOfWood(smallest: 13, largest: 79);

        Drag(run, CraftingGame.SlotCenter(0), CraftingGame.SlotCenter(9));

        Assert.Null(run.Game.Bag[0]);
        Assert.Equal(92, run.Game.Bag[9]?.Count);
    }

    [Fact]
    public void Merging_onto_a_nearly_full_stack_leaves_the_rest_behind()
    {
        using var run = TwoStacksOfWood(smallest: 13, largest: 90);

        Drag(run, CraftingGame.SlotCenter(0), CraftingGame.SlotCenter(9));

        Assert.Equal(4, run.Game.Bag[0]?.Count);
        Assert.Equal(99, run.Game.Bag[9]?.Count);
    }

    /// <summary>The starting 12 wood topped up past one stack, leaving two in slots 0 and 9.</summary>
    private static HeadlessRun<CraftingGame> TwoStacksOfWood(int smallest, int largest)
    {
        var run = Start();
        var bag = run.Game.Bag;
        bag.Add("wood", 87 + smallest);
        bag.Move(0, 9);
        bag.Craft(new Recipe("plank", 1, [new Ingredient("wood", 99 - largest)]));
        run.Ticks(1);
        return run;
    }

    [Fact]
    public void Dropping_outside_the_bag_changes_nothing()
    {
        using var run = Start();
        var moved = run.Game.Bag[0];

        Drag(run, CraftingGame.SlotCenter(0), new Vector2(1200f, 480f));

        Assert.Equal(moved, run.Game.Bag[0]);
    }

    [Fact]
    public void Picking_a_recipe_and_crafting_uses_up_the_ingredients()
    {
        using var run = Start();
        int fiber = run.Game.Bag.Count("fiber");

        run.Input.MoveMouse(At(run, "Boots"));
        run.Ticks(1);
        run.Input.Scroll(-3f);
        run.Ticks(1);
        Click(run, At(run, "Rope"));
        Assert.Equal("rope", run.Game.Selected?.Id);

        Click(run, At(run, "Craft"));

        Assert.Equal(fiber - 3, run.Game.Bag.Count("fiber"));
        Assert.Equal(1, run.Game.Bag.Count("rope"));
    }

    [Fact]
    public void A_recipe_short_of_ingredients_cannot_be_crafted()
    {
        using var run = Start();
        Click(run, At(run, "Bandage"));
        Click(run, At(run, "Craft"));
        Click(run, At(run, "Craft"));
        Click(run, At(run, "Craft"));
        Click(run, At(run, "Craft"));

        Assert.Equal(0, run.Game.Bag.Count("cloth"));
        Assert.Equal(6, run.Game.Bag.Count("bandage"));
    }

    [Fact]
    public void The_wheel_scrolls_the_recipes()
    {
        using var run = Start();
        float before = At(run, "Boots").Y;

        run.Input.MoveMouse(At(run, "Boots"));
        run.Ticks(1);
        run.Input.Scroll(-1f);
        run.Ticks(1);

        Assert.Equal(before - 48f, At(run, "Boots").Y, 0.01f);
    }

    [Fact]
    public void Hovering_a_stack_shows_what_it_is()
    {
        using var run = Start();

        run.Input.MoveMouse(CraftingGame.SlotCenter(0));
        run.Ticks(20);

        Assert.Contains(run.ShownText, shown => shown.Text == "A rough log. Everything starts here.");
    }

    [Fact]
    public void A_full_stack_leaves_what_will_not_fit()
    {
        var bag = new Inventory(2);
        bag.Add("wood", 99);
        bag.Add("wood", 40);

        bag.Move(1, 0);

        Assert.Equal(99, bag[0]?.Count);
        Assert.Equal(40, bag[1]?.Count);
    }

    [Fact]
    public void Crafting_with_nowhere_to_put_the_result_changes_nothing()
    {
        var bag = new Inventory(1);
        bag.Add("wood", 5);
        var recipe = new Recipe("plank", 2, [new Ingredient("wood", 1)]);

        Assert.False(bag.Craft(recipe));
        Assert.Equal(5, bag.Count("wood"));
    }
}
