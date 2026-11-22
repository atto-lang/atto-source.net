using Ores.Net.Compiler;

internal static class CoreTypeRegistryTests
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Core type registry regression: " + reason);
    }

    private static void Reject(string source, string expected, string label)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException error) when
            (error.Message.Contains(expected, StringComparison.Ordinal))
        {
            Require(error.Message.Contains("offset", StringComparison.Ordinal),
                label + ": diagnostic must preserve source offset");
            return;
        }
        throw new Exception("Expected " + expected + " for " + label);
    }

    public static void Run()
    {
        var all = CoreTypeRegistry.All;
        Require(all.Count == all.Keys.Distinct(StringComparer.Ordinal).Count(),
            "registry names must be unique");
        foreach (var entry in all.Values)
        {
            Require(!string.IsNullOrWhiteSpace(entry.Name), "blank source name");
            Require(!string.IsNullOrWhiteSpace(entry.CanonicalIdentity), "blank canonical identity");
            Require(!entry.CanonicalIdentity.StartsWith("/", StringComparison.Ordinal),
                "absolute canonical identity");
            Require(!entry.CanonicalIdentity.Contains("..", StringComparison.Ordinal),
                "parent path in canonical identity");
            Require(entry.AcceptsArity(entry.MinGenericArity), "minimum generic arity rejected");
            Require(entry.MaxGenericArity >= entry.MinGenericArity, "inverted generic arity");
            if (entry.Kind == CoreTypeRegistry.Kind.Trait)
            {
                Require(entry.Ownership == CoreTypeRegistry.Ownership.Trait, entry.Name + " trait ownership");
                Require(entry.DropPolicy == CoreTypeRegistry.DropPolicy.NotAValue, entry.Name + " trait drop");
            }
            if (entry.Ownership == CoreTypeRegistry.Ownership.BorrowedView)
            {
                Require(entry.Allocation == CoreTypeRegistry.Allocation.BorrowedView, entry.Name + " view allocation");
                Require(entry.DropPolicy == CoreTypeRegistry.DropPolicy.None, entry.Name + " view drop");
                Require(entry.SendPolicy == CoreTypeRegistry.SendPolicy.NeverBorrowed, entry.Name + " view Send");
            }
            if (entry.Ownership == CoreTypeRegistry.Ownership.Copy)
            {
                Require(entry.Allocation == CoreTypeRegistry.Allocation.None, entry.Name + " Copy allocation");
                Require(entry.DropPolicy == CoreTypeRegistry.DropPolicy.None, entry.Name + " Copy drop");
                Require(entry.SendPolicy == CoreTypeRegistry.SendPolicy.Always, entry.Name + " Copy Send");
            }
            if (entry.Ownership == CoreTypeRegistry.Ownership.Owned)
                Require(entry.DropPolicy == CoreTypeRegistry.DropPolicy.OwnedStorage,
                    entry.Name + " owned drop");
        }

        var option = CoreTypeRegistry.Lookup("Option")!;
        Require(option.CanonicalIdentity == "core/option/Option", "Option canonical identity");
        Require(option.MinGenericArity == 1 && option.MaxGenericArity == 1, "Option generic arity");
        Require(CoreTypeRegistry.Lookup("Array")!.MinGenericArity == 1 &&
            CoreTypeRegistry.Lookup("Array")!.MaxGenericArity == 1, "Array fixed generic arity");
        Require(option.Ownership == CoreTypeRegistry.Ownership.PayloadDependent, "Option payload ownership");
        Require(option.DropPolicy == CoreTypeRegistry.DropPolicy.ActivePayloadOnce, "Option active drop");
        Require(option.BackendLowering == CoreTypeRegistry.Lowering.NetOptionI64Subset, "Option lowering subset");
        var result = CoreTypeRegistry.Lookup("Result")!;
        Require(result.CanonicalIdentity == "core/result/Result", "Result canonical identity");
        Require(result.MinGenericArity == 2 && result.MaxGenericArity == 2, "Result generic arity");
        Require(result.BackendLowering == CoreTypeRegistry.Lowering.ContractOnly, "Result must stay unlowered");
        Require(result.DropPolicy == CoreTypeRegistry.DropPolicy.ActivePayloadOnce, "Result future active drop");

        Require(CoreTypeRegistry.Lookup("Some")!.Kind == CoreTypeRegistry.Kind.Constructor,
            "Some constructor identity");
        Require(CoreTypeRegistry.Lookup("None")!.Kind == CoreTypeRegistry.Kind.Constructor,
            "None constructor identity");
        Require(CoreTypeRegistry.Lookup("None")!.DropPolicy == CoreTypeRegistry.DropPolicy.None,
            "None carries no payload");
        Require(CoreTypeRegistry.Lookup("Ok")!.BackendLowering == CoreTypeRegistry.Lowering.ContractOnly,
            "Ok must not claim .NET lowering");
        Require(CoreTypeRegistry.Lookup("Err")!.BackendLowering == CoreTypeRegistry.Lowering.ContractOnly,
            "Err must not claim .NET lowering");

        foreach (var name in new[] { "Array", "FixedArray", "Tuple", "Pair", "Entry" })
            Require(CoreTypeRegistry.Lookup(name)!.DropPolicy ==
                CoreTypeRegistry.DropPolicy.AllInitializedMembers, name + " aggregate drop");

        var slice = CoreTypeRegistry.Lookup("Slice")!;
        Require(slice.Ownership == CoreTypeRegistry.Ownership.BorrowedView, "Slice view ownership");
        Require(slice.Allocation == CoreTypeRegistry.Allocation.BorrowedView, "Slice view allocation");
        var list = CoreTypeRegistry.Lookup("List")!;
        Require(list.Scope == CoreTypeRegistry.Scope.Alloc, "List alloc scope");
        Require(list.Allocation == CoreTypeRegistry.Allocation.AllocatorRequired, "List allocator");
        Require(list.BackendLowering == CoreTypeRegistry.Lowering.ContractOnly, "List not lowered");
        Require(CoreTypeRegistry.Lookup("HashMap")!.Scope == CoreTypeRegistry.Scope.Explicit,
            "HashMap explicit import");
        Require(CoreTypeRegistry.Lookup("String")!.Scope == CoreTypeRegistry.Scope.Alloc,
            "Owned String is alloc-only");
        Require(CoreTypeRegistry.Lookup("String")!.Ownership == CoreTypeRegistry.Ownership.Owned,
            "Owned String ownership");
        Require(CoreTypeRegistry.Lookup("string")!.CanonicalIdentity ==
            "core/primitive/string-view", "legacy lowercase string view identity");
        Require(CoreTypeRegistry.Lookup("Object") is null, "Object must not be a universal base type");

        Require(CoreTypeRegistry.IsCoreSumCarrier("Option", 1), "Option carrier");
        Require(CoreTypeRegistry.IsCoreSumCarrier("Result", 2), "Result carrier contract");
        Require(!CoreTypeRegistry.IsCoreSumCarrier("Option", 2), "Option bad arity");
        Require(!CoreTypeRegistry.IsCoreSumCarrier("Result", 1), "Result bad arity");
        Require(CoreTypeRegistry.IsProtectedIdentity("Option"), "Option protected");
        Require(CoreTypeRegistry.IsProtectedIdentity("Some"), "Some protected");
        Require(CoreTypeRegistry.IsProtectedIdentity("int"), "scalar identity protected");
        Require(!CoreTypeRegistry.IsProtectedIdentity("HashMap"),
            "explicit-only types are not automatically reserved");
        Require(CoreTypeRegistry.Require("Option", 1).CanonicalIdentity == option.CanonicalIdentity,
            "canonical Require");
        bool optionArityRejected = false;
        try { _ = CoreTypeRegistry.Require("Option", 2); }
        catch (FormatException error) when
            (error.Message.Contains("E_CORE_TYPE_ARITY", StringComparison.Ordinal))
        { optionArityRejected = true; }
        Require(optionArityRejected,
            "CoreTypeRegistry.Require must reject malformed Option arity");
        bool coreAllocRejected = false;
        try { _ = CoreTypeRegistry.RequireProfile("List", CoreTypeRegistry.Profile.Core); }
        catch (FormatException error) when
            (error.Message.Contains("E_ALLOC_PROFILE_REQUIRED", StringComparison.Ordinal))
        { coreAllocRejected = true; }
        Require(coreAllocRejected, "core-only profile must not admit List");
        Require(CoreTypeRegistry.RequireProfile("List", CoreTypeRegistry.Profile.Alloc).Name == "List",
            "alloc profile exposes registry List name without claiming lowering");

        Require(OresStructuredParser.Parse(
            "fnc main():int{return Some(42).unwrap_or(0);}") is not null,
            "verified Option<int> subset remains admitted");
        Require(OresStructuredParser.Parse(
            "fnc main():int{val Array<int> values=Array.of(40,2);return values.get_copy(0).unwrap_or(0)+2;}") is not null,
            "verified fixed Array<int> Option lookup remains admitted");

        Reject("fnc main():int{val Option<int, bool> value=None;return 42;}",
            "E_CORE_TYPE_ARITY", "Option generic arity is registry-backed");
        Reject("fnc main():int{val Option<> value=None;return 42;}",
            "E_CORE_TYPE_ARITY", "Option requires an explicit type argument");
        Reject("fnc main():int{val Option<Box> value=None;return 42;}",
            "E_OPTION_I64_ONLY", "affine payload Option is fail-closed until typed drop/borrow lowering");
        Reject("fnc main():int{val Result<int, int> value=Ok(42);return 42;}",
            "E_CORE_TYPE_LOWERING_UNSUPPORTED", "Result is catalogued but not lowered");
        Reject("fnc main():int{return Ok(42);}",
            "E_CORE_TYPE_LOWERING_UNSUPPORTED", "Ok constructor is not lowered");
        Reject("fnc main():int{return Err(42);}",
            "E_CORE_TYPE_LOWERING_UNSUPPORTED", "Err constructor is not lowered");
        Reject("fnc Option():int{return 42;}",
            "E_PRELUDE_SPOOF", "compiler-owned core identity cannot be forged");
        Reject("fnc main():int{val List<int> values=List();return 42;}",
            "E_CORE_TYPE_LOWERING_UNSUPPORTED", "allocator collections stay fail closed");
    }
}
