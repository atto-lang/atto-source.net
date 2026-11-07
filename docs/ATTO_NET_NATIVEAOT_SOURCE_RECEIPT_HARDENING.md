# NativeAOT canonical AttoLang module provenance hardening

The same host project previously published two different generated CIL graphs in a single build workspace, both returning 42. A stale NativeAOT build could therefore look plausible. CI now validates every compiler-reported .atto source SHA-256 and byte length, verifies four canonical source identities and graph SHA, pins the generated CIL digest and requires the canonical and legacy CIL builds to differ. A `dotnet clean` runs before each NativeAOT publish, and the receipts record graph, CIL, native executable and exact source IDs.

This is a proposed executable CI gate, not a completed test. A zero-step GitHub job means runner admission failure. The compiler-owned `atto.core` namespace is still not implemented in this .NET path.
