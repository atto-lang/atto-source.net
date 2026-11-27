namespace Ores.Net.Compiler;

/// <summary>
/// Canonical .NET compiler metadata for source-level core/alloc identities.
/// This registry classifies identity and safety obligations; it does not grant
/// lowering to a backend path which has not independently implemented it.
/// </summary>
public static class CoreTypeRegistry
{
    public enum Scope { Core, Alloc, Explicit }
    public enum Kind { Type, Constructor, Trait }
    public enum Ownership { Copy, PayloadDependent, BorrowedView, Owned, Trait }
    public enum Allocation { None, BorrowedView, AllocatorRequired }
    public enum DropPolicy { None, ActivePayloadOnce, AllInitializedMembers, OwnedStorage, NotAValue }
    public enum SendPolicy { Always, PayloadDependent, NeverBorrowed, ProofRequired, NotApplicable }
    public enum Lowering
    {
        NetScalar,
        NetOptionI64Subset,
        NetFixedArrayI64Subset,
        LegacyStringView,
        ContractOnly
    }
    public enum Profile { Core, Alloc, Hosted }

    public sealed record TypeInfo(
        string Name,
        string CanonicalIdentity,
        Scope Scope,
        Kind Kind,
        int MinGenericArity,
        int MaxGenericArity,
        Ownership Ownership,
        Allocation Allocation,
        DropPolicy DropPolicy,
        SendPolicy SendPolicy,
        Lowering BackendLowering)
    {
        public bool AcceptsArity(int arity) =>
            arity >= MinGenericArity && arity <= MaxGenericArity;

        public bool IsExecutableInStructuredNet =>
            BackendLowering is Lowering.NetScalar or Lowering.NetOptionI64Subset
                or Lowering.NetFixedArrayI64Subset;
    }

    private static readonly IReadOnlyDictionary<string, TypeInfo> Entries = Build();

    private static IReadOnlyDictionary<string, TypeInfo> Build()
    {
        var entries = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);

