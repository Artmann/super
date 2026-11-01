namespace Crafting;

/// <summary>What something is, from content/items.</summary>
public sealed record Item(string Id, string Name, string Description, string Icon);

/// <summary>One thing a recipe uses up.</summary>
public sealed record Ingredient(string Item, int Count);

/// <summary>How to make an item, from content/recipes. The id names the item it makes.</summary>
public sealed record Recipe(string Id, int Makes, IReadOnlyList<Ingredient> Needs);

/// <summary>Some of one item in a slot.</summary>
public sealed record Stack(string Item, int Count);

/// <summary>Numbered slots holding stacks. It knows nothing about the screen.</summary>
public sealed class Inventory(int slots)
{
    public const int MaxStack = 99;

    private readonly Stack?[] _slots = new Stack?[slots];

    public int Slots => _slots.Length;

    public Stack? this[int slot] => _slots[slot];

    /// <summary>How much of an item there is, across every slot.</summary>
    public int Count(string item)
    {
        int total = 0;
        foreach (var stack in _slots)
        {
            if (stack?.Item == item)
            {
                total += stack.Count;
            }
        }

        return total;
    }

    /// <summary>
    /// Puts some of an item away, topping up its stacks before taking empty slots. Returns
    /// false, changing nothing, if it wouldn't all fit.
    /// </summary>
    public bool Add(string item, int count)
    {
        int room = 0;
        foreach (var stack in _slots)
        {
            room += stack is null ? MaxStack : stack.Item == item ? MaxStack - stack.Count : 0;
        }

        if (room < count)
        {
            return false;
        }

        for (int slot = 0; slot < _slots.Length && count > 0; slot++)
        {
            if (_slots[slot] is { } stack && stack.Item == item)
            {
                count -= TopUp(slot, stack, count);
            }
        }

        for (int slot = 0; slot < _slots.Length && count > 0; slot++)
        {
            if (_slots[slot] is null)
            {
                int taken = Math.Min(count, MaxStack);
                _slots[slot] = new Stack(item, taken);
                count -= taken;
            }
        }

        return true;
    }

    /// <summary>Moves a stack onto another slot: it merges with the same item, and swaps with anything else.</summary>
    public void Move(int from, int to)
    {
        if (from == to || _slots[from] is not { } moving)
        {
            return;
        }

        if (_slots[to] is { } there && there.Item == moving.Item)
        {
            int left = moving.Count - TopUp(to, there, moving.Count);
            _slots[from] = left > 0 ? moving with { Count = left } : null;
            return;
        }

        _slots[from] = _slots[to];
        _slots[to] = moving;
    }

    /// <summary>Whether there's enough of everything this recipe needs.</summary>
    public bool Has(Recipe recipe)
    {
        foreach (var need in recipe.Needs)
        {
            if (Count(need.Item) < need.Count)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Uses up a recipe's ingredients for its result. If it can't be done, nothing changes.</summary>
    public bool Craft(Recipe recipe)
    {
        if (!Has(recipe))
        {
            return false;
        }

        var before = (Stack?[])_slots.Clone();
        foreach (var need in recipe.Needs)
        {
            Remove(need.Item, need.Count);
        }

        if (Add(recipe.Id, recipe.Makes))
        {
            return true;
        }

        before.CopyTo(_slots, 0);
        return false;
    }

    private int TopUp(int slot, Stack stack, int count)
    {
        int taken = Math.Min(count, MaxStack - stack.Count);
        _slots[slot] = stack with { Count = stack.Count + taken };
        return taken;
    }

    private void Remove(string item, int count)
    {
        for (int slot = _slots.Length - 1; slot >= 0 && count > 0; slot--)
        {
            if (_slots[slot] is { } stack && stack.Item == item)
            {
                int taken = Math.Min(count, stack.Count);
                _slots[slot] = stack.Count > taken ? stack with { Count = stack.Count - taken } : null;
                count -= taken;
            }
        }
    }
}
