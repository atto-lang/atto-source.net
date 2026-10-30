namespace Ores.Net.Compiler;

// Compile-time declaration identity, NOT a method runtime or CIL dispatcher.
// The source parser must pass caller-visible value arity after erasing self.
public enum ReceiverMethodNamespace { Instance, Static }

public readonly record struct NominalReceiverMethodSlot(
    string Owner, ReceiverMethodNamespace Namespace, string Name, int ValueArity);

public sealed class NominalReceiverMethodSlots
{
    private readonly HashSet<NominalReceiverMethodSlot> slots = new();

    public NominalReceiverMethodSlot Declare(
        string owner, string name, int valueArity,
        ReceiverMethodNamespace methodNamespace = ReceiverMethodNamespace.Instance,
        bool explicitSelf = false, bool mutableSelf = false)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            throw new FormatException("Nominal method owner and name must be nonempty");
        if (valueArity < 0)
            throw new FormatException("Nominal method value arity must be nonnegative");
        if (mutableSelf && !explicitSelf)
            throw new FormatException("Mutable self requires an explicit receiver marker");
        if (methodNamespace == ReceiverMethodNamespace.Static && (explicitSelf || mutableSelf))
            throw new FormatException("Static method cannot declare an instance self receiver");
        if (!Enum.IsDefined(methodNamespace))
            throw new FormatException("Invalid nominal method namespace");
        var slot = new NominalReceiverMethodSlot(owner, methodNamespace, name, valueArity);
        if (!slots.Add(slot))
            throw new FormatException(
                $"Duplicate nominal method slot '{owner}::{methodNamespace}::{name}/{valueArity}': " +
                "self and mut self do not count toward caller-visible arity");
        return slot;
    }

    public bool Contains(NominalReceiverMethodSlot slot) => slots.Contains(slot);
}
