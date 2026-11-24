# Guest Result and checked arithmetic in .NET CIL

The normal `oresnet check` and `compile` entry points route standalone `.atto` sources with checked division or tagged guest constructs to verified structured CIL. The versioned preview and explicit host-interop profiles retain their own admission rules.

## Supported source and execution

- Signed `int / int` uses direct CIL and truncates toward zero. Zero divisors and the signed-minimum quotient overflow remain distinct. `Integer.rem` and `Integer.mod` use the checked numeric runtime.
- Closed `Option<int>` and `Result` shapes cross private static call boundaries. The Result shapes are `Result<int, Error>`, `Result<Option<int>, Error>`, `Result<int, String>`, and `Result<int, String | Error>`. `Ok` and `Err` are independently type-checked. Unwrapping an absent Option or an Err raises a guest panic.
- `trap` and `trap!` accept expressions or precede a callable declaration. Both evaluate the operand once in a generated static helper. Existing Results retain a single layer. The `String` error variant widens to `String | Error` at the caught-effect boundary without changing a preexisting Err payload.
- The trap region converts signed checked arithmetic overflow, division by zero, and a thrown built-in guest Error into Err. Ordinary `throw` and `throw!` preserve Error object identity. Panic and raise, including the bang spellings, escape the trap region.
- Postfix `?` in a callable with the same Result or Option return type returns the original failure value immediately. The successful path continues with the unwrapped payload.

## Verification

`ci/test-guest-result-cil.sh` executes guests on CoreCLR and requires no CIL output for invalid source. `ci/test-guest-result-native-macos.sh` builds a native compiler and executes standalone Apple Silicon NativeAOT guests. The independent CFG verifier checks operand types, return shapes, dominance, and the exact static trap call target. The compiler tests inspect the emitted exception table and require the three deliberately scoped catch types.

Additional error unions, custom effect traits, generic Result layouts, match lowering, suspension cleanup, and postfix propagation from inside a trap operand require further lowering. This scalar implementation does not wire actor or asynchronous continuations.
