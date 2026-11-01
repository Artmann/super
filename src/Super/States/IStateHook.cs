namespace Super;

/// <summary>
/// Something told about every state change, in any <see cref="States{TState}"/>. Register
/// one as a service to do bookkeeping, like removing what belonged to the state just left.
/// </summary>
public interface IStateHook
{
    /// <summary>Runs after a state's exit callbacks, while it's still the current state.</summary>
    void Exited<TState>(TState state)
        where TState : struct, Enum;

    /// <summary>Runs after the new state's enter callbacks.</summary>
    void Entered();
}
