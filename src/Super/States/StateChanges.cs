namespace Super;

/// <summary>
/// Where every <see cref="States{TState}"/> queues its changes, so the game loop can land
/// them all at one moment. Games have no need for it.
/// </summary>
public sealed class StateChanges
{
    /// <summary>More changes than this in one tick can only be a loop.</summary>
    internal const int MaxChangesPerTick = 32;

    private readonly List<IStateMachine> _machines = [];

    /// <summary>False during Draw and Ui, which describe state and so can't change it.</summary>
    internal bool Open { get; set; } = true;

    internal IStateHook[] Hooks { get; set; } = [];

    internal void Add(IStateMachine machine) => _machines.Add(machine);

    /// <summary>Runs every change that's due, including any that callbacks ask for along the way.</summary>
    internal void Apply()
    {
        int changes = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < _machines.Count; i++)
            {
                var machine = _machines[i];
                if (!machine.ChangeDue)
                {
                    continue;
                }

                if (++changes > MaxChangesPerTick)
                {
                    throw new InvalidOperationException($"States changed more than {MaxChangesPerTick} times in one tick, so an enter or exit callback is probably looping.");
                }

                machine.Change(Hooks);
                changed = true;
            }
        }
    }
}

/// <summary>The part of a state machine that doesn't depend on its enum.</summary>
internal interface IStateMachine
{
    bool ChangeDue { get; }

    void Change(IStateHook[] hooks);
}
