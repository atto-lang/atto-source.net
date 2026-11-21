namespace Ores.Net.Runtime;

/// <summary>
/// An actor-bound host/compiler handle for one local 32-bit guest reference.
/// The actual native field stays four bytes; this managed value wrapper
/// also binds it to the runtime-minted actor arena identity. It must not be
/// serialized or embedded in guest memory. Default is the canonical null.
/// </summary>
/// <remarks>
/// The older uint32 API remains a trusted low-level ABI, not an authenticated
/// cross-actor capability. Compiler-generated code must use this checked API
/// so two different actors' identical slot/generation words never alias.
/// </remarks>
public readonly struct ActorGuestOwnedRef32
{
    private readonly ActorArena? _owner;
    private readonly uint _word;

    internal ActorGuestOwnedRef32(ActorArena owner, uint word)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (word == 0)
            throw new ArgumentOutOfRangeException(nameof(word),
                "Use default for a null guest reference");
        _owner = owner;
        _word = word;
    }

    public bool IsNull => _word == 0;

    internal uint Word => _word;

    internal bool BelongsTo(ActorArena arena) =>
        _word != 0 && ReferenceEquals(_owner, arena);
}
