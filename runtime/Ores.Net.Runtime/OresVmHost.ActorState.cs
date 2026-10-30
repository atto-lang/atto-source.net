namespace Ores.Net.Runtime;

/// <summary>
/// Trusted NativeAOT host-owned scalar state storage for a single actor
/// instance. This is NOT source compiler lowering or a tenant OS sandbox.
/// Every cell access validates and uses the state loan under one host lock.
/// </summary>
public sealed partial class OresVmHost
{
    public enum ActorStateScalarKind { Int64 = 1, Boolean = 2 }

    public readonly record struct ActorStateField(
        ActorStateScalarKind Kind, long Int64Default = 0, bool BooleanDefault = false);

    internal sealed class ActorStateCell
    {
        internal ActorStateCell(ActorStateField field) {
            Kind = field.Kind;
            Int64Value = field.Int64Default;
            BooleanValue = field.BooleanDefault;
        }

        internal readonly ActorStateScalarKind Kind;
        internal long Int64Value;
        internal bool BooleanValue;
    }

    /// <summary>
    /// Install one bounded, immutable typed layout before the actor runs.
    /// Defaults are copied into actor-local cells; no guest obtains the array.
    /// </summary>
    public void ConfigureActorState(
        IsolateHandle handle, ActorTicket actor, ReadOnlySpan<ActorStateField> fields)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running || actor.ActiveTurn is not null ||
                actor.StateCells is not null)
                throw new InvalidOperationException("actor state layout admission closed");
            if (fields.Length is < 1 or > 16)
                throw new InvalidOperationException("actor state requires 1 to 16 scalar fields");

            var cells = new ActorStateCell[fields.Length];
            for (var i = 0; i < fields.Length; ++i) {
                var field = fields[i];
                if (field.Kind != ActorStateScalarKind.Int64 &&
                    field.Kind != ActorStateScalarKind.Boolean)
                    throw new InvalidOperationException("unsupported actor state scalar kind");
                if ((field.Kind == ActorStateScalarKind.Int64 && field.BooleanDefault) ||
                    (field.Kind == ActorStateScalarKind.Boolean && field.Int64Default != 0))
                    throw new InvalidOperationException("actor state default does not match scalar kind");
                cells[i] = new ActorStateCell(field);
            }
            actor.StateCells = cells;
        }
    }

    private static ActorStateCell CheckActorStateCell(
        TenantState tenant, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, ActorStateScalarKind kind, bool write)
    {
        // Both the loan check and the actual cell access take place inside
        // _gate, preventing a cancellation/restart between them.
        CheckStateLoan(tenant, actor, turn, loan, write);
        var cells = actor.StateCells;
        if (cells is null || index < 0 || index >= cells.Length)
            throw new InvalidOperationException("actor state field is not configured");
        var cell = cells[index];
        if (cell.Kind != kind)
            throw new InvalidOperationException("actor state field type mismatch");
        return cell;
    }

    public long ReadActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index)
    {
        lock (_gate) {
            var tenant = Check(handle);
            return CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, false).Int64Value;
        }
    }

    public void WriteActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, long value)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, true).Int64Value = value;
        }
    }

    /// <summary>One checked read/modify/write without an external TOCTOU gap.</summary>
    public long AddActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, long delta)
    {
        lock (_gate) {
            var tenant = Check(handle);
            var cell = CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, true);
            var next = checked(cell.Int64Value + delta);
            cell.Int64Value = next;
            return next;
        }
    }

    public bool ReadActorStateBoolean(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index)
    {
        lock (_gate) {
            var tenant = Check(handle);
            return CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Boolean, false).BooleanValue;
        }
    }

    public void WriteActorStateBoolean(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, bool value)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Boolean, true).BooleanValue = value;
        }
    }
}
