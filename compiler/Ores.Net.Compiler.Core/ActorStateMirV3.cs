using System.Collections.ObjectModel;

namespace Ores.Net.Compiler;

/// <summary>
/// Typed actor state MIR v3 shared between the verified external receive
/// emitter and the async main emitter. A compiler plan is not a guest
/// scheduler token: the actor runtime still mints real generation-fenced
/// read/write permits and releases them before mailbox publication.
/// </summary>
public static class ActorStateMirV3
{
    public enum Op
    {
        RequireActorTurn,
        AcquireWrite,
        LoadStateI64,
        LoadMailI64,
        CheckedAddI64,
        StoreStateI64,
        ReleaseWrite,
        AcquireRead,
        CopyStateI64,
        ReleaseRead,
        MailboxSendCopyI64,
        CheckStop,
        ReturnVoid
    }

    public enum LoanMode { Read, Write }
    public enum LoanAction { Borrow, Release }

    public readonly record struct SymbolicLoan(
        string Region, string Field, long LoanId, LoanMode Mode,
        LoanAction Action, bool GenerationFenced, bool CurrentTurn)
    {
        internal SymbolicLoan Release() =>
            this with { Action = LoanAction.Release };
    }

    private static readonly Op[] ReceiveSequence =
    [
        Op.RequireActorTurn, Op.AcquireWrite, Op.LoadStateI64,
        Op.LoadMailI64, Op.CheckedAddI64, Op.StoreStateI64,
        Op.ReleaseWrite, Op.AcquireRead, Op.CopyStateI64,
        Op.ReleaseRead, Op.MailboxSendCopyI64, Op.CheckStop, Op.ReturnVoid
    ];

    public sealed class Plan
    {
        internal Plan(string ns, string actor, string field,
            long initial, long stop, IReadOnlyList<long>? sourceMessages)
        {
            if (string.IsNullOrWhiteSpace(ns) ||
                string.IsNullOrWhiteSpace(actor) ||
                string.IsNullOrWhiteSpace(field) ||
                !ValidIdentifier(ns) || !ValidIdentifier(actor) ||
                !ValidIdentifier(field))
                throw new FormatException("ACTOR_MIR_V3: invalid source identity");
            Namespace = ns;
            ActorName = actor;
            StateField = field;
            InitialValue = initial;
            StopValue = stop;
            Instructions = Array.AsReadOnly((Op[])ReceiveSequence.Clone());

            string region = "state:" + ns + ":" + actor;
            var write = new SymbolicLoan(region, field, 1,
                LoanMode.Write, LoanAction.Borrow, true, true);
            var read = new SymbolicLoan(region, field, 2,
                LoanMode.Read, LoanAction.Borrow, true, true);
            Loans = Array.AsReadOnly(new[]
            {
                write, write.Release(), read, read.Release()
            });
            if (sourceMessages is null)
            {
                Messages = Array.Empty<long>();
            }
            else
            {
                if (sourceMessages.Count != 4 ||
                    sourceMessages[3] != StopValue ||
                    sourceMessages.Take(3).Any(x => x == StopValue))
                    throw new FormatException(
                        "ACTOR_MIR_V3: four sends and terminal STOP required");
                long sum = initial;
                foreach (long message in sourceMessages)
                {
                    try { sum = checked(sum + message); }
                    catch (OverflowException error)
                    {
                        throw new FormatException(
                            "ACTOR_MIR_V3: checked actor state overflow", error);
                    }
                }
                Messages = Array.AsReadOnly(sourceMessages.ToArray());
            }

            // The MIR is a single immutable canonical typed sequence.
            // No mailbox side effect can execute under the read loan.
            int acquire = Instructions.IndexOf(Op.AcquireRead);
            int release = Instructions.IndexOf(Op.ReleaseRead);
            int send = Instructions.IndexOf(Op.MailboxSendCopyI64);
            if (!(acquire < release && release < send) ||
                Loans.Count != 4 || Loans[0].Mode != LoanMode.Write ||
                Loans[1].Action != LoanAction.Release ||
                Loans[2].Mode != LoanMode.Read ||
                Loans[3].Action != LoanAction.Release)
                throw new InvalidOperationException(
                    "ACTOR_MIR_V3: unbalanced state loan or in-loan publication");
        }

        public string Namespace { get; }
        public string ActorName { get; }
        public string StateField { get; }
        public long InitialValue { get; }
        public long StopValue { get; }
        public IReadOnlyList<long> Messages { get; }
        public ReadOnlyCollection<Op> Instructions { get; }
        public ReadOnlyCollection<SymbolicLoan> Loans { get; }

        public bool ReadLoanReleasedBeforeMailboxSend() =>
            Instructions.IndexOf(Op.ReleaseRead) <
            Instructions.IndexOf(Op.MailboxSendCopyI64);
    }

    private static bool ValidIdentifier(string value) =>
        value.Length is > 0 and <= 128 &&
        (value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') &&
        value.Skip(1).All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    public static Plan Lower(VerifiedExternalCounterV2 declared) =>
        declared is null
            ? throw new ArgumentNullException(nameof(declared))
            : new Plan(declared.Namespace, declared.ActorName,
                declared.StateField, declared.InitialValue, declared.StopValue, null);

    public static Plan Lower(VerifiedCounterMainV2 main) =>
        main is null
            ? throw new ArgumentNullException(nameof(main))
            : new Plan(main.Namespace, main.ActorName, main.StateField,
                main.InitialValue, main.StopValue, main.Messages);
}
