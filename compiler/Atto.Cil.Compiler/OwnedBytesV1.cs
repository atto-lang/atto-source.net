// Candidate host runtime primitive for the future sealed Atto OwnedBytes ABI.
// NOT an Atto guest, automatic TenantVM isolation, or a built-in allocator.
// The caller is trusted host code supplying an authenticated owner context.
namespace Atto.Cil.Compiler;

internal readonly record struct ByteOwnerV1(long Tenant, long Actor, long Generation);
internal enum ByteOrderV1 { Little, Big }
internal enum ByteFaultV1 { Owner, Bounds, Value, Quota, Retired }
internal sealed class ByteFailureV1 : InvalidOperationException
{
    internal ByteFaultV1 Kind { get; }
    internal ByteFailureV1(ByteFaultV1 kind) : base(kind.ToString()) => Kind = kind;
}

internal sealed class OwnedBytesV1 : IDisposable
{
    private readonly ByteOwnerV1 owner;
    private readonly byte[] bytes;
    private readonly object gate = new();
    private bool retired;

    internal OwnedBytesV1(ByteOwnerV1 owner, long length, long regionLimit)
    {
        if (owner.Tenant <= 0 || owner.Actor <= 0 || owner.Generation <= 0)
            throw new ByteFailureV1(ByteFaultV1.Owner);
        if (length < 0 || regionLimit < 0 || length > regionLimit || length > Array.MaxLength)
            throw new ByteFailureV1(ByteFaultV1.Quota);
        this.owner = owner;
        bytes = new byte[(int)length]; // CLR specifies zero initialization
    }

    private void RequireOwner(ByteOwnerV1 caller)
    {
        if (retired) throw new ByteFailureV1(ByteFaultV1.Retired);
        if (caller != owner) throw new ByteFailureV1(ByteFaultV1.Owner);
    }

    private int Offset(ByteOwnerV1 caller, long at, int width)
    {
        RequireOwner(caller);
        if (width != 1 && width != 2 && width != 4)
            throw new ByteFailureV1(ByteFaultV1.Value);
        if (at < 0 || width > bytes.Length || at > (long)bytes.Length - width)
            throw new ByteFailureV1(ByteFaultV1.Bounds);
        return (int)at;
    }

    internal int Length(ByteOwnerV1 caller)
    {
        lock (gate) { RequireOwner(caller); return bytes.Length; }
    }

    internal long Read(ByteOwnerV1 caller, long at, int width, ByteOrderV1 order)
    {
        lock (gate)
        {
            if (order != ByteOrderV1.Little && order != ByteOrderV1.Big)
                throw new ByteFailureV1(ByteFaultV1.Value);
            int start = Offset(caller, at, width);
            ulong result = 0;
            for (int i = 0; i < width; i++)
            {
                int shift = 8 * (order == ByteOrderV1.Little ? i : width - i - 1);
                result |= (ulong)bytes[start + i] << shift;
            }
            return (long)result;
        }
    }

    internal void Write(ByteOwnerV1 caller, long at, int width, long value, ByteOrderV1 order)
    {
        lock (gate)
        {
            if (order != ByteOrderV1.Little && order != ByteOrderV1.Big)
                throw new ByteFailureV1(ByteFaultV1.Value);
            int start = Offset(caller, at, width);
            ulong max = (1UL << (8 * width)) - 1;
            if (value < 0 || (ulong)value > max)
                throw new ByteFailureV1(ByteFaultV1.Value);
            // All errors before mutation; these byte stores are infallible.
            for (int i = 0; i < width; i++)
            {
                int shift = 8 * (order == ByteOrderV1.Little ? i : width - i - 1);
                bytes[start + i] = (byte)(((ulong)value >> shift) & 255);
            }
        }
    }

    internal void Retire(ByteOwnerV1 caller)
    {
        lock (gate)
        {
            RequireOwner(caller);
            Array.Clear(bytes);
            retired = true;
        }
    }

    public void Dispose()
    {
        lock (gate) { Array.Clear(bytes); retired = true; }
    }
}
