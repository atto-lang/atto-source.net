# AttoLang .NET host imports: closed-world Math.Abs v0

Status: **implemented narrow compiler slice, Native AOT execution pending stepful CI**.
This is **not** general CLR library imports.

```atto
import dotnet "System.Math" as Math;
pub fnc main() -> int { return Math.Abs(-42); }
```

The shipping scalar parser admits precisely one top-level `import dotnet`
of the type `System.Math` under a local identifier alias. The only admitted
method is the static `Abs(System.Int64) -> System.Int64` overload. All other
types, overloads, methods, instance construction, casts, dynamic invocation
and collection/object interfaces fail closed.

## Compiler implementation and signed AOT path

1. Scalar parser admits exact library and member names, with a bounded
   unescaped quoted literal and no dynamic assembly-name expressions.
2. A new AST node `DotnetMathAbsI64` lowers to a typed OresIR SSA operation.
3. `OresIrVerifier` checks exactly one prior i64 value and rejects forged
   right operands, constants and forward SSA references.
4. `CilEmitter` emits a direct `call` token to a `MemberRef` with exact
   `System.Math.Abs(Int64)` signature and the .NET 10
   `System.Runtime` assembly reference. No `Reflection.Emit`,
   `Assembly.Load`, JIT fallback, generic dispatch or reflection.
5. Existing `scripts/publish-linux.sh` statically links `OresGenerated.dll`
   into a Native AOT application, with no generated managed DLL sidecar.
6. A CI step checks the negative `System.IO.File` import produces no DLL
   and requires the positive guest's native executable to output `42`.

The host-call boundary is presently **scalar i64 only**, with no references
crossing actor heap ownership boundaries. The single allowlisted method is
pure and does not grant filesystem, network, process or reflection access.
It can still throw on `Abs(long.MinValue)`; general checked exceptions and
guest effect typing need additional acceptance.

## Required before production and general host imports

- Stepful exact-head .NET tests, independent CIL metadata inspection, Linux
  Native AOT execution and equivalent Windows native execution.
- Full CLR reference-assembly lookup with pinned SDK and transitive assembly
  identities, exact generic/overload resolution and static trimming roots.
- Compile-time authorization for `interop.dotnet` plus per-member capability
  and effect classification. No host-type authority inherited by guest modules.
- Borrow-checked owned handles; actor-local lifecycle, cancellation and
  generation fencing; no arbitrary mutable host values sent over channels.
- Explicit managed allocation/GC accounting. Foreign CLR libraries cannot
  be assumed zero-GC; strict no-GC profiles must reject unproven imports.
- Preserve platform-portable core/std as canonical `.atto` source and
  existing `.ores` compatibility; host imports belong to backend adapters.

Check `verification/Ores.Net.Tests/DotnetHostImportTests.cs`,
`examples/hostinterop-math-abs.atto` and the strict Linux Native AOT workflow.
No job with `steps=[]` or `steps=null` is execution evidence.
