namespace Atto.Net.ActorRuntime;

// Fixed-profile host calculation. No guest lowering, allocator, or pool authority.
public static class ActorSpawnChargePlan
{
    public enum Affinity { None, Sticky, Pinned }
    public readonly record struct Policy(bool Serial, int CarrierCap, int DefaultCarriers,
        long LiveStackSlots, long HeapLimit, long StackLimit, long MailboxMessagesLimit, long MailboxByteCharge);
    public readonly record struct Defaults(Affinity Affinity, long HeapBytes, long StackBytes, long MailboxMessages);
    public readonly record struct Request(int? Carriers = null, Affinity? Affinity = null,
        long? HeapBytes = null, long? StackBytes = null, long? MailboxMessages = null);
    public readonly record struct Plan(int CarrierCap, Affinity Affinity, long StackBytes,
        long Heap, long Stacks, long MailboxBytes, long Messages);
    public static Plan Resolve(Policy policy, Defaults defaults, Request request)
    {
        if (policy.CarrierCap <= 0 || policy.DefaultCarriers <= 0 || policy.DefaultCarriers > policy.CarrierCap
            || policy.LiveStackSlots <= 0 || policy.HeapLimit <= 0 || policy.StackLimit <= 0
            || policy.MailboxMessagesLimit <= 0 || policy.MailboxByteCharge <= 0
            || (policy.Serial && (policy.CarrierCap != 1 || policy.DefaultCarriers != 1)))
            throw new ArgumentException("E_SPAWN_POLICY");
        _ = Validate(policy, policy.DefaultCarriers, defaults.Affinity,
            defaults.HeapBytes, defaults.StackBytes, defaults.MailboxMessages);
        return Validate(policy, request.Carriers ?? policy.DefaultCarriers, request.Affinity ?? defaults.Affinity,
            request.HeapBytes ?? defaults.HeapBytes, request.StackBytes ?? defaults.StackBytes,
            request.MailboxMessages ?? defaults.MailboxMessages);
    }
    private static Plan Validate(Policy p, int cap, Affinity affinity, long heap, long stack, long messages)
    {
        if (cap <= 0 || cap > p.CarrierCap ||
            affinity is not (Affinity.None or Affinity.Sticky or Affinity.Pinned) ||
            (affinity == Affinity.Pinned && cap != 1))
            throw new ArgumentException("E_SPAWN_POLICY");
        if (heap <= 0 || heap > p.HeapLimit || stack <= 0 || stack > p.StackLimit
            || messages <= 0 || messages > p.MailboxMessagesLimit
            || stack > long.MaxValue / p.LiveStackSlots)
            throw new ArgumentException("E_SPAWN_QUOTA");
        return new Plan(cap, affinity, stack, heap, checked(stack * p.LiveStackSlots),
            p.MailboxByteCharge, messages);
    }
}
