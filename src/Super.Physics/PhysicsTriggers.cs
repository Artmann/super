using System.Runtime.InteropServices;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Who is in each <see cref="Trigger"/> area, and who came and went in the last step. Reach it through
/// <c>physics.Triggers</c>.
/// </summary>
public sealed class PhysicsTriggers
{
    private static readonly List<Entity> Nobody = [];

    private readonly World _world;
    private readonly BodySync _bodies;
    private readonly QueryShapeCache _shapes;
    private readonly QueryFilters _filters;
    private readonly ShapeOverlaps _overlaps;
    private readonly Dictionary<Entity, List<Entity>> _inside = [];
    private readonly List<Entity> _touching = [];
    private readonly List<Entity> _gone = [];
    private readonly List<TriggerEvent> _entered = [];
    private readonly List<TriggerEvent> _exited = [];

    internal PhysicsTriggers(World world, BodySync bodies, QueryParts parts)
    {
        _world = world;
        _bodies = bodies;
        (_shapes, _filters, _overlaps) = parts;
    }

    /// <summary>
    /// Who came into a <see cref="Trigger"/> area in the last step, in trigger then entity order. Read it in Update;
    /// the next step replaces it.
    /// </summary>
    public IReadOnlyList<TriggerEvent> Entered => _entered;

    /// <summary>
    /// Who left a <see cref="Trigger"/> area in the last step, in trigger then entity order. An entity destroyed, or
    /// stripped of its collider, while inside leaves too, so check <see cref="World.IsAlive"/> before using it. A
    /// trigger that is itself removed reports no one leaving.
    /// </summary>
    public IReadOnlyList<TriggerEvent> Exited => _exited;

    /// <summary>Who is in a <see cref="Trigger"/> area, as of the last step, in entity order. Empty for anything else.</summary>
    public IReadOnlyList<Entity> Inside(Entity trigger) => _inside.TryGetValue(trigger, out var inside) ? inside : Nobody;

    /// <summary>Finds who is in each trigger the last sync met, and who came and went since the last step.</summary>
    internal void Find()
    {
        _entered.Clear();
        _exited.Clear();
        foreach (var trigger in _bodies.Triggers)
        {
            Check(trigger);
        }

        // A trigger that's gone takes its list with it, quietly.
        _gone.Clear();
        foreach (var (trigger, _) in _inside)
        {
            if (_bodies.Triggers.BinarySearch(trigger, EntityOrder.Instance) < 0)
            {
                _gone.Add(trigger);
            }
        }

        foreach (var trigger in _gone)
        {
            _inside.Remove(trigger);
        }
    }

    /// <summary>Finds who touches one trigger now, and reports the change from who was in it.</summary>
    private void Check(Entity trigger)
    {
        ref var tracked = ref _bodies.Find(trigger);
        var shape = _shapes.Get(tracked.Collider, tracked.Scale, trigger, "trigger");
        var detects = _world.Get<Trigger>(trigger).Detects;
        if (detects == 0 || (detects & ~LayerBits.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Trigger), detects, $"Entity {trigger.Id}'s Trigger detects layers {detects}; layers are bits 0 to 15, and it needs at least one.");
        }

        _filters.Layers.Layers = detects;
        _filters.Alive.Skip = trigger;
        _overlaps.CollideAt(shape, in tracked.Body, _touching);
        _filters.Alive.Skip = null;

        ref var inside = ref CollectionsMarshal.GetValueRefOrAddDefault(_inside, trigger, out _);
        inside ??= [];
        Compare(trigger, inside, _touching);
        inside.Clear();
        inside.AddRange(_touching);
    }

    /// <summary>Walks two lists in entity order, reporting who is only in the new one as entering, and only in the old one as leaving.</summary>
    private void Compare(Entity trigger, List<Entity> before, List<Entity> now)
    {
        int i = 0;
        int j = 0;
        while (i < before.Count || j < now.Count)
        {
            int order = i == before.Count ? 1 : j == now.Count ? -1 : EntityOrder.Instance.Compare(before[i], now[j]);
            if (order < 0)
            {
                _exited.Add(new TriggerEvent(trigger, before[i++]));
            }
            else if (order > 0)
            {
                _entered.Add(new TriggerEvent(trigger, now[j++]));
            }
            else
            {
                i++;
                j++;
            }
        }
    }
}
