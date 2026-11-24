# Guest Result and checked arithmetic in .NET CIL

The normal `oresnet check` and `compile` entry points route standalone `.atto` sources with checked division or tagged guest constructs to verified structured CIL. The versioned preview and explicit host-interop profiles retain their own admission rules.

## Supported source and execution

- Signed `int / int` uses direct CIL and truncates toward zero. Zero divisors and the signed-minimum quotient overflow remain distinct. `Integer.rem` and `Integer.mod` use the checked numeric runtime.
- Closed `Option<int>` and `Result` shapes cross private static call boundaries. The Result shapes are `Result<int, Error>`, `Result<Option<int>, Error>`, `Result<int, String>`, and `Result<int, String | Error>`. `Ok` and `Err` are independently type-checked. Unwrapping an absent Option or an Err raises a guest panic.
- `trap` and `trap!` accept expressions or precede a callable declaration. Both evaluate the operand once in a generated static helper. Existing Results retain a single layer. The `String` error variant widens to `String | Error` at the caught-effect boundary without changing a preexisting Err payload.
- The trap region converts signed checked arithmetic overflow, division by zero, and a thrown built-in guest Error into Err. Ordinary `throw` and `throw!` preserve Error object identity. Panic and raise, including the bang spellings, escape the trap region.
- Postfix `?` in a callable with the same Result or Option return type returns the original failure value immediately. The successful path continues with the unwrapped payload.
- `match … over` and `do match … over` admit bool, `Option<int>`, and the closed Result shapes. The subject is evaluated once. Each pattern binds its typed payload before its optional `when` guard. A false guard advances to the next source arm. Unguarded patterns must cover the subject's full domain.
- Value match arms produce a common value with `return`, `^`, or an arrow expression. A `do match` arm may fall through or exit the arm with `return;`, `return void;`, or `^ void;`. Live outer bindings join after each arm. An Error bound by `Err` is the original caught object.

## Verification

`ci/test-guest-result-cil.sh` executes guests on CoreCLR and requires no CIL output for invalid source. `ci/test-guest-result-native-macos.sh` builds a native compiler and executes standalone Apple Silicon NativeAOT guests. The independent CFG verifier checks operand types, return shapes, dominance, and the exact static trap call target. The compiler tests inspect the emitted exception table and require the three deliberately scoped catch types.

An `Err(_)` pattern may discard the `String | Error` payload. Binding that union, general scalar and structural match patterns, additional generic Result layouts, custom nominal effect traits, suspension cleanup, and postfix propagation from inside a trap operand require further lowering. This scalar implementation does not wire actor or asynchronous continuations.
