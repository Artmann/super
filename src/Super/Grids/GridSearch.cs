using System.Globalization;

namespace Super;

/// <summary>The flood fill behind <see cref="Cell.Reachable"/> and <see cref="Hex.Reachable"/>.</summary>
internal static class GridSearch
{
    /// <summary>Dijkstra's walk outwards from the start, stopping where the budget runs out.</summary>
    public static Dictionary<TCell, float> Reachable<TCell>(TCell start, float budget, Func<TCell, float?> cost, TCell[] steps)
        where TCell : struct, IGridCell<TCell>
    {
        if (!(budget >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget), string.Create(CultureInfo.InvariantCulture, $"A walk's budget must be 0 or more, but it is {budget}."));
        }

        ArgumentNullException.ThrowIfNull(cost);
        var spent = new Dictionary<TCell, float> { [start] = 0f };
        var open = new PriorityQueue<TCell, float>();
        open.Enqueue(start, 0f);
        while (open.TryDequeue(out var cell, out float sofar))
        {
            // A cell is queued again each time a cheaper way to it turns up; only the cheapest counts.
            if (sofar > spent[cell])
            {
                continue;
            }

            foreach (var step in steps)
            {
                var next = cell + step;
                float total = sofar + Enter(cost, next);
                if (total <= budget && !(spent.TryGetValue(next, out float known) && known <= total))
                {
                    spent[next] = total;
                    open.Enqueue(next, total);
                }
            }
        }

        return spent;
    }

    /// <summary>What stepping into a cell costs, with a blocked cell costing more than any budget.</summary>
    private static float Enter<TCell>(Func<TCell, float?> cost, TCell cell)
        where TCell : struct, IGridCell<TCell>
    {
        if (cost(cell) is not { } enter)
        {
            return float.PositiveInfinity;
        }

        if (!(enter >= 0f))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Stepping into {cell} costs {enter}, but a step must cost 0 or more. Return null for a cell nothing can enter."),
                nameof(cost));
        }

        return enter;
    }
}