        void Add(string name, string canonical, Scope scope, Kind kind,
            int minArity, int maxArity, Ownership ownership, Allocation allocation,
            DropPolicy drop, SendPolicy send, Lowering lowering)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(canonical) ||
                canonical.StartsWith("/", StringComparison.Ordinal) ||
                canonical.Contains("..", StringComparison.Ordinal) ||
                minArity < 0 || maxArity < minArity)
                throw new InvalidOperationException("Invalid .NET core registry entry: " + name);
            var info = new TypeInfo(name, canonical, scope, kind, minArity, maxArity,
                ownership, allocation, drop, send, lowering);
            if (kind == Kind.Trait &&
                (ownership != Ownership.Trait || drop != DropPolicy.NotAValue ||
                 send != SendPolicy.NotApplicable))
                throw new InvalidOperationException("Trait value metadata mismatch: " + name);
            if (ownership == Ownership.BorrowedView &&
                (allocation != Allocation.BorrowedView || drop != DropPolicy.None ||
                 send != SendPolicy.NeverBorrowed))
                throw new InvalidOperationException("Borrowed-view metadata mismatch: " + name);
            if (ownership == Ownership.Copy &&
                (allocation != Allocation.None || drop != DropPolicy.None ||
                 send != SendPolicy.Always))
                throw new InvalidOperationException("Copy metadata mismatch: " + name);
            if (ownership == Ownership.Owned && drop != DropPolicy.OwnedStorage)
                throw new InvalidOperationException("Owned storage must have deterministic drop: " + name);
            if (!entries.TryAdd(name, info))
                throw new InvalidOperationException("Duplicate .NET core registry identity: " + name);
        }

        void Scalar(string name, string canonical, Lowering lowering) =>
            Add(name, canonical, Scope.Core, Kind.Type, 0, 0,
                Ownership.Copy, Allocation.None, DropPolicy.None,
                SendPolicy.Always, lowering);

        // Current verified .NET scalar ABI. Uppercase aliases are contract
        // identities only until their syntax/lowering is independently enabled.
        Scalar("int", "core/primitive/i64", Lowering.NetScalar);
        Scalar("i64", "core/primitive/i64", Lowering.ContractOnly);
        Scalar("Int", "core/primitive/i64", Lowering.ContractOnly);
        Scalar("bool", "core/primitive/bool", Lowering.NetScalar);
        // Contextual compatibility alias: existing value identifiers stay valid.
        Scalar("boolean", "core/primitive/bool", Lowering.NetScalar);
        Scalar("Bool", "core/primitive/bool", Lowering.ContractOnly);
        Add("string", "core/primitive/string-view", Scope.Core, Kind.Type, 0, 0,
            Ownership.BorrowedView, Allocation.BorrowedView, DropPolicy.None,
            SendPolicy.NeverBorrowed, Lowering.LegacyStringView);

        // Closed NativeAOT Option<int> source subset. This does NOT claim
        // generic affine payload extraction, method-parameter ABI, or Result.
        Add("Option", "core/option/Option", Scope.Core, Kind.Type, 1, 1,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.ActivePayloadOnce,
            SendPolicy.PayloadDependent, Lowering.NetOptionI64Subset);
        Add("Some", "core/option/Some", Scope.Core, Kind.Constructor, 1, 1,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.ActivePayloadOnce,
            SendPolicy.PayloadDependent, Lowering.NetOptionI64Subset);
        Add("None", "core/option/None", Scope.Core, Kind.Constructor, 0, 0,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.None,
            SendPolicy.PayloadDependent, Lowering.NetOptionI64Subset);
        Add("Result", "core/result/Result", Scope.Core, Kind.Type, 2, 2,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.ActivePayloadOnce,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Ok", "core/result/Ok", Scope.Core, Kind.Constructor, 1, 1,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.ActivePayloadOnce,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Err", "core/result/Err", Scope.Core, Kind.Constructor, 1, 1,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.ActivePayloadOnce,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);

        // Fixed Array<int> is a val-only inline scalar component shape, not a
        // CLR array allocation. All initialized elements are destroyed.
        Add("Array", "core/array/Array", Scope.Core, Kind.Type, 1, 1,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.AllInitializedMembers,
            SendPolicy.PayloadDependent, Lowering.NetFixedArrayI64Subset);
        Add("FixedArray", "core/array/FixedArray", Scope.Core, Kind.Type, 2, 2,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.AllInitializedMembers,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Slice", "core/slice/Slice", Scope.Core, Kind.Type, 1, 1,
            Ownership.BorrowedView, Allocation.BorrowedView, DropPolicy.None,
            SendPolicy.NeverBorrowed, Lowering.ContractOnly);
        Add("Tuple", "core/tuple/Tuple", Scope.Core, Kind.Type, 0, 32,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.AllInitializedMembers,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Pair", "core/tuple/Pair", Scope.Core, Kind.Type, 2, 2,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.AllInitializedMembers,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Iterator", "core/iter/Iterator", Scope.Core, Kind.Trait, 1, 1,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Range", "core/range/Range", Scope.Core, Kind.Type, 1, 1,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.None,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Eq", "core/cmp/Eq", Scope.Core, Kind.Trait, 0, 0,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Ord", "core/cmp/Ord", Scope.Core, Kind.Trait, 0, 0,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Ordering", "core/cmp/Ordering", Scope.Core, Kind.Type, 0, 0,
            Ownership.Copy, Allocation.None, DropPolicy.None,
            SendPolicy.Always, Lowering.ContractOnly);
        Add("From", "core/convert/From", Scope.Core, Kind.Trait, 1, 1,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Into", "core/convert/Into", Scope.Core, Kind.Trait, 1, 1,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("TryFrom", "core/convert/TryFrom", Scope.Core, Kind.Trait, 1, 1,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("TryInto", "core/convert/TryInto", Scope.Core, Kind.Trait, 1, 1,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Display", "core/fmt/Display", Scope.Core, Kind.Trait, 0, 0,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Debug", "core/fmt/Debug", Scope.Core, Kind.Trait, 0, 0,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);
        Add("Sendable", "core/marker/Sendable", Scope.Core, Kind.Trait, 0, 0,
            Ownership.Trait, Allocation.None, DropPolicy.NotAValue,
            SendPolicy.NotApplicable, Lowering.ContractOnly);

        Add("Error", "core/error/Error", Scope.Core, Kind.Type, 0, 0,
            Ownership.Owned, Allocation.None, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("String", "alloc/string/String", Scope.Alloc, Kind.Type, 0, 0,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("List", "alloc/list/List", Scope.Alloc, Kind.Type, 1, 1,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Vec", "alloc/vec/Vec", Scope.Alloc, Kind.Type, 1, 1,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Map", "alloc/map/Map", Scope.Alloc, Kind.Type, 2, 2,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Set", "alloc/set/Set", Scope.Alloc, Kind.Type, 1, 1,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Box", "alloc/box/Box", Scope.Alloc, Kind.Type, 1, 1,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("HashMap", "alloc/hash_map/HashMap", Scope.Explicit, Kind.Type, 2, 2,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("HashSet", "alloc/hash_set/HashSet", Scope.Explicit, Kind.Type, 1, 1,
            Ownership.Owned, Allocation.AllocatorRequired, DropPolicy.OwnedStorage,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);
        Add("Entry", "alloc/map/Entry", Scope.Explicit, Kind.Type, 2, 2,
            Ownership.PayloadDependent, Allocation.None, DropPolicy.AllInitializedMembers,
            SendPolicy.PayloadDependent, Lowering.ContractOnly);

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, TypeInfo>(entries);
    }

    public static IReadOnlyDictionary<string, TypeInfo> All => Entries;

    public static TypeInfo? Lookup(string name) =>
        Entries.TryGetValue(name, out var info) ? info : null;

    public static TypeInfo Require(string name, int arity)
    {
        var info = Lookup(name) ?? throw new FormatException("E_CORE_TYPE_UNKNOWN: " + name);
        if (!info.AcceptsArity(arity))
            throw new FormatException($"E_CORE_TYPE_ARITY: {info.CanonicalIdentity} expects {info.MinGenericArity}..{info.MaxGenericArity} type arguments; received {arity}");
        return info;
    }

    public static TypeInfo RequireExecutableStructuredNet(string name, int arity)
    {
        var info = Require(name, arity);
        if (!info.IsExecutableInStructuredNet)
            throw new FormatException($"E_CORE_TYPE_LOWERING_UNSUPPORTED: {info.CanonicalIdentity} has no verified .NET structured/NativeAOT lowering");
        return info;
    }

    public static bool IsProtectedIdentity(string name) =>
        name != "boolean" && Entries.TryGetValue(name, out var info) &&
        (info.Kind == Kind.Constructor || info.Scope != Scope.Explicit);

    public static bool IsCoreConstructor(string name) =>
        Entries.TryGetValue(name, out var info) &&
        info.Scope == Scope.Core && info.Kind == Kind.Constructor;

    public static bool IsCoreSumCarrier(string name, int arity) =>
        Entries.TryGetValue(name, out var info) &&
        info.Scope == Scope.Core && info.Kind == Kind.Type &&
        info.Ownership == Ownership.PayloadDependent &&
        info.AcceptsArity(arity) && (name is "Option" or "Result");

    public static TypeInfo RequireProfile(string name, Profile profile)
    {
        var info = Lookup(name) ?? throw new FormatException("E_CORE_TYPE_UNKNOWN: " + name);
        if (info.Scope == Scope.Explicit)
            throw new FormatException($"E_CORE_TYPE_EXPLICIT_IMPORT_REQUIRED: {info.CanonicalIdentity}");
        if (info.Scope == Scope.Alloc && profile == Profile.Core)
            throw new FormatException($"E_ALLOC_PROFILE_REQUIRED: {info.CanonicalIdentity}");
        return info;
    }
}
